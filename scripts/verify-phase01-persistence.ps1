$ErrorActionPreference = 'Stop'

$apiExe = Join-Path $PSScriptRoot '..\src\RoomForge.Api\bin\Debug\net10.0\RoomForge.Api.exe'
if (-not (Test-Path -LiteralPath $apiExe)) { throw 'Build the solution before running this script.' }
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousAssetRoot = $env:ROOMFORGE_ASSET_ROOT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ROOMFORGE_ASSET_ROOT = Join-Path (Resolve-Path (Join-Path $PSScriptRoot '..')).Path 'data\assets'
$port = Get-Random -Minimum 54000 -Maximum 59000
$baseUrl = "http://127.0.0.1:$port"
$server = $null

function Start-RoomForgeApi {
    $script:server = Start-Process -FilePath $apiExe -ArgumentList @('--urls', $baseUrl) -PassThru -WindowStyle Hidden
    for ($attempt = 0; $attempt -lt 40; $attempt++) {
        if ($script:server.HasExited) { throw 'RoomForge API exited before becoming ready. Check local User Secrets and database permissions.' }
        try {
            $database = Invoke-RestMethod "$baseUrl/health/database" -TimeoutSec 2
            if ($database.database -ne 'roomforge') { throw 'API connected to a database other than roomforge.' }
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'RoomForge API did not connect to roomforge on local PostgreSQL port 5432.'
}

function Stop-RoomForgeApi {
    if ($script:server -and -not $script:server.HasExited) {
        Stop-Process -Id $script:server.Id -Force
        $script:server.WaitForExit()
    }
}

try {
    $provisioner = Start-Process -FilePath $apiExe -ArgumentList '--ensure-roomforge-database' -PassThru -Wait -WindowStyle Hidden
    if ($provisioner.ExitCode -ne 0) { throw 'Database provisioning failed. Configure RoomForgeAdmin User Secrets for the local postgres maintenance database.' }

    Start-RoomForgeApi
    $fixture = Invoke-RestMethod "$baseUrl/api/sample-scene"
    $sceneId = $fixture.sceneId
    try { $scene = Invoke-RestMethod "$baseUrl/api/scenes/$sceneId"; $revision = [int]$scene.revision }
    catch { if ($_.Exception.Response.StatusCode.value__ -eq 404) { $scene = $fixture; $revision = 0 } else { throw } }

    $floor = $scene.objects | ForEach-Object { $_.surfaces } | Where-Object semanticRole -eq 'floor' | Select-Object -First 1
    if (-not $floor) { throw 'Sample fixture has no selectable floor surface.' }
    $nextMaterial = $scene.materials | Where-Object id -ne $floor.materialId | Select-Object -First 1
    if (-not $nextMaterial) { throw 'Sample fixture needs at least two materials for persistence verification.' }
    $floor.materialId = $nextMaterial.id
    $objectSnapshot = ConvertTo-Json -Compress -Depth 100 -InputObject @{
        objects = @($scene.objects | ForEach-Object {
            @{ id = $_.id; position = $_.position; rotation = $_.rotation; dimensions = $_.dimensions; visible = $_.visible; surfaces = $_.surfaces }
        })
        deletedObjects = @($scene.deletedObjects)
    }
    $surfaceIdsBefore = @($scene.objects | ForEach-Object { $_.surfaces } | ForEach-Object id | Sort-Object)
    $pbrMaterial = $scene.materials | Where-Object { $_.maps.baseColor } | Select-Object -First 1
    if (-not $pbrMaterial) { throw 'Scene has no uploaded base-color texture for restart persistence verification.' }
    $pbrSnapshot = ConvertTo-Json -Compress -Depth 20 -InputObject @{
        maps = $pbrMaterial.maps
        tileSizeMeters = $pbrMaterial.tileSizeMeters
        roughness = $pbrMaterial.roughness
        metalness = $pbrMaterial.metalness
    }
    $uvSnapshot = ConvertTo-Json -Compress -Depth 10 -InputObject $floor.uv
    $saved = Invoke-RestMethod "$baseUrl/api/scenes/$sceneId" -Method Put -ContentType 'application/json' -Headers @{ 'If-Match' = "`"$revision`"" } -Body ($scene | ConvertTo-Json -Depth 100 -Compress)
    if ($saved.revision -ne ($revision + 1)) { throw 'The save did not increment the scene revision.' }
    Stop-RoomForgeApi

    Start-RoomForgeApi
    $reloaded = Invoke-RestMethod "$baseUrl/api/scenes/$sceneId"
    $floorAfterRestart = $reloaded.objects | ForEach-Object { $_.surfaces } | Where-Object semanticRole -eq 'floor' | Select-Object -First 1
    $surfaceIdsAfter = @($reloaded.objects | ForEach-Object { $_.surfaces } | ForEach-Object id | Sort-Object)
    if ($reloaded.revision -ne $saved.revision -or $floorAfterRestart.materialId -ne $nextMaterial.id) { throw 'Scene data did not persist across API restart.' }
    if (($surfaceIdsBefore -join ',') -ne ($surfaceIdsAfter -join ',')) { throw 'Surface IDs changed across API restart.' }
    $objectSnapshotAfterRestart = ConvertTo-Json -Compress -Depth 100 -InputObject @{
        objects = @($reloaded.objects | ForEach-Object {
            @{ id = $_.id; position = $_.position; rotation = $_.rotation; dimensions = $_.dimensions; visible = $_.visible; surfaces = $_.surfaces }
        })
        deletedObjects = @($reloaded.deletedObjects)
    }
    if ($objectSnapshot -ne $objectSnapshotAfterRestart) { throw 'Object transforms, visibility, assignments, UV settings, or archived objects did not survive API restart.' }
    $pbrAfterRestart = $reloaded.materials | Where-Object id -eq $pbrMaterial.id | Select-Object -First 1
    $pbrAfterSnapshot = ConvertTo-Json -Compress -Depth 20 -InputObject @{
        maps = $pbrAfterRestart.maps
        tileSizeMeters = $pbrAfterRestart.tileSizeMeters
        roughness = $pbrAfterRestart.roughness
        metalness = $pbrAfterRestart.metalness
    }
    if ($pbrSnapshot -ne $pbrAfterSnapshot) { throw 'PBR material definition did not survive API restart.' }
    if ((ConvertTo-Json -Compress -Depth 10 -InputObject $floorAfterRestart.uv) -ne $uvSnapshot) { throw 'Surface UV settings did not survive API restart.' }
    foreach ($textureUrl in $pbrAfterRestart.maps.PSObject.Properties.Value) {
        $texture = Invoke-WebRequest "$baseUrl$textureUrl" -TimeoutSec 5
        if ($texture.StatusCode -ne 200 -or $texture.Headers['Content-Type'] -notlike 'image/*') { throw 'Uploaded texture asset did not survive API restart.' }
    }

    Write-Output "PASS: roomforge PostgreSQL scene revision $($saved.revision), floor assignment, object transforms/visibility/archive, stable ObjectIds/SurfaceIds, surface assignments/UV, PBR definition, and uploaded texture maps survived API restart."
}
finally {
    Stop-RoomForgeApi
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue }
    else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
    if ($null -eq $previousAssetRoot) { Remove-Item Env:ROOMFORGE_ASSET_ROOT -ErrorAction SilentlyContinue }
    else { $env:ROOMFORGE_ASSET_ROOT = $previousAssetRoot }
}
