import { expect, test } from '@playwright/test'
import { readFileSync } from 'node:fs'
import { createRequire } from 'node:module'
import { dirname, resolve } from 'node:path'
import { fileURLToPath } from 'node:url'

const sceneId = '11111111-1111-4111-8111-111111111111'
type GltfValidationReport = { issues: { numErrors: number; messages: { code: string; severity: number; message: string }[] } }
const gltfValidator = createRequire(import.meta.url)('gltf-validator') as {
  validateBytes(data: Uint8Array, options: { uri: string; format: 'glb'; maxIssues: number }): Promise<GltfValidationReport>
}
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==', 'base64')
const interiorPhoto = readFileSync(resolve(dirname(fileURLToPath(import.meta.url)), '../../../examples/interior-reference.jpg'))
test.afterEach(async ({ page }) => {
  // Keep the shared sample-scene ID canonical even when an assertion fails mid-flow.
  await page.evaluate(async (id) => {
    const currentResponse = await fetch(`/api/scenes/${id}`)
    if (!currentResponse.ok) return
    const current = await currentResponse.json()
    const sample = await (await fetch('/api/sample-scene')).json()
    const sampleIds = new Set(sample.objects.map((object: { id: string }) => object.id))
    sample.objects.push(...current.objects.filter((object: { id: string; locked?: boolean; category?: string; surfaces?: { semanticRole?: string }[] }) => {
      const protectedObject = object.locked === true || object.category === 'architecture' || object.category === 'structure' || object.surfaces?.some((surface) => ['floor', 'wall', 'countertop_top', 'countertop_edge'].includes(surface.semanticRole ?? ''))
      if (!protectedObject || sampleIds.has(object.id)) return false
      sampleIds.add(object.id)
      return true
    }))
    sample.revision = current.revision
    await fetch(`/api/scenes/${id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${current.revision}"` },
      body: JSON.stringify(sample),
    })
  }, sceneId)
})

