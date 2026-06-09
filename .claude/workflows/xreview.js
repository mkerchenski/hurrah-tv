export const meta = {
  name: 'xreview',
  description: 'Hurrah.tv multi-agent code review — fan out specialized reviewers over a diff, score each finding 0-100, return confirmed findings (score >= 50)',
  phases: [
    { title: 'Review', detail: 'specialized reviewers read the diff in parallel' },
    { title: 'Score', detail: 'score each finding 0-100, keep >= 50' },
  ],
}

// args can arrive as a JSON string rather than a parsed object — parse defensively
const input = typeof args === 'string' ? JSON.parse(args) : (args ?? {})
const { diffPath, filesPath, claudeMdPath, learningsIndex, dimensions, issueContext } = input

// fail loud on missing/misparsed args — never silently expand scope.
// dimensions is REQUIRED and has no implicit "run everything" default: a dropped
// arg must surface as an error, not quietly review all 4 lenses over the wrong scope.
if (!diffPath || !filesPath || !claudeMdPath) {
  throw new Error('xreview workflow: diffPath, filesPath, and claudeMdPath are all required (claudeMdPath is read unconditionally by every reviewer)')
}
if (!Array.isArray(dimensions) || dimensions.length === 0) {
  throw new Error('xreview workflow: dimensions must be a non-empty array — the caller picks the subset (quick vs review); there is no implicit run-everything default')
}

const FINDINGS_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    findings: {
      type: 'array',
      items: {
        type: 'object',
        additionalProperties: false,
        properties: {
          file: { type: 'string', description: 'path:line, e.g. Pages/Home.razor:512' },
          issue: { type: 'string', description: 'one-line summary of the problem' },
          severity: { type: 'string', enum: ['critical', 'high', 'medium', 'low'] },
          explanation: { type: 'string', description: 'why it is a problem and the suggested fix' },
        },
        required: ['file', 'issue', 'severity', 'explanation'],
      },
    },
  },
  required: ['findings'],
}

const SCORE_SCHEMA = {
  type: 'object',
  additionalProperties: false,
  properties: {
    score: { type: 'integer', minimum: 0, maximum: 100 },
    rationale: { type: 'string', description: 'one line: why this score' },
  },
  required: ['score', 'rationale'],
}

const DIMENSIONS = {
  claudemd: {
    label: 'claude-md',
    prompt: `Review for violations of the project's CLAUDE.md conventions.

Key patterns to check:
- Code style: 4-space indentation, no XML doc comments, lowercase comments, Type over var
- No unnecessary abstractions, no over-engineering — three similar lines is better than premature abstraction
- Comments only where logic isn't self-explanatory; don't explain WHAT (well-named identifiers do that)
- Pre-computed counts: no \`Count()\` per render inside a loop
- Self-gating predicates over caller-supplied visibility flags
- Mutating endpoints return the updated entity
- Background work uses IServiceScopeFactory, not captured request-scoped services

Report ONLY violations clearly present in the diff. Do not flag pre-existing code.`,
  },
  blazor: {
    label: 'blazor',
    prompt: `Review for Blazor WebAssembly issues.

Concerns:
- Component lifecycle: OnInitializedAsync vs OnParametersSetAsync usage
- Disposal: event handlers, timers, and singleton subscriptions unsubscribed in Dispose
- StateHasChanged: called correctly, not from background threads without InvokeAsync
- DI scope: Singleton consuming Scoped fails the DI validator (e.g. MediaFilterService -> ApiClient)
- Two-way binding pitfalls
- HttpClient usage in WASM — auth handler attached, base address set
- Fire-and-forget — backgrounded tasks captured via _ = ... so they're not awaited synchronously
- Optimistic UI: mutate local state before API confirm; revert on failure
- Skeleton placeholders: any new async-loaded section should reserve its final shape`,
  },
  apidata: {
    label: 'api-data',
    prompt: `Review for API and data safety issues.

Concerns:
- SQL injection: raw string concatenation in Dapper queries (use parameterized only)
- Postgres bigint -> int32 mapping: COUNT(*), SUM() etc. need ::int cast or DTO must use long
- Missing input validation on endpoints (TmdbId > 0, MediaTypes.IsValid, enum bounds)
- Authentication: every user-facing endpoint has RequireAuthorization() or is intentionally AllowAnonymous
- Authorization policies: Admin policy is DB-backed (re-check per request), not just JWT-claim
- CORS configuration not weakened
- TMDb / Anthropic / Twilio API keys not exposed to the client
- Schema migrations: idempotent (IF NOT EXISTS), safe on existing data, backfill considered
- Background TmdbService work: fresh DI scope, no captured request services
- Error handling: bad-request validation returns 400 with explanation, not 500

Report ONLY confirmed or highly-likely issues. No theoretical concerns.`,
  },
  bugs: {
    label: 'bugs',
    prompt: `Review for bugs, logic errors, and regressions.

Concerns:
- Null reference exceptions
- Off-by-one errors, incorrect loop bounds
- Async/await: missing await, fire-and-forget tasks that should be awaited, deadlocks
- Exception handling: swallowing exceptions, catching too broadly
- Logic inversions, negation errors
- Edge cases: empty collections, zero values, boundaries, network failures
- Regressions: does the change break existing behavior on Home / Queue / Details?
- Resource management: IDisposable not disposed, connections leaked
- JSON serialization mismatches between Shared DTO and how Dapper / API actually populate it
- DateTime UTC vs local mismatches (see Learnings/wasm-datetime-source-matters.md)

Ignore: version numbers, csproj metadata, cosmetic changes.
Report ONLY issues with real impact.`,
  },
}

