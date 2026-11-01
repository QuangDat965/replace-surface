$ErrorActionPreference = 'Stop'

$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$apiExe = Join-Path $repoRoot 'src\RoomForge.Api\bin\Debug\net10.0\RoomForge.Api.exe'
if (-not (Test-Path -LiteralPath $apiExe)) { throw 'Build the solution before running this verifier.' }
$previousEnvironment = $env:ASPNETCORE_ENVIRONMENT
$previousAssetRoot = $env:ROOMFORGE_ASSET_ROOT
$env:ASPNETCORE_ENVIRONMENT = 'Development'
$env:ROOMFORGE_ASSET_ROOT = Join-Path $repoRoot 'data\assets'
$port = Get-Random -Minimum 54000 -Maximum 59000
$baseUrl = "http://127.0.0.1:$port"
$server = $null
[System.Reflection.Assembly]::LoadWithPartialName('System.Net.Http') | Out-Null
$http = [System.Net.Http.HttpClient]::new()

function Start-RoomForgeApi {
    $script:server = Start-Process -FilePath $apiExe -ArgumentList @('--urls', $baseUrl) -WorkingDirectory $repoRoot -PassThru -WindowStyle Hidden
    for ($attempt = 0; $attempt -lt 60; $attempt++) {
        if ($script:server.HasExited) { throw 'RoomForge API exited before becoming ready. Check local User Secrets.' }
        try {
            $database = Invoke-RestMethod "$baseUrl/health/database" -TimeoutSec 2
            if ($database.database -ne 'roomforge') { throw 'API connected to a database other than roomforge.' }
            return
        } catch { Start-Sleep -Milliseconds 500 }
    }
    throw 'RoomForge API did not connect to the local roomforge database.'
}

function Stop-RoomForgeApi {
    if ($script:server -and -not $script:server.HasExited) {
        Stop-Process -Id $script:server.Id -Force
        $script:server.WaitForExit()
    }
}

function Get-SceneObject($document, [string]$id) {
    return $document.objects | Where-Object { $_.id -eq $id } | Select-Object -First 1
}

