# Agent rules: RoomForge AI (.NET + external AI APIs)

Read `docs/ROOMFORGE_MASTER_BLUEPRINT.md`, `docs/PRODUCT.md`, `docs/ARCHITECTURE.md`, `docs/AI_PROVIDER_CONTRACT.md`, `docs/SCENE_CONTRACT.md`, `docs/IMPLEMENTATION_PLAN.md`, and the active phase prompt.

## Repository map and boundaries
- `apps/web`: React UI and Three.js rendering. No provider secrets or vendor calls.
- `src/RoomForge.Api`: HTTP boundary and OpenAPI.
- `src/RoomForge.Application`: use cases and provider-neutral orchestration.
- `src/RoomForge.Domain`: semantic scene invariants; no EF or vendor DTOs.
- `src/RoomForge.Infrastructure`: EF/PostgreSQL, assets, provider HTTP adapters.
- `tests`: contract, unit, integration, and UI checks; `examples`: deterministic fixtures.
- `docs/IMPLEMENTATION_PLAN.md` defines phase gates; `docs/PROGRESS.md` records witnessed progress.

## Fixed technical decisions
- No Python, FastAPI, PyTorch, CUDA, local model hosting or GPU worker in this repository.
- .NET 10 hosts API, provider orchestration, background jobs and third-party AI HTTP clients; external AI providers perform inference.
- Provider adapters are replaceable, capability-driven and tested with recorded/synthetic fixture responses.
- Do not guess provider endpoints or authentication; require real docs before implementing production integration.
- All spatial dimensions in meters; Y-up, XZ floor. Do not claim exact dimensions from one image without measurements.
- Versioned semantic Scene JSON is canonical; GLBs are meshes/assets and export. Stable ObjectId and SurfaceId UUIDs.
- Individual surfaces support PBR materials with physical size, tiling and UV rotation.
- Material definitions remain separate from per-surface materialId assignments. Texture maps reference server-owned, size-limited raster assets; do not trust upload filenames or filesystem paths.
- Persist processing jobs and external job IDs. Never block a UI request on long AI inference.
- Phase 01 uses the existing local PostgreSQL instance on port 5432 and a dedicated `roomforge` database; never access or alter `banhang`. Do not add SQLite or require Docker.
- Read database credentials from .NET User Secrets or environment variables. Never hardcode them, print them, or commit them.
- Support manual-first reconstruction, mock mode and primitive fallback without vendor keys.
- API provides revision/ETag conflict checks, safe upload/download handling and server-side secrets.

## Coding workflow
- State a short scoped plan; implement one vertical slice at a time.
- Use `IHttpClientFactory`, DI, explicit mapping between vendor DTOs and internal DTOs, typed cancellation, structured logs, retry policies and sensible timeouts.
- Inspect API provider response contracts before writing adapters; make unsupported capabilities explicit.
- UI buttons must work; use integration and UI tests; show status/error/provider/provenance clearly.
- Save credentials only in development user-secrets or environment/vault, never repo/logs.
- Compare assumptions vs observations, and record geometry/AI uncertainties.
- Run builds and relevant tests before reporting completion.

## Verify after scaffolding (adjust actual solution paths)
- `npm --prefix apps/web install`
- `npm --prefix apps/web run build`
- `npm --prefix apps/web run test`
- `dotnet build src/RoomForge.sln`
- `dotnet test src/RoomForge.sln`
- `docker compose up -d postgres minio` (only when included in project)

See `workflows/DEVELOPMENT_WORKFLOW.md`.

## Skills, roles, and definition of done
- Load a relevant `.agents/skills/*/SKILL.md` only when its task applies. Role files in `.github/agents/` are guidance; they do not imply that parallel agents are available or authorized.
- Follow the matching `workflows/*.md` for repeatable work. The active phase and the user's request control scope.
- A phase is done only when its acceptance criteria work through the intended interface, builds and relevant tests pass, contracts/docs are current, and `docs/PROGRESS.md` records commands, outcomes, limitations, and next prerequisites.
- Keep C# nullable and TypeScript strict. Name measurements with meter units; keep provider JSON in Infrastructure. Avoid unused abstractions and placeholder UI controls.
