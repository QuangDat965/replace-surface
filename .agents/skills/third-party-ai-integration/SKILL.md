---
name: third-party-ai-integration
description: Add a documented external AI capability through a .NET adapter and durable provider job.
---

# External AI integration

Use `docs/AI_PROVIDER_CONTRACT.md`, `ai-provider-adapters`, and `dotnet-ai-orchestration`. Read official provider docs before production integration; map actual capabilities and unsupported cases. Keep vendor payloads in Infrastructure, secrets server-side, and jobs durable. Label mock output mock. Test recorded or synthetic responses, errors, retries, and ambiguous submissions without making paid requests in CI.
