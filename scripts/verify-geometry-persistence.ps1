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

try {
    Start-RoomForgeApi
    $project = Invoke-RestMethod "$baseUrl/api/projects" -Method Post -ContentType 'application/json' -Body '{"name":"Geometry restart verification"}'
    $photoPath = Join-Path $repoRoot 'examples\interior-reference.jpg'
    $photoBytes = [System.IO.File]::ReadAllBytes($photoPath)
    $form = [System.Net.Http.MultipartFormDataContent]::new()
    $imageContent = [System.Net.Http.ByteArrayContent]::new($photoBytes)
    $imageContent.Headers.ContentType = [System.Net.Http.Headers.MediaTypeHeaderValue]::Parse('image/jpeg')
    $form.Add($imageContent, 'image', 'interior-reference.jpg')
    $http = [System.Net.Http.HttpClient]::new()
    $uploadResponse = $http.PostAsync("$baseUrl/api/projects/$($project.id)/images", $form).GetAwaiter().GetResult()
    if (-not $uploadResponse.IsSuccessStatusCode) { throw "Image upload failed with HTTP $([int]$uploadResponse.StatusCode)." }
    $uploaded = $uploadResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json

    $annotation = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations" -Method Post -ContentType 'application/json' -Body (@{ sourceImageId = $uploaded.imageId } | ConvertTo-Json -Compress)
    $imageWidth = [double]$uploaded.width
    $imageHeight = [double]$uploaded.height
    $floorId = [Guid]::NewGuid().ToString()
    $wallId = [Guid]::NewGuid().ToString()
    $spanId = [Guid]::NewGuid().ToString()
    $heightSpanId = [Guid]::NewGuid().ToString()
    $floorPoints = @(@{ x = $imageWidth * .05; y = $imageHeight * .52 }, @{ x = $imageWidth * .95; y = $imageHeight * .52 }, @{ x = $imageWidth * .95; y = $imageHeight * .95 }, @{ x = $imageWidth * .05; y = $imageHeight * .95 })
    $wallPoints = @(@{ x = $imageWidth * .05; y = $imageHeight * .05 }, @{ x = $imageWidth * .95; y = $imageHeight * .05 }, @{ x = $imageWidth * .95; y = $imageHeight * .52 }, @{ x = $imageWidth * .05; y = $imageHeight * .52 })
    $floorRegion = @{ id = $floorId; semanticRole = 'floor'; polygon = @{ points = $floorPoints }; provenance = 'user_supplied'; confidence = $null }
    $wallRegion = @{ id = $wallId; semanticRole = 'wall'; polygon = @{ points = $wallPoints }; provenance = 'user_supplied'; confidence = $null }
    $floorSpan = @{ id = $spanId; start = @{ x = $imageWidth * .10; y = $imageHeight * .51 }; end = @{ x = $imageWidth * .90; y = $imageHeight * .51 }; knownMeters = 2.4; provenance = 'user_supplied' }
    $heightSpan = @{ id = $heightSpanId; start = @{ x = $imageWidth * .50; y = $imageHeight * .10 }; end = @{ x = $imageWidth * .50; y = $imageHeight * .90 }; knownMeters = 2.7; provenance = 'user_supplied' }
    $annotationBody = @{ regions = @($floorRegion, $wallRegion); referenceSpans = @($floorSpan, $heightSpan); cameraHint = @{ focalLengthPixels = $null; principalPoint = $null }; notes = @() } | ConvertTo-Json -Depth 20 -Compress
    $annotation = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations/$($annotation.annotationId)" -Method Put -ContentType 'application/json' -Headers @{ 'If-Match' = '"1"' } -Body $annotationBody
    $topology = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations/$($annotation.annotationId)/topology" -Method Post
    $edgeCorrections = @($topology.edges | Where-Object { $_.regionId -eq $floorId } | ForEach-Object { @{ edgeId = $_.id; role = 'floor_wall' } })
    $topology = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/topologies/$($topology.id)" -Method Patch -ContentType 'application/json' -Body (@{ expectedRevision = $topology.revision; edges = $edgeCorrections } | ConvertTo-Json -Depth 20 -Compress)

    $sample = Invoke-RestMethod "$baseUrl/api/sample-scene"
    $sample.sceneId = [Guid]::NewGuid().ToString()
    $sample.projectId = $project.id
    $sample.revision = 0
    $sceneUrl = "$baseUrl/api/scenes/$($sample.sceneId)"
    $scene = Invoke-RestMethod $sceneUrl -Method Put -ContentType 'application/json' -Headers @{ 'If-Match' = '"0"' } -Body ($sample | ConvertTo-Json -Depth 100 -Compress)
    $geometry = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations/$($annotation.annotationId)/geometry" -Method Post -ContentType 'application/json' -Body (@{ topologyId = $topology.id; floorRegionId = $floorId; calibrationSpanId = $spanId } | ConvertTo-Json -Compress)
    $floorInstance = $geometry.geometrySpec.instances | Where-Object { $_.name -eq 'Reconstructed floor' } | Select-Object -First 1
    $overrides = @()
    foreach ($instance in $geometry.geometrySpec.instances) {
        for ($index = 0; $index -lt $instance.dimensions.Count; $index++) {
            if ($instance.dimensions[$index].status -eq 'unknown') { $overrides += @{ instanceId = $instance.stableId; dimensionIndex = $index; meters = 0.12 } }
        }
    }
    if ($overrides.Count -ne 5) { throw "Expected unknown floor/wall thicknesses to remain explicit; found $($overrides.Count)." }
    $candidateRequest = @{
        topologyId = $topology.id; sceneId = $scene.sceneId; expectedSceneRevision = $scene.revision
        floorRegionId = $floorId; calibrationSpanId = $spanId
        dimensionOverrides = $overrides
    } | ConvertTo-Json -Depth 20 -Compress
    $candidate = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations/$($annotation.annotationId)/geometry-candidates" -Method Post -ContentType 'application/json' -Body $candidateRequest
    $applyBody = @{ confirmed = $true; expectedRevision = $scene.revision } | ConvertTo-Json -Compress
    $applied = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/geometry-candidates/$($candidate.candidateId)/apply" -Method Post -ContentType 'application/json' -Body $applyBody

    $sceneDocument = $applied.document | ConvertFrom-Json
    $floor = $sceneDocument.objects | Where-Object { $_.id -eq $floorInstance.stableId } | Select-Object -First 1
    if (-not $floor -or -not $floor.surfaces -or -not $floor.surfaces[0].materialId) { throw 'Applied geometry did not create a floor with a stable material assignment.' }
    $floor.dimensions[0] = 2.6
    $materialId = ($sceneDocument.materials | Where-Object { $_.id -ne $floor.surfaces[0].materialId } | Select-Object -First 1).id
    if (-not $materialId) { throw 'Sample Scene must provide a second material for independent surface verification.' }
    $floor.surfaces[0].materialId = $materialId
    $sceneDocument.revision = $applied.revision
    $saved = Invoke-RestMethod $sceneUrl -Method Put -ContentType 'application/json' -Headers @{ 'If-Match' = "`"$($applied.revision)`"" } -Body ($sceneDocument | ConvertTo-Json -Depth 100 -Compress)

    Stop-RoomForgeApi
    Start-RoomForgeApi
    $persistedAnnotation = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/annotations/$($annotation.annotationId)"
    $persistedTopology = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/topologies/$($topology.id)"
    $persistedCandidate = Invoke-RestMethod "$baseUrl/api/projects/$($project.id)/geometry-candidates/$($candidate.candidateId)"
    $persistedScene = Invoke-RestMethod $sceneUrl
    $persistedFloor = $persistedScene.objects | Where-Object { $_.id -eq $floorInstance.stableId } | Select-Object -First 1
    if ($persistedAnnotation.revision -ne 2 -or $persistedTopology.revision -ne 2) { throw 'Annotation/topology revisions did not persist after API restart.' }
    if ($persistedCandidate.baseSceneRevision -ne $scene.revision) { throw 'Append-only geometry candidate did not persist after API restart.' }
    if ($persistedScene.revision -ne $saved.revision -or -not $persistedFloor -or $persistedFloor.dimensions[0] -ne 2.6 -or $persistedFloor.surfaces[0].materialId -ne $materialId) {
        throw 'Applied/edited geometry, stable identity or surface material assignment did not persist after API restart.'
    }
    $persistedWalls = @($persistedScene.objects | Where-Object { $_.name -eq 'Reconstructed wall' })
    if ($persistedWalls.Count -ne 4) { throw "Expected four generated wall objects after restart; found $($persistedWalls.Count)." }
    Write-Output "PASS: licensed interior photo, manual floor/wall annotations, two measured references, topology, geometry candidate, confirmed apply, floor dimension/material edit and four walls persisted after local roomforge API restart (Scene revision $($persistedScene.revision))."
    $http.Dispose()
}
finally {
    Stop-RoomForgeApi
    if ($null -eq $previousEnvironment) { Remove-Item Env:ASPNETCORE_ENVIRONMENT -ErrorAction SilentlyContinue } else { $env:ASPNETCORE_ENVIRONMENT = $previousEnvironment }
    if ($null -eq $previousAssetRoot) { Remove-Item Env:ROOMFORGE_ASSET_ROOT -ErrorAction SilentlyContinue } else { $env:ROOMFORGE_ASSET_ROOT = $previousAssetRoot }
}