try {
    Start-RoomForgeApi
    $project = Invoke-RestMethod "$baseUrl/api/projects" -Method Post -ContentType 'application/json' -Body '{"name":"Reconstruction persistence verification"}'

    $png = [Convert]::FromBase64String('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==')
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $imageContent = [System.Net.Http.ByteArrayContent]::new($png)
    $imageContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/png')
    $form.Add($imageContent, 'image', 'room.png')
    $uploadResponse = $http.PostAsync("$baseUrl/api/projects/$($project.id)/images", $form).GetAwaiter().GetResult()
    if (-not $uploadResponse.IsSuccessStatusCode) { throw "Room image upload failed with HTTP $([int]$uploadResponse.StatusCode)." }
    $uploaded = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json

    $sample = Invoke-RestMethod "$baseUrl/api/sample-scene"
    $sample.sceneId = [Guid]::NewGuid().ToString()
    $sample.projectId = $project.id
    $sample.revision = 0
    $sceneUrl = "$baseUrl/api/scenes/$($sample.sceneId)"
    $savedScene = Invoke-RestMethod $sceneUrl -Method Put -ContentType 'application/json' -Headers @{ 'If-Match' = '"0"' } -Body ($sample | ConvertTo-Json -Depth 100 -Compress)
    $before = $savedScene
    $chair = $before.objects | Where-Object { $_.category -eq 'furniture' } | Select-Object -First 1
    $chairSurfaceSnapshot = ConvertTo-Json -Compress -Depth 40 -InputObject $chair.surfaces
    $objectIdsBefore = @($before.objects | ForEach-Object { $_.id } | Sort-Object)

    $idempotencyKey = "verify-$([Guid]::NewGuid().ToString('N'))"
    $jobRequest = @{ imageId = $uploaded.imageId; idempotencyKey = $idempotencyKey } | ConvertTo-Json -Compress
    $job = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/reconstruction-jobs" -Method Post -ContentType 'application/json' -Body $jobRequest
    $duplicate = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/reconstruction-jobs" -Method Post -ContentType 'application/json' -Body $jobRequest
    if ($duplicate.id -ne $job.id) { throw 'Repeated idempotency key created a second job.' }
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $job = Invoke-RestMethod "$baseUrl/api/reconstruction/jobs/$($job.id)"
        if ($job.status -in @('succeeded', 'failed', 'cancelled')) { break }
        Start-Sleep -Milliseconds 250
    }
    if ($job.status -ne 'succeeded' -or -not $job.analysisJson) { throw "Reconstruction job did not succeed (status: $($job.status))." }

    $analysis = $job.analysisJson | ConvertFrom-Json
    $candidate = $analysis.objects | Select-Object -First 1
    $candidate.linkedObjectId = $chair.id
    $candidate.dimensionsMeters = @(0.73, 0.9, 0.55)
    $floorDetection = $analysis.surfaces | Where-Object { $_.semanticRole -eq 'floor' } | Select-Object -First 1
    $floor = $before.objects | ForEach-Object { $_.surfaces } | Where-Object { $_.semanticRole -eq 'floor' } | Select-Object -First 1
    $floorDetection.linkedSurfaceId = $floor.id
    $previewBody = @{ targetSceneId = $before.sceneId; expectedRevision = $before.revision; analysis = $analysis } | ConvertTo-Json -Depth 100 -Compress
    try { $preview = Invoke-RestMethod "$baseUrl/api/reconstruction/jobs/$($job.id)/preview" -Method Post -ContentType 'application/json' -Body $previewBody }
    catch {
        $response = $_.Exception.Response
        $reader = [System.IO.StreamReader]::new($response.GetResponseStream())
        $detail = $reader.ReadToEnd()
        throw "Reconstruction preview failed with HTTP $([int]$response.StatusCode): $detail"
    }
    $storedBeforeApply = Invoke-RestMethod $sceneUrl
    if ($storedBeforeApply.revision -ne $before.revision) { throw 'Preview changed the stored Scene revision before explicit confirmation.' }
    $previewDocument = $preview.document | ConvertFrom-Json
    $previewChair = Get-SceneObject $previewDocument $chair.id
    if (-not $previewChair -or $previewChair.dimensions[0] -ne 0.73) { throw 'Preview did not apply the reviewed dimensions to the linked stable ObjectId.' }
    if ((ConvertTo-Json -Compress -Depth 40 -InputObject $previewChair.surfaces) -ne $chairSurfaceSnapshot) { throw 'Preview changed the linked object SurfaceIds, materials or UV settings.' }
    if ((@($previewDocument.objects | ForEach-Object { $_.id } | Sort-Object) -contains $objectIdsBefore[0]) -eq $false) { throw 'Preview dropped existing ObjectIds.' }

    try {
        Invoke-RestMethod "$baseUrl/api/reconstruction/jobs/$($job.id)/apply" -Method Post -ContentType 'application/json' -Body (@{ confirmed = $false; expectedRevision = $preview.baseRevision } | ConvertTo-Json -Compress) | Out-Null
        throw 'API accepted apply without explicit confirmation.'
    } catch { if ($_.Exception.Message -eq 'API accepted apply without explicit confirmation.') { throw } }

    $applied = Invoke-RestMethod "$baseUrl/api/reconstruction/jobs/$($job.id)/apply" -Method Post -ContentType 'application/json' -Body (@{ confirmed = $true; expectedRevision = $preview.baseRevision } | ConvertTo-Json -Compress)
    if ($applied.revision -ne ($before.revision + 1)) { throw 'Explicit apply did not create the next Scene revision.' }
    Stop-RoomForgeApi

    Start-RoomForgeApi
    $persistedJob = Invoke-RestMethod "$baseUrl/api/reconstruction/jobs/$($job.id)"
    $persistedScene = Invoke-RestMethod $sceneUrl
    $persistedChair = Get-SceneObject $persistedScene $chair.id
    if ($persistedJob.status -ne 'succeeded' -or -not $persistedJob.analysisJson) { throw 'Job status/result did not survive API restart.' }
    if ($persistedScene.revision -ne $applied.revision -or -not $persistedChair -or $persistedChair.dimensions[0] -ne 0.73) { throw 'Applied Scene did not survive API restart.' }
    if ((ConvertTo-Json -Compress -Depth 40 -InputObject $persistedChair.surfaces) -ne $chairSurfaceSnapshot) { throw 'Surface identity/material/UV did not survive API restart.' }
    $persistedImageResponse = $http.GetAsync("$baseUrl$($uploaded.url)").GetAwaiter().GetResult()
    if (-not $persistedImageResponse.IsSuccessStatusCode) { throw 'Uploaded image bytes were not available after API restart.' }

    Write-Output "PASS: local roomforge image asset, idempotent reconstruction job/result, preview non-mutation, explicit apply, stable ObjectId/SurfaceIds, dimensions, and surface material/UV data persisted after API restart (scene revision $($persistedScene.revision))."
}
finally {
    Stop-RoomForgeApi
    $http.Dispose()
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
    if ($null -eq $previousAssetRoot) { Remove-Item Env:ROOMFORGE_ASSET_ROOT -ErrorAction SilentlyContinue } else { $env:ROOMFORGE_ASSET_ROOT = $previousAssetRoot }
}