test('uploads, reviews, previews and explicitly applies a durable mock reconstruction', async ({ page }) => {
  test.setTimeout(300_000)
  let idempotencyKey = ''
  page.on('request', (request) => {
    if (request.url().includes('/reconstruction-jobs') && request.method() === 'POST') {
      const body = request.postDataJSON() as { idempotencyKey?: string }
      idempotencyKey = body.idempotencyKey ?? ''
    }
  })
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Sample room' })).toBeVisible()
  const before = await page.evaluate(async (id) => {
    const response = await fetch(`/api/scenes/${id}`)
    const existing = response.ok ? await response.json() : null
    const sample = await (await fetch('/api/sample-scene')).json()
    if (existing) {
      const sampleIds = new Set(sample.objects.map((item: { id: string }) => item.id))
      sample.objects.push(...existing.objects.filter((item: { id: string; locked?: boolean; category?: string; surfaces?: { semanticRole?: string }[] }) => {
        const protectedObject = item.locked === true || item.category === 'architecture' || item.category === 'structure' || item.surfaces?.some((surface) => ['floor', 'wall', 'countertop_top', 'countertop_edge'].includes(surface.semanticRole ?? ''))
        if (!protectedObject || sampleIds.has(item.id)) return false
        sampleIds.add(item.id)
        return true
      }))
    }
    sample.revision = existing?.revision ?? 0
    const created = await fetch(`/api/scenes/${id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${sample.revision}"` }, body: JSON.stringify(sample),
    })
    if (!created.ok) throw new Error(`Could not seed reconstruction test scene: ${created.status} ${await created.text()}`)
    return created.json()
  }, sceneId)
  const originalChair = before.objects.find((item: { name: string }) => item.name === 'Demo Chair Proxy')
  const originalFloor = before.objects.flatMap((item: { surfaces: { semanticRole: string; id: string }[] }) => item.surfaces).find((item: { semanticRole: string }) => item.semanticRole === 'floor')
  await page.reload()
  await expect(page.getByRole('heading', { name: 'Sample room' })).toBeVisible()
  const glbDownload = page.waitForEvent('download')
  const glbResponse = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}/export.glb`))
  await page.getByRole('button', { name: 'Export saved scene GLB' }).click()
  const downloadedGlb = await glbDownload
  expect(downloadedGlb.suggestedFilename()).toMatch(/\.glb$/)
  const glbExportResponse = await glbResponse
  expect(glbExportResponse.ok(), `GLB export returned HTTP ${glbExportResponse.status()}`).toBe(true)
  const downloadedPath = await downloadedGlb.path()
  expect(downloadedPath).not.toBeNull()
  const exportedGlb = readFileSync(downloadedPath!)
  const validatorReport = await gltfValidator.validateBytes(new Uint8Array(exportedGlb), { uri: 'roomforge-export.glb', format: 'glb', maxIssues: 0 })
  expect(validatorReport.issues.numErrors, JSON.stringify(validatorReport.issues.messages)).toBe(0)
  const imageResponse = page.waitForResponse((response) => response.url().includes(`/api/projects/${before.projectId}/images`) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await page.getByLabel('Room image').setInputFiles({ name: 'room.png', mimeType: 'image/png', buffer: png })
  await page.getByRole('button', { name: 'Upload and analyze (mock)' }).click()
  const uploaded = await (await imageResponse).json() as { imageId: string; url: string }
  await expect(page.getByText('succeeded', { exact: true })).toBeVisible({ timeout: 20_000 })
  await expect(page.getByText(/Scene observations: roomforge-mock; Segmentation provenance: mock\/deterministic-polygons/)).toBeVisible()

  const duplicateA = await page.evaluate(async ({ projectId, imageId, key }) => fetch(`/api/projects/${projectId}/reconstruction-jobs`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ imageId, idempotencyKey: key }),
  }).then((response) => response.json()), { projectId: before.projectId, imageId: uploaded.imageId, key: idempotencyKey })
  const duplicateB = await page.evaluate(async ({ projectId, imageId, key }) => fetch(`/api/projects/${projectId}/reconstruction-jobs`, {
    method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ imageId, idempotencyKey: key }),
  }).then((response) => response.json()), { projectId: before.projectId, imageId: uploaded.imageId, key: idempotencyKey })
  expect(duplicateA.id).toBe(duplicateB.id)

  const includeChair = page.getByLabel('Include Detected chair')
  await includeChair.uncheck()
  await includeChair.check()
  await page.getByLabel('Object type Detected chair').fill('decor')
  await page.getByLabel('floor point 1 X').fill('1')
  await expect(page.getByText(/user-corrected/).first()).toBeVisible()
  await page.getByLabel('Width Detected chair').fill('0.72')
  await page.getByLabel('Reference pixel length').fill('100')
  await page.getByLabel('Known reference length').fill('2.5')
  await expect(page.getByLabel('Meters per pixel')).toHaveValue('0.025')
  await page.getByRole('checkbox', { name: /Update current scene/ }).check()
  await page.getByLabel('Match object Detected chair').selectOption(originalChair.id)
  await page.getByLabel('Match surface floor').selectOption(originalFloor.id)

  await page.getByRole('button', { name: 'Preview candidate scene' }).click()
  await expect(page.getByRole('region', { name: 'Reconstruction scene preview' })).toBeVisible()
  const afterPreview = await page.evaluate(async (id) => (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  expect(afterPreview.revision).toBe(before.revision)
  expect(afterPreview.objects).toEqual(before.objects)
  const preview = await page.evaluate(async (jobId) => fetch(`/api/reconstruction/jobs/${jobId}/preview`).then((response) => response.json()), duplicateA.id)
  const candidate = JSON.parse(preview.document) as typeof before
  const candidateChair = candidate.objects.find((item: { id: string }) => item.id === originalChair.id)
  expect(candidateChair.dimensions[0]).toBe(0.72)
  expect(candidateChair.category).toBe('decor')
  expect(candidateChair.surfaces).toEqual(originalChair.surfaces)
  expect(candidate.sourceImageId).toBe(uploaded.imageId)

  await page.getByLabel(/I reviewed this candidate/).check()
  await page.getByRole('button', { name: 'Confirm and update scene' }).click()
  await expect(page.getByText(/Reconstruction saved · revision/)).toBeVisible()
  const committed = await page.evaluate(async (id) => (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  expect(committed.revision).toBe(before.revision + 1)
  expect(committed.objects.map((item: { id: string }) => item.id)).toContain(originalChair.id)
  expect(committed.objects.find((item: { id: string }) => item.id === originalChair.id).dimensions[0]).toBe(0.72)
  for (const original of before.objects as { id: string; surfaces: unknown }[]) {
    expect(committed.objects.find((item: { id: string }) => item.id === original.id).surfaces).toEqual(original.surfaces)
  }
  expect(await page.evaluate(async (url) => (await fetch(url)).status, uploaded.url)).toBe(200)

  const jsonDownloadEvent = page.waitForEvent('download')
  const jsonResponseEvent = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}/export.json`))
  await page.getByRole('button', { name: 'Export saved scene JSON' }).click()
  const jsonDownload = await jsonDownloadEvent
  const jsonResponse = await jsonResponseEvent
  expect(jsonResponse.ok()).toBe(true)
  const exportedJson = JSON.parse(readFileSync((await jsonDownload.path())!, 'utf8')) as { revision: number; scene: typeof committed }
  expect(exportedJson.revision).toBe(committed.revision)
  expect(exportedJson.scene.objects.map((item: { id: string }) => item.id).sort()).toEqual(committed.objects.map((item: { id: string }) => item.id).sort())

  const appliedGlbDownloadEvent = page.waitForEvent('download')
  const appliedGlbResponseEvent = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}/export.glb`))
  await page.getByRole('button', { name: 'Export saved scene GLB' }).click()
  const appliedGlbResponse = await appliedGlbResponseEvent
  expect(appliedGlbResponse.ok(), `Applied Scene GLB export returned HTTP ${appliedGlbResponse.status()}: ${await appliedGlbResponse.text()}`).toBe(true)
  const appliedGlbDownload = await appliedGlbDownloadEvent
  const appliedGlbReport = await gltfValidator.validateBytes(new Uint8Array(readFileSync((await appliedGlbDownload.path())!)),
    { uri: 'roomforge-reconstructed-scene.glb', format: 'glb', maxIssues: 0 })
  expect(appliedGlbReport.issues.numErrors, JSON.stringify(appliedGlbReport.issues.messages)).toBe(0)

  const texturedScene = await page.evaluate(async ({ id, encodedPng }) => {
    const textureResponse = await fetch('/api/assets/textures', {
      method: 'POST', headers: { 'Content-Type': 'image/png' },
      body: Uint8Array.from(atob(encodedPng), (character) => character.charCodeAt(0)),
    })
    if (!textureResponse.ok) throw new Error(`Texture fixture upload failed: ${textureResponse.status}`)
    const texture = await textureResponse.json()
    const currentResponse = await fetch(`/api/scenes/${id}`)
    const current = await currentResponse.json()
    current.materials = current.materials.map((material: { name: string; maps?: Record<string, string> }) => material.name !== 'Light Oak'
      ? material
      : { ...material, maps: { baseColor: texture.url, normal: texture.url, roughness: texture.url, metalness: texture.url } })
    const saved = await fetch(`/api/scenes/${id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${current.revision}"` }, body: JSON.stringify(current),
    })
    if (!saved.ok) throw new Error(`Textured scene save failed: ${saved.status} ${await saved.text()}`)
    return saved.json()
  }, { id: sceneId, encodedPng: png.toString('base64') })
  expect(texturedScene.revision).toBe(committed.revision + 1)
  await page.reload()
  const texturedGlbDownloadEvent = page.waitForEvent('download')
  const texturedGlbResponseEvent = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}/export.glb`))
  await page.getByRole('button', { name: 'Export saved scene GLB' }).click()
  const texturedGlbDownload = await texturedGlbDownloadEvent
  const texturedGlbResponse = await texturedGlbResponseEvent
  expect(texturedGlbResponse.ok()).toBe(true)
  const texturedGlbReport = await gltfValidator.validateBytes(new Uint8Array(readFileSync((await texturedGlbDownload.path())!)),
    { uri: 'roomforge-pbr-scene.glb', format: 'glb', maxIssues: 0 })
  expect(texturedGlbReport.issues.numErrors, JSON.stringify(texturedGlbReport.issues.messages)).toBe(0)

})

test('rejects invalid room image bytes at upload boundary', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Sample room' })).toBeVisible()
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await page.getByLabel('Room image').setInputFiles({ name: 'not-an-image.png', mimeType: 'image/png', buffer: Buffer.from('invalid image bytes') })
  const uploadResponse = page.waitForResponse((response) => response.url().includes('/images') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Upload and analyze (mock)' }).click()
  expect((await uploadResponse).status()).toBe(400)
  await expect(page.getByRole('alert')).toContainText('PNG')
  await expect(page.getByText('Upload an image to create a persistent job.')).toBeVisible()
})

test('creates, edits and reloads manual image annotations without creating an AI job', async ({ page }) => {
  test.setTimeout(300_000)
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Sample room' })).toBeVisible()
  const scene = await page.evaluate(async (id) => (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await page.getByLabel('Room image').setInputFiles({ name: 'interior-reference.jpg', mimeType: 'image/jpeg', buffer: interiorPhoto })
  const upload = page.waitForResponse((response) => response.url().includes(`/api/projects/${scene.projectId}/images`) && response.request().method() === 'POST')
  const annotationCreated = page.waitForResponse((response) => response.url().includes(`/api/projects/${scene.projectId}/annotations`) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Upload for manual annotation' }).click()
  expect((await upload).status()).toBe(201)
  const createdResponse = await annotationCreated
  expect(createdResponse.status()).toBe(201)
  const created = await createdResponse.json() as { annotationId: string; revision: number; imageWidth: number; imageHeight: number }
  await expect(page.getByRole('region', { name: 'Manual image annotation' })).toBeVisible()

  await page.getByRole('button', { name: 'Draw polygon' }).click()
  const canvas = page.locator('.annotation-canvas canvas').first()
  await expect(canvas).toBeVisible()
  await canvas.scrollIntoViewIfNeeded()
  const bounds = await canvas.boundingBox()
  if (!bounds) throw new Error('Annotation canvas has no bounds')
  const stageWidth = bounds.width
  const imageWidth = created.imageWidth
  const imageHeight = created.imageHeight
  const stageHeight = Math.min(560, stageWidth * imageHeight / imageWidth)
  const scale = Math.min(stageWidth / imageWidth, stageHeight / imageHeight)
  const offsetX = (stageWidth - imageWidth * scale) / 2
  const offsetY = (stageHeight - imageHeight * scale) / 2
  const imagePoint = (xRatio: number, yRatio: number) => ({ x: imageWidth * xRatio, y: imageHeight * yRatio })
  const clickImagePoint = async (xRatio: number, yRatio: number) => {
    const point = imagePoint(xRatio, yRatio)
    await page.mouse.click(bounds.x + offsetX + point.x * scale, bounds.y + offsetY + point.y * scale)
  }
  for (const [x, y] of [[.05, .52], [.95, .52], [.95, .95], [.05, .95]]) {
    await clickImagePoint(x, y)
  }
  await expect(page.getByText('Draft polygon · 4 points')).toBeVisible()
  await page.getByRole('button', { name: 'Finish polygon' }).click()
  await expect(page.getByText('1 regions')).toBeVisible()
  await page.getByRole('button', { name: 'New region' }).click()
  await page.getByLabel('Annotation region role').selectOption('wall')
  await page.getByRole('button', { name: 'Draw polygon' }).click()
  for (const [x, y] of [[.05, .05], [.95, .05], [.95, .52], [.05, .52]]) await clickImagePoint(x, y)
  await page.getByRole('button', { name: 'Finish polygon' }).click()
  await expect(page.getByText('2 regions')).toBeVisible()

  await page.getByLabel('Known reference length meters').fill('2.4')
  await page.getByRole('button', { name: 'Draw reference' }).click()
  await clickImagePoint(.10, .51)
  await clickImagePoint(.90, .51)
  await expect(page.getByText('1 measured spans')).toBeVisible()
  await page.getByLabel('Known reference length meters').fill('2.7')
  await page.getByRole('button', { name: 'Draw reference' }).click()
  await clickImagePoint(.50, .10)
  await clickImagePoint(.50, .90)
  await expect(page.getByText('2 measured spans')).toBeVisible()
  await page.getByRole('button', { name: 'Save revision 2' }).click()
  await expect(page.getByText(/MANUAL ANNOTATION.*REVISION 2/)).toBeVisible()

  await page.getByRole('button', { name: 'Back to scene editor' }).click()
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await expect(page.getByText(/MANUAL ANNOTATION.*REVISION 2/)).toBeVisible()
  await expect(page.getByText('2 regions')).toBeVisible()
  await expect(page.getByText('2 measured spans')).toBeVisible()
  const topologyResponse = page.waitForResponse((response) => response.url().includes(`/api/projects/${scene.projectId}/annotations/${created.annotationId}/topology`) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Build candidates from current annotation' }).click()
  const topologyCreated = await topologyResponse
  expect(topologyCreated.status()).toBe(201)
  const topologyDocument = await topologyCreated.json() as { id: string; edges: { id: string; start: { x: number; y: number }; end: { x: number; y: number } }[] }
  await expect(page.getByRole('combobox', { name: 'Topology edge 1 role' })).toBeVisible()
  const edgeRoleSelectors = await page.getByRole('combobox', { name: /Topology edge .* role/ }).all()
  expect(edgeRoleSelectors).toHaveLength(8)
  for (const [index, edgeRole] of edgeRoleSelectors.entries()) if (index < 4) await edgeRole.selectOption('floor_wall')
  const topologySaved = page.waitForResponse((response) => response.url().includes('/topologies/') && response.request().method() === 'PATCH')
  await page.getByRole('button', { name: 'Save topology corrections' }).click()
  expect((await topologySaved).status()).toBe(200)
  for (const [label, value] of [['Camera fx', String(imageWidth)], ['Camera fy', String(imageWidth)], ['Camera cx', String(imageWidth / 2)], ['Camera cy', String(imageHeight / 2)],
    ['Camera position 1 meters', '0'], ['Camera position 2 meters', '0'], ['Camera position 3 meters', '0']] as const) await page.getByLabel(label).fill(value)
  const edge = topologyDocument.edges[0]!
  const toWorld = (point: { x: number; y: number }) => [(point.x - imageWidth / 2) * 3 / imageWidth, -(point.y - imageHeight / 2) * 3 / imageWidth, -3]
  const worldCoordinates = [...toWorld(edge.start), ...toWorld(edge.end)]
  for (const [index, endpoint] of ['Start', 'End'].entries()) for (const [axisIndex, axis] of ['X', 'Y', 'Z'].entries()) {
    await page.getByLabel(`Edge 1 ${endpoint} ${axis} meters`).fill(String(worldCoordinates[index * 3 + axisIndex]))
  }
  await page.getByRole('button', { name: 'Check photo alignment' }).click()
  await expect(page.getByRole('status').filter({ hasText: 'Reprojection RMS:' })).toContainText('visible samples')
  await page.getByRole('button', { name: 'Preview parametric geometry' }).click()
  await expect(page.getByRole('region', { name: 'Geometry preview result' })).toContainText(/parametric geometry.*ok/)
  await expect(page.getByRole('region', { name: 'Geometry preview result' })).toContainText('unknown m (unknown)')
  const dimensionInputs = page.locator('.geometry-preview-result input[type="number"]')
  await expect(dimensionInputs).toHaveCount(5)
  for (const dimensionInput of await dimensionInputs.all()) await dimensionInput.fill('0.12')
  const candidateCreated = page.waitForResponse((response) => response.url().includes('/geometry-candidates') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Save append-only scene candidate' }).click()
  expect((await candidateCreated).status()).toBe(201)
  await expect(page.getByText(/Candidate .* based on Scene revision/)).toBeVisible()
  await expect(page.locator('.candidate-view canvas')).toBeVisible()
  const sceneBeforeCandidateApply = await page.evaluate(async (id) => await (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  expect(sceneBeforeCandidateApply.revision).toBe(scene.revision)
  await page.getByRole('button', { name: 'Back to scene editor' }).click()
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await expect(page.getByRole('combobox', { name: 'Topology edge 1 role' })).toHaveValue('floor_wall')
  await expect(page.getByText(/Candidate .* based on Scene revision/)).toBeVisible()
  const savedCandidateId = await page.evaluate((annotationId) => window.localStorage.getItem(`roomforge:geometry-candidate:${annotationId}`), created.annotationId)
  expect(savedCandidateId).toBeTruthy()
  const savedCandidate = await page.evaluate(async ({ projectId, candidateId }) => {
    const response = await fetch(`/api/projects/${projectId}/geometry-candidates/${candidateId}`)
    return response.json()
  }, { projectId: scene.projectId, candidateId: savedCandidateId })
  const generatedFloorId = savedCandidate.geometrySpec.instances.find((instance: { name: string }) => instance.name === 'Reconstructed floor').stableId
  await page.getByLabel(/Apply this geometry to the Scene/).check()
  const candidateApply = page.waitForResponse((response) => response.url().includes(`/geometry-candidates/${savedCandidate.candidateId}/apply`) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Apply confirmed geometry' }).click()
  expect((await candidateApply).status()).toBe(200)
  await expect(page.getByRole('button', { name: 'Reconstruct', exact: true })).toBeVisible()
  const sceneAfterGeometryApply = await page.evaluate(async (id) => await (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  const generatedFloor = sceneAfterGeometryApply.objects.find((object: { id: string }) => object.id === generatedFloorId)
  expect(generatedFloor).toBeDefined()
  expect(generatedFloor.category).toBe('architecture')
  expect(generatedFloor.surfaces[0].materialId).toBeTruthy()
  await page.getByRole('button', { name: 'Select object Reconstructed floor' }).last().click()
  const widthInput = page.getByLabel('Width (m)', { exact: true })
  await widthInput.fill('2.6')
  await widthInput.press('Enter')
  await page.getByRole('button', { name: 'floor Reconstructed floor' }).last().click()
  await page.getByRole('button', { name: 'Edit material Warm White' }).click()
  await page.getByRole('button', { name: 'Assign to selected surface' }).click()
  const sceneEditSaved = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}`) && response.request().method() === 'PUT')
  await page.getByRole('button', { name: 'Save scene' }).click()
  const savedSceneResponse = await sceneEditSaved
  expect(savedSceneResponse.status()).toBe(200)
  const savedScene = await savedSceneResponse.json()
  await expect(page.getByLabel(/Saved to PostgreSQL/)).toBeVisible()
  const reloadResponse = page.waitForResponse((response) => response.url().includes(`/api/scenes/${sceneId}`) && response.request().method() === 'GET')
  await page.getByRole('button', { name: 'Reload' }).click()
  const editedSceneResponse = await reloadResponse
  expect(editedSceneResponse.status()).toBe(200)
  const editedScene = await editedSceneResponse.json()
  await expect(page.locator('.scene-footer')).toContainText(`Revision ${savedScene.revision}`)
  const editedFloor = editedScene.objects.find((object: { id: string }) => object.id === generatedFloorId)
  expect(editedFloor.dimensions[0]).toBe(2.6)
  expect(editedFloor.surfaces[0].materialId).toBe(sceneAfterGeometryApply.materials.find((material: { name: string }) => material.name === 'Warm White').id)
  await page.getByRole('button', { name: 'Reconstruct', exact: true }).click()
  await expect(page.getByText(/MANUAL ANNOTATION.*REVISION 2/)).toBeVisible()
  await page.getByRole('button', { name: 'floor · 4 points' }).click()
  await page.getByLabel('Annotation region role').selectOption('wall')
  await page.getByRole('button', { name: 'Save revision 3' }).click()
  await expect(page.getByText(/MANUAL ANNOTATION.*REVISION 3/)).toBeVisible()

  expect(created.revision).toBe(1)
  const loaded = await page.evaluate(async ({ projectId, annotationId }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations/${annotationId}`)
    return { status: response.status, etag: response.headers.get('ETag'), document: await response.json() }
  }, { projectId: scene.projectId, annotationId: created.annotationId })
  expect(loaded.status).toBe(200)
  expect(loaded.etag).toBe('"3"')
  expect(loaded.document.regions).toHaveLength(2)
  expect(loaded.document.regions[0].semanticRole).toBe('wall')
  expect(loaded.document.regions[0].provenance).toBe('user_supplied')
  expect(loaded.document.regions[0].confidence).toBeNull()
  expect(loaded.document.referenceSpans[0].knownMeters).toBe(2.4)
  const staleWrite = await page.evaluate(async ({ projectId, annotationId, document }) => fetch(`/api/projects/${projectId}/annotations/${annotationId}`, {
    method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': '"1"' },
    body: JSON.stringify({ regions: document.regions, referenceSpans: document.referenceSpans, cameraHint: document.cameraHint, notes: document.notes }),
  }).then(async (response) => ({ status: response.status, body: await response.json() })),
  { projectId: scene.projectId, annotationId: created.annotationId, document: loaded.document })
  expect(staleWrite.status).toBe(409)
  expect(staleWrite.body.currentRevision).toBe(3)
})

test('persists geometry candidates and applies them only after explicit confirmation', async ({ page }) => {
  test.setTimeout(120_000)
  await page.goto('/')
  const scene = await page.evaluate(async (id) => await (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  const imageBase64 = await page.evaluate(() => {
    const canvas = document.createElement('canvas'); canvas.width = 640; canvas.height = 480
    const context = canvas.getContext('2d')!; context.fillStyle = '#bbb'; context.fillRect(0, 0, 640, 480)
    return canvas.toDataURL('image/png').split(',')[1]!
  })
  const uploaded = await page.evaluate(async ({ projectId, encoded }) => {
    const data = new FormData(); data.append('image', new Blob([Uint8Array.from(atob(encoded), (char) => char.charCodeAt(0))], { type: 'image/png' }), 'candidate.png')
    const response = await fetch(`/api/projects/${projectId}/images`, { method: 'POST', body: data })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, encoded: imageBase64 })
  expect(uploaded.status).toBe(201)
  const annotationCreated = await page.evaluate(async ({ projectId, sourceImageId }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ sourceImageId }) })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, sourceImageId: uploaded.body.imageId })
  expect(annotationCreated.status).toBe(201)
  const annotation = annotationCreated.body
  const regionId = crypto.randomUUID()
  const spanId = crypto.randomUUID()
  const region = { id: regionId, semanticRole: 'floor', polygon: { points: [{ x: 30, y: 30 }, { x: 610, y: 30 }, { x: 610, y: 450 }, { x: 30, y: 450 }] }, provenance: 'user_supplied', confidence: null }
  const span = { id: spanId, start: { x: 80, y: 240 }, end: { x: 560, y: 240 }, knownMeters: 2.4, provenance: 'user_supplied' }
  const annotationSaved = await page.evaluate(async ({ projectId, annotationId, revision, region, span, cameraHint }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations/${annotationId}`, { method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${revision}"` }, body: JSON.stringify({ regions: [region], referenceSpans: [span], cameraHint, notes: [] }) })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, annotationId: annotation.annotationId, revision: annotation.revision, region, span, cameraHint: annotation.cameraHint })
  expect(annotationSaved.status).toBe(200)
  const topology = await page.evaluate(async ({ projectId, annotationId }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations/${annotationId}/topology`, { method: 'POST' })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, annotationId: annotation.annotationId })
  expect(topology.status).toBe(201)
  const geometry = await page.evaluate(async ({ projectId, annotationId, topologyId, floorRegionId, calibrationSpanId }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations/${annotationId}/geometry`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ topologyId, floorRegionId, calibrationSpanId }) })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, annotationId: annotation.annotationId, topologyId: topology.body.id, floorRegionId: regionId, calibrationSpanId: spanId })
  expect(geometry.status).toBe(200)
  const floorInstance = geometry.body.geometrySpec.instances[0]
  const unknownIndex = floorInstance.dimensions.findIndex((dimension: { status: string }) => dimension.status === 'unknown')
  expect(unknownIndex).toBeGreaterThanOrEqual(0)
  const candidate = await page.evaluate(async ({ projectId, annotationId, topologyId, sceneId, expectedSceneRevision, floorRegionId, calibrationSpanId, dimensionOverrides }) => {
    const response = await fetch(`/api/projects/${projectId}/annotations/${annotationId}/geometry-candidates`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ topologyId, sceneId, expectedSceneRevision, floorRegionId, calibrationSpanId, dimensionOverrides }) })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, annotationId: annotation.annotationId, topologyId: topology.body.id, sceneId, expectedSceneRevision: scene.revision, floorRegionId: regionId, calibrationSpanId: spanId, dimensionOverrides: [{ instanceId: floorInstance.stableId, dimensionIndex: unknownIndex, meters: 0.15 }] })
  expect(candidate.status).toBe(201)
  const unchanged = await page.evaluate(async (id) => await (await fetch(`/api/scenes/${id}`)).json(), sceneId)
  expect(unchanged.revision).toBe(scene.revision)
  const reloaded = await page.evaluate(async ({ projectId, candidateId }) => {
    const response = await fetch(`/api/projects/${projectId}/geometry-candidates/${candidateId}`)
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, candidateId: candidate.body.candidateId })
  expect(reloaded.status).toBe(200)
  expect(reloaded.body.geometrySpec.instances[0].dimensions[unknownIndex].status).toBe('measured')
  const rejected = await page.evaluate(async ({ projectId, candidateId, expectedRevision }) => (await fetch(`/api/projects/${projectId}/geometry-candidates/${candidateId}/apply`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ confirmed: false, expectedRevision }) })).status,
  { projectId: scene.projectId, candidateId: candidate.body.candidateId, expectedRevision: scene.revision })
  expect(rejected).toBe(400)
  const applied = await page.evaluate(async ({ projectId, candidateId, expectedRevision }) => {
    const response = await fetch(`/api/projects/${projectId}/geometry-candidates/${candidateId}/apply`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ confirmed: true, expectedRevision }) })
    return { status: response.status, body: await response.json() }
  }, { projectId: scene.projectId, candidateId: candidate.body.candidateId, expectedRevision: scene.revision })
  expect(applied.status).toBe(200)
  expect(applied.body.revision).toBe(scene.revision + 1)
  const appliedDocument = JSON.parse(applied.body.document) as { objects: Array<{ id: string; dimensions: number[]; measurementStatus: string }> }
  const savedObject = appliedDocument.objects.find((object) => object.id === floorInstance.stableId)
  expect(savedObject).toBeDefined()
  expect(savedObject.dimensions[unknownIndex]).toBe(0.15)
  expect(savedObject.measurementStatus).toBe('estimated')
})
