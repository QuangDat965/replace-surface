# Phase 04 — ASP.NET Core durable AI jobs (NO PYTHON)

Read `AGENTS.md`, `MASTER_PROMPT.md`, `docs/AI_PROVIDER_CONTRACT.md` and current code. Implement a working .NET-only backend pipeline.

Scope:
1. EF Core `ProcessingJob` persisted in PostgreSQL with idempotency, external job ID, lease, attempts, stage, timestamps and status.
2. `BackgroundService` host to claim and process work durably; crash/restart recovery; graceful shutdown. API returns 202, never waits for remote inference.
3. Configurable provider registry and DI interfaces for image analysis, segmentation/depth/layout and image-to-3D. Use `IHttpClientFactory` typed client scaffolding.
4. Deterministic Mock remote provider that submits/polls/completes and emits normalized reference observations, clearly marked mock.
5. REST job creation/status, typed failure, cancellation/retry and frontend job progress/error view.
6. Tests for restart durability, duplicate/idempotent submission, polling, cancellation, rate limit and malformed provider results.

Do NOT add a Python service, real inference model, GPU runtime, fake third-party endpoints, or a message broker unless needed and justified. Implement one end-to-end mock job; document limitations and run tests. End with concrete verification and phase 05 handoff.
