import { expect, test } from '@playwright/test'

const sceneId = '11111111-1111-4111-8111-111111111111'
const png = Buffer.from('iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAAAAAA6fptVAAAACklEQVR4nGNgAAAAAgABSK+kcQAAAABJRU5ErkJggg==', 'base64')

test.afterEach(async ({ page }) => {
  // Keep the shared sample-scene ID canonical even when an assertion fails mid-flow.
  await page.evaluate(async (id) => {
    const currentResponse = await fetch(`/api/scenes/${id}`)
    if (!currentResponse.ok) return
    const current = await currentResponse.json()
    const sample = await (await fetch('/api/sample-scene')).json()
    const sampleIds = new Set(sample.objects.map((object: { id: string }) => object.id))
    sample.objects.push(...current.objects.filter((object: { id: string; locked?: boolean }) => object.locked && !sampleIds.has(object.id)))
    sample.revision = current.revision
    await fetch(`/api/scenes/${id}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${current.revision}"` },
      body: JSON.stringify(sample),
    })
  }, sceneId)
})

test('uploads, reviews, previews and explicitly applies a durable mock reconstruction', async ({ page }) => {
  test.setTimeout(120_000)
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
      sample.objects.push(...existing.objects.filter((item: { id: string; locked?: boolean }) => item.locked && !sampleIds.has(item.id)))
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
  const imageResponse = page.waitForResponse((response) => response.url().includes(`/api/projects/${before.projectId}/images`) && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Reconstruct' }).click()
  await page.getByLabel('Room image').setInputFiles({ name: 'room.png', mimeType: 'image/png', buffer: png })
  await page.getByRole('button', { name: 'Upload and analyze (mock)' }).click()
  const uploaded = await (await imageResponse).json() as { imageId: string; url: string }
  await expect(page.getByText('succeeded', { exact: true })).toBeVisible({ timeout: 20_000 })
  await expect(page.getByText(/roomforge-mock.*deterministic mock output/)).toBeVisible()

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

})

test('rejects invalid room image bytes at upload boundary', async ({ page }) => {
  await page.goto('/')
  await expect(page.getByRole('heading', { name: 'Sample room' })).toBeVisible()
  await page.getByRole('button', { name: 'Reconstruct' }).click()
  await page.getByLabel('Room image').setInputFiles({ name: 'not-an-image.png', mimeType: 'image/png', buffer: Buffer.from('invalid image bytes') })
  const uploadResponse = page.waitForResponse((response) => response.url().includes('/images') && response.request().method() === 'POST')
  await page.getByRole('button', { name: 'Upload and analyze (mock)' }).click()
  expect((await uploadResponse).status()).toBe(400)
  await expect(page.getByRole('alert')).toContainText('PNG')
  await expect(page.getByText('Upload an image to create a persistent job.')).toBeVisible()
})