const unknown = dimensions.filter(d => !DIMENSIONS[d])
if (unknown.length) {
  throw new Error(`xreview workflow: unknown dimension(s): ${unknown.join(', ')}. Known: ${Object.keys(DIMENSIONS).join(', ')}`)
}

const commonPrefix = `You are a code reviewer for the Hurrah.tv repo. Review ONLY the changes in the diff.

Diff: read the file at ${diffPath}
Changed files: read the file at ${filesPath}
CLAUDE.md conventions: read the file at ${claudeMdPath}
Read the full source of any changed file where the diff alone is insufficient.
${issueContext ? `\nLinked issue acceptance criteria (treat as a hard checklist for the diff; a \`closes #NN\` that doesn't satisfy these is a high-severity finding):\n${issueContext}\n` : ''}
Relevant learnings — title index below. Read ONLY the 2-5 entries whose titles relate
to your specialty (via Read on the listed Learnings/<name>.md path). Do NOT read all of
them — that is the dominant cost. If a learning conflicts with the diff, that's a finding.
${learningsIndex || '(no learnings index supplied)'}

Report ONLY issues introduced by the diff, never pre-existing concerns. For each finding
give {file (path:line), issue, severity, explanation}. If you find nothing, return an
empty findings array.

Your specialty:
`

// pipeline (not a barrier): each dimension's findings start scoring the moment that
// dimension finishes, while other dimensions are still reviewing.
const results = await pipeline(
  dimensions,
  d => agent(commonPrefix + DIMENSIONS[d].prompt, {
    label: `review:${DIMENSIONS[d].label}`,
    phase: 'Review',
    model: 'sonnet',
    schema: FINDINGS_SCHEMA,
  }),
  (review, d) => parallel((review?.findings ?? []).map(f => () =>
    agent(
      `Score this code-review finding 0-100 for whether it is real and worth acting on.

Finding: ${JSON.stringify(f)}
Diff: read ${diffPath} (and the changed file if needed) to verify.

Rubric:
- 0   = false positive, doesn't hold up, or pre-existing (not introduced by the diff)
- 25  = might be real but unverified; stylistic and not in CLAUDE.md
- 50  = verified real but minor; nitpick relative to the change
- 75  = verified, important — directly impacts functionality or violates CLAUDE.md
- 100 = definitely real, confirmed with evidence, will happen in practice`,
      { label: `score:${DIMENSIONS[d].label}`, phase: 'Score', model: 'haiku', schema: SCORE_SCHEMA },
    // a dead scorer (agent() -> null) must not silently drop the finding: surface it
    // at the >= 50 threshold with a clear rationale so it's reviewed, not lost.
    ).then(s => ({
      ...f,
      dimension: DIMENSIONS[d].label,
      score: s?.score ?? 50,
      scoreRationale: s?.rationale ?? 'scorer unavailable — surfaced unscored at threshold',
    }))
  )),
)

const confirmed = results
  .flat()
  .filter(Boolean)
  .filter(f => f.score >= 50)
  .sort((a, b) => b.score - a.score)

log(`xreview: ${confirmed.length} finding(s) at score >= 50 across [${dimensions.join(', ')}]`)
return { confirmed, reviewed: dimensions }
