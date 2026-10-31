# RoomForge AI

Phase 01 provides an editable Three.js sample room and a .NET API backed by local PostgreSQL. It supports surface selection, sample material assignment, revision checked scene saves, and reload. The sample is authored data, not a photo reconstruction. See [progress](docs/PROGRESS.md) and the [phase plan](docs/IMPLEMENTATION_PLAN.md).

## Requirements

.NET SDK 10, Node 24+, npm, and the existing local PostgreSQL server on port 5432. Phase 01 does not require Docker or SQLite.

## Configure local credentials

Set `ConnectionStrings:RoomForge` to a connection string for local port 5432 and database `roomforge`; the existing `ConnectionStrings:RoomForgeDatabase` key is also accepted. Set `ConnectionStrings:RoomForgeAdmin` to the same local PostgreSQL role with database `postgres` for provisioning. If omitted, the API derives a maintenance connection by changing the configured RoomForge connection database to `postgres`. You can instead use `ConnectionStrings__RoomForge` / `ConnectionStrings__RoomForgeAdmin` or `ROOMFORGE_ADMIN_CONNECTION` environment variables. Never commit or paste the values into source or chat.

```powershell
dotnet user-secrets set "ConnectionStrings:RoomForge" "<your private local connection string for roomforge>" --project src/RoomForge.Api
dotnet user-secrets set "ConnectionStrings:RoomForgeAdmin" "<your private local connection string for postgres>" --project src/RoomForge.Api
dotnet run --project src/RoomForge.Api -- --ensure-roomforge-database
dotnet tool restore
dotnet ef database update --project src/RoomForge.Infrastructure --startup-project src/RoomForge.Api
```

Replace the placeholders locally. Provisioning accepts only the maintenance database `postgres` on local port 5432 and creates only `roomforge` when it is absent. API startup and EF tooling accept only `roomforge` on local port 5432; both reject other database names before connecting. Neither path connects to `banhang`. Pending migrations also apply when the API starts.

## Run the editor

```powershell
npm --prefix apps/web install
dotnet run --project src/RoomForge.Api --urls http://localhost:5080
npm --prefix apps/web run dev
```

Open `http://localhost:5173`. Select a surface, choose or create a material, upload PNG/JPEG/WebP PBR maps (up to 10 MB each), set tile width/height in meters and surface UV transforms, then save. Texture files are stored under `data/assets` (or `ROOMFORGE_ASSET_ROOT`) while Scene JSON stores their server-owned URLs. Back up the asset folder with the database. Without database credentials, the API still serves health and the authored fixture; persistent scene routes report that credentials are not configured.

## Verify

```powershell
dotnet build src/RoomForge.slnx
dotnet test src/RoomForge.slnx
npm --prefix apps/web run build
npm --prefix apps/web run test
npm --prefix apps/web run lint
npx playwright install chromium
npm --prefix apps/web run test:e2e
./scripts/verify-phase01-persistence.ps1
```

The Playwright and persistence checks require local User Secrets or environment credentials. The persistence script provisions only `roomforge`, saves a changed floor material, restarts the API, and verifies the scene revision and SurfaceIds survived.

The canonical source is versioned semantic Scene JSON, with meters, Y-up and an XZ floor. GLB is an asset/export format. Read [AGENTS.md](AGENTS.md) and the [master blueprint](docs/ROOMFORGE_MASTER_BLUEPRINT.md) before changing code.
