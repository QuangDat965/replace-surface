# Local RoomForge operations

This runbook covers the first personal-machine deployment only. The API is configured for PostgreSQL on `localhost:5432`, database `roomforge`; it rejects a different database. Never substitute `banhang` or point restore commands at it. Do not add Docker, SQLite or local model inference.

## Configuration and startup

- Keep `ConnectionStrings:RoomForge` and `FAL_KEY` in .NET User Secrets or the process environment. Do not paste connection strings into tickets, terminal transcripts or this document.
- For paid fal processing, explicitly set the selected provider and review the provider/account data-transfer terms first. Current documented toggles are `Reconstruction:SegmentationProvider=fal-sam3` or `Reconstruction:ImageTo3DProvider=fal-hunyuan3d`; Hunyuan also requires `Reconstruction:FalHunyuanDataTransferApproved=true`. This toggle is an operator attestation, not a substitute for a privacy/legal review.
- Ensure PostgreSQL is running locally, then start the API and web app using `workflows/DEVELOPMENT_WORKFLOW.md`. Verify `/health/database` returns `roomforge` before creating data. Startup runs the configured EF migrations; inspect logs for migration failures and do not start the UI against a failed database.
- Asset files are stored under `ROOMFORGE_ASSET_ROOT` when configured, otherwise `src/RoomForge.Api/data/assets` (relative to the API content root). Back up this directory with the database.

## Spend and provider safety

The shared PostgreSQL reservation ledger enforces an aggregate hard limit of $20 USD. SAM reserves $0.01 and Hunyuan geometry reserves $0.50 before submission; ambiguous or rejected requests remain charged in the conservative reservation total. Check the persisted ledger and provider invoice before authorizing additional live smoke tests. Fixture tests use no credentials and incur no provider spend. Do not send customer images until account/model retention and third-party transfer applicability are confirmed.

## Coordinated backup

Schedule downtime so no API worker is processing a job or writing an asset. Stop the API first; copy the configured asset root, then make a PostgreSQL custom-format backup. Keep both artifacts in access-controlled storage. Use a PostgreSQL password prompt or a protected `PGPASSFILE`; never put a password in the command line.

```powershell
$backupDir = Join-Path $PWD 'backups\roomforge'
$assetRoot = $env:ROOMFORGE_ASSET_ROOT
if ([string]::IsNullOrWhiteSpace($assetRoot)) { $assetRoot = Join-Path $PWD 'src\RoomForge.Api\data\assets' }
New-Item -ItemType Directory -Force -Path $backupDir | Out-Null
pg_dump --host localhost --port 5432 --username <local-postgres-user> --password --dbname roomforge --format custom --file (Join-Path $backupDir 'roomforge.dump')
Copy-Item -LiteralPath $assetRoot -Destination (Join-Path $backupDir 'assets') -Recurse
```

If the API uses a different content root, update `$assetRoot` to its actual `data\assets` directory before copying; do not copy an assumed relative directory from a different working directory. Record the dump timestamp and asset root together. Since jobs may refer to assets, a database-only backup is incomplete.

## Restore drill

Restore only into the dedicated local `roomforge` database while the API is stopped. First create a separate disposable PostgreSQL database for a restore drill, inspect the dump contents, and restore there; do not use production or another application database. `pg_restore --clean` drops objects in its target, so never run it with an unverified target name. Restore the matching asset snapshot to the configured asset root, then start the API and verify `/health/database`, sample project/scene reads, image/texture reads and the migration state. Confirm at least one source image, one scene and a referenced asset before considering the recovery usable.

The repository does not yet automate a backup/restore drill or asset/database consistency check. Record the drill date, dump identifier and verification outcomes here or in the operator's private run log; do not place credentials or customer image data in the repository.

## Current limitations

- No automated metrics endpoint, retention-based unreferenced-asset cleanup, or scheduled backup exists yet.
- The $20 ledger tracks estimated provider reservations, not a provider's final invoice or tax/credit adjustments.
- Object-mesh apply/reload has not yet been exercised through an API restart with a provider-generated mesh.
- GLB export currently rejects non-box geometry and mapped texture assets; see `EXPORT_CONTRACT.md`.
