---
name: ai-provider-adapters
description: Integrate hosted third-party AI APIs from .NET using typed HTTP clients, normalized outputs and durable asynchronous jobs. No Python or local inference.
---

# Third-party AI Provider Adapters (.NET)

1. Read `docs/AI_PROVIDER_CONTRACT.md`, the active phase and provider's official docs.
2. Identify actual capability support, output formats, auth method, synchronous/async jobs, polling/webhook and rate/cost limits. Never invent an endpoint.
3. Create provider-independent C# interfaces and DTO mapping; preserve model/provenance and unknown values.
4. Use `IHttpClientFactory`, typed clients, cancellation tokens, retry/backoff/429 handling, structured logs with redaction and API keys server-side.
5. Persist job state before remote submission; protect against duplicate paid jobs and support restart recovery.
6. Safely fetch output assets; verify type/size and checksum; prefer GLB with explicit converter for alternatives.
7. Test mocked HTTP responses including failures, missing capability and bad output; run opt-in live smoke tests when configured.

Return actual verification results and precise unresolved vendor-contract assumptions.
