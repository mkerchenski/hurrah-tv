# Security Policy

Thanks for helping keep Hurrah.tv and its users safe.

## Reporting a vulnerability

**Please do NOT open a public issue for security vulnerabilities.** Public issues are indexed, searchable, and visible to attackers before a fix is in place.

Instead, use **GitHub's Private Vulnerability Reporting**:

👉 [Report a vulnerability privately](https://github.com/mkerchenski/hurrah-tv/security/advisories/new)

This gives us a private channel to discuss the issue, develop a fix, and coordinate disclosure. You'll get a response within a few days.

If for some reason GitHub's channel isn't an option, contact the repo owner directly via the email on [@mkerchenski's GitHub profile](https://github.com/mkerchenski).

## What to include in a report

- A clear description of the vulnerability
- Steps to reproduce (proof of concept is helpful but not required)
- The impact — what an attacker could do with this
- Your suggestion for a fix, if you have one
- Whether you want credit in the eventual advisory (we're happy to credit; say so explicitly if that's your preference)

## What we commit to

- **Acknowledge your report within 7 days** — even if that acknowledgement is "we need more information"
- **Keep you updated** on our progress toward a fix
- **Coordinate disclosure** — publishing an advisory only after a fix is live, giving you credit (if you want it)
- **Not threaten or pursue legal action** against anyone who reports in good faith

## Scope

This policy covers the Hurrah.tv codebase and the live deployment at hurrah.tv.

**In scope:**
- The Blazor WebAssembly client
- The .NET Minimal API backend
- The PostgreSQL schema and data handling
- Authentication (phone OTP + JWT)
- Dependencies (we'll investigate; fix may need upstream coordination)

**Out of scope:**
- Social engineering against the maintainers
- Physical attacks
- Denial-of-service attacks that merely require volume
- Vulnerabilities in third-party services (TMDb, Twilio, Anthropic) — please report those to their vendors directly

## Thank you

This is a small passion project, not a funded program — so we can't offer bounties. But thoughtful, responsible disclosure is genuinely appreciated, and we'll credit you in the advisory if you'd like.
