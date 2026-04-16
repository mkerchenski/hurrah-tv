# Anthropic SDK v12 Patterns (.NET)

> **Area:** API | AI
> **Date:** 2026-03-29

## Context
Integrated Claude Haiku into HurrahTv.Api for content curation and per-show match scoring. Hit several compile errors from incorrect SDK assumptions.

## Learning
The Anthropic .NET SDK v12.0.1 has non-obvious patterns:

**Reading response text** — don't use `OfType<TextBlock>()`:
```csharp
// BAD — CA2021 warning, always returns empty (ContentBlock != TextBlock)
string text = response.Content.OfType<TextBlock>().FirstOrDefault()?.Text ?? "";

// GOOD — use TryPickText
if (response.Content[0].TryPickText(out TextBlock? textBlock))
    text = textBlock.Text;
```

**Token counts are `long`, not `int`** — cast explicitly:
```csharp
int inputTokens = (int)response.Usage.InputTokens;
int outputTokens = (int)response.Usage.OutputTokens;
```

**Prompts with JSON examples** — use `$$"""` (double-dollar raw string) so `{` and `}` in JSON are literal, and `{{variable}}` is interpolation:
```csharp
string prompt = $$"""
    User: {{userName}}
    Respond with JSON: {"match":"strong","reason":"..."}
    """;
```
Single `$"""` treats every `{` as interpolation start, causing CS9006 errors.

**Client instantiation** — create once, not per-call:
```csharp
// constructor (SDK 12.16.0+ — property renamed from APIKey to ApiKey)
_client = new AnthropicClient { ApiKey = apiKey };

// per-call — just use _client
Message response = await _client.Messages.Create(new MessageCreateParams { ... });
```

> **SDK 12.0.1 → 12.16.0 breaking change:** the property on `AnthropicClient` was renamed from `APIKey` (all-caps) to `ApiKey` (PascalCase). Pure rename — no behavior difference. If you're upgrading from an older version, grep for `APIKey` and replace.
