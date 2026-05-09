# Worktale Free AI Setup

This API uses OpenAI-compatible free providers for changelog narrative generation.

## Required Configuration

Set these environment variables (or equivalent app settings) in every environment:

- `Worktale__IngestApiKey` (required for protected endpoints)
- `AI_PROVIDER` (for example: `openrouter`)
- `AI_BASE_URL` (for example: `https://openrouter.ai/api/v1`)
- `AI_MODEL` (for example: `meta-llama/llama-3.3-8b-instruct:free`)
- `AI_API_KEY` (required for key-based providers)
- `AI_FALLBACKS` (comma-separated provider names, for example: `groq,mistral,cerebras`)
- `AI_RATE_LIMIT_PER_MINUTE` (local per-provider request budget, default `24`)
- `AI_RATE_LIMIT_WINDOW_SECONDS` (budget window size, default `60`)

Provider-specific fallback keys (optional unless provider is used):

- `OPENROUTER_API_KEY`
- `GROQ_API_KEY`
- `MISTRAL_API_KEY`
- `CEREBRAS_API_KEY`

## Endpoints

- `POST /api/worktale/ingest` (protected)
- `POST /api/changelog/milestone` (protected)
- `GET /api/changelog` (public)
- `GET /api/changelog/{id}` (public)
- `GET /api/worktale/ai/health` (protected)

Health check parameters:

- `simulatePrimaryFailure=true` skips the first provider to verify fallback routing.

## Runtime Behavior

- Commit ingest stores payload first, then queues AI generation.
- AI generation uses provider chain: primary then `AI_FALLBACKS`.
- Retries apply for `429`, timeout, and transient `5xx` responses.
- Local request budget is enforced per provider before outbound calls.
- If all providers fail, the API writes a deterministic fallback narrative.

## Free-Tier Notes

- Free tiers and limits change frequently.
- Verify each provider's latest free limits before production rollout.
- Keep all secrets in environment variables or user secrets, never in source control.
