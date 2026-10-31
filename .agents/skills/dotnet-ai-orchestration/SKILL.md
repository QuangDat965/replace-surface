---
name: dotnet-ai-orchestration
description: Design durable .NET-only AI orchestration and BackgroundService job processing for remote AI API tasks.
---

# .NET AI Orchestration

1. `ProcessingJob` in PostgreSQL is canonical for job state, submitted external task IDs and idempotency.
2. API accepts/uploads then returns HTTP 202 with a job ID; inference runs after response in a .NET BackgroundService.
3. Claim jobs atomically with leases; renew/expire/recover leases. Use bounded concurrency and configurable provider quotas.
4. Prevent duplicate vendor charges: persist submission intent, use provider idempotency when offered, reconcile ambiguous timeouts before retrying.
5. Poll when callbacks unavailable; verify webhook signatures/replay prevention where supported.
6. Download assets safely, normalize provider output and allow manual review before SceneDocument mutation.
7. Tests: persistence restart, duplicate submit, slow provider, 429, timeout, malformed result, cancellation and crash recovery.

No Python, FastAPI, Torch, CUDA or arbitrary local model execution.
