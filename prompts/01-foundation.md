# Phase 01 — Viewer and PostgreSQL persistence

Follow `AGENTS.md`, `docs/IMPLEMENTATION_PLAN.md`, `docs/SCENE_CONTRACT.md`, `docs/ARCHITECTURE.md`, and the relevant repository skills/workflows. Implement only Phase 01.

Use the existing local PostgreSQL server on port 5432. Create a separate database named `roomforge` only if it does not exist. Never connect to, query, modify, or migrate the `banhang` database. Do not add SQLite or require Docker. Keep credentials in .NET User Secrets or environment variables; do not put them in source, logs, or committed files.

Configure EF Core and Npgsql, create and apply migrations, and implement scene CRUD with optimistic revision checks. Render the authored `examples/sample-scene.json` using React Three Fiber. Support orbit/zoom, raycast selection by SurfaceId, per-surface sample material assignment, save and reload. The API must validate that its app connection points to local port 5432 and database `roomforge` before running migrations.

Acceptance: select floor, change only its material, save, reload, and retain stable object/surface IDs. Restart the API and verify the persisted Scene JSON still has the saved material and revision. Run focused tests and report actual evidence. No AI integration.
