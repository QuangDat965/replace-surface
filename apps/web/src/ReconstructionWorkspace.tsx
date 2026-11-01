import { lazy, Suspense, useCallback, useEffect, useState } from 'react'
import type { SceneAnalysisResult } from './reconstructionTypes'
import type { ReconstructionAssumptions } from './reconstructionTypes'
import type { SceneDocument } from './sceneTypes'
import './ReconstructionWorkspace.css'

const SceneViewer = lazy(() => import('./SceneViewer').then((module) => ({ default: module.SceneViewer })))

type Props = { scene: SceneDocument; onClose: () => void; onApplied: (scene: SceneDocument) => void }
type UploadedImage = { imageId: string; url: string; width: number; height: number; contentType: string }
type Job = { id: string; status: string; stage: string; progress: number; attemptCount: number; maxAttempts: number; errorCode: string | null; errorMessage: string | null; analysisJson: string | null }
type Preview = { sceneId: string; baseRevision: number; document: string; createsNewScene: boolean }
type Applied = { sceneId: string; revision: number; created: boolean; document: string }

function errorText(body: unknown, fallback: string) {
  if (body && typeof body === 'object' && 'detail' in body && typeof body.detail === 'string') return body.detail
  if (body && typeof body === 'object' && 'error' in body && typeof body.error === 'string') return body.error
  return fallback
}

export function ReconstructionWorkspace({ scene, onClose, onApplied }: Props) {
  const [file, setFile] = useState<File | null>(null)
  const [image, setImage] = useState<UploadedImage | null>(null)
  const [job, setJob] = useState<Job | null>(null)
  const [analysis, setAnalysis] = useState<SceneAnalysisResult | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [updatingExisting, setUpdatingExisting] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [capabilities, setCapabilities] = useState<{ name: string; available: boolean; providerId: string; description: string }[]>([])
  const isActive = job?.status === 'queued' || job?.status === 'processing'

  const refreshJob = useCallback(async () => {
    if (!job) return
    const response = await fetch(`/api/reconstruction/jobs/${job.id}`)
    if (!response.ok) throw new Error(`Job status request failed (${response.status})`)
    const current = await response.json() as Job
    setJob(current)
    if (current.status === 'succeeded' && current.analysisJson) {
      setAnalysis(JSON.parse(current.analysisJson) as SceneAnalysisResult)
      setError(null)
    }
  }, [job])

  useEffect(() => {
    let cancelled = false
    void fetch('/api/reconstruction/capabilities').then((response) => response.ok ? response.json() : []).then((items) => {
      if (!cancelled) setCapabilities(items as typeof capabilities)
    }).catch(() => undefined)
    return () => { cancelled = true }
  }, [])

  useEffect(() => {
    if (!isActive) return
    const timer = window.setInterval(() => { void refreshJob().catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'Unable to refresh job status')) }, 700)
    return () => window.clearInterval(timer)
  }, [isActive, refreshJob])

  async function uploadAndAnalyze() {
    if (!file) return
    setBusy(true); setError(null); setPreview(null); setAnalysis(null); setJob(null)
    try {
      const data = new FormData()
      data.append('image', file)
      const uploaded = await fetch(`/api/projects/${scene.projectId}/images`, { method: 'POST', body: data })
      const uploadedBody = await uploaded.json().catch(() => null)
      if (!uploaded.ok) throw new Error(errorText(uploadedBody, `Image upload failed (${uploaded.status})`))
      const storedImage = uploadedBody as UploadedImage
      setImage(storedImage)
      const created = await fetch(`/api/projects/${scene.projectId}/reconstruction-jobs`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageId: storedImage.imageId, idempotencyKey: crypto.randomUUID() }),
      })
      const createdBody = await created.json().catch(() => null)
      if (!created.ok) throw new Error(errorText(createdBody, `Unable to start reconstruction (${created.status})`))
      setJob(createdBody as Job)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to upload and analyze the image') }
    finally { setBusy(false) }
  }

  function updateObject(id: string, update: (item: SceneAnalysisResult['objects'][number]) => SceneAnalysisResult['objects'][number]) {
    setAnalysis((current) => current ? { ...current, objects: current.objects.map((item) => item.id === id ? update(item) : item) } : current)
    setPreview(null); setConfirmed(false)
  }

  function updateSurface(id: string, linkedSurfaceId: string | null) {
    setAnalysis((current) => current ? { ...current, surfaces: current.surfaces.map((item) => item.id === id ? { ...item, linkedSurfaceId } : item) } : current)
    setPreview(null); setConfirmed(false)
  }

  function updateCalibration(referencePixels: number | null, knownMeters: number | null) {
    setAnalysis((current) => current ? { ...current, assumptions: {
      ...current.assumptions,
      referencePixelLength: referencePixels,
      referenceLengthMeters: knownMeters,
      metersPerPixel: referencePixels && knownMeters ? knownMeters / referencePixels : null,
      scaleStatus: referencePixels && knownMeters ? 'user_supplied' : 'unknown',
    } } : current)
    setPreview(null); setConfirmed(false)
  }

  function updateAssumptions(patch: Partial<ReconstructionAssumptions>) {
    setAnalysis((current) => current ? { ...current, assumptions: { ...current.assumptions, ...patch } } : current)
    setPreview(null); setConfirmed(false)
  }

  async function cancelJob() {
    if (!job) return
    const response = await fetch(`/api/reconstruction/jobs/${job.id}/cancel`, { method: 'POST' })
    if (!response.ok) setError('This job can no longer be cancelled.')
    else await refreshJob()
  }

  async function retryJob() {
    if (!job) return
    const response = await fetch(`/api/reconstruction/jobs/${job.id}/retry`, { method: 'POST' })
    if (!response.ok) { setError('This job has no retry attempts remaining.'); return }
    setAnalysis(null); setPreview(null); setConfirmed(false); await refreshJob()
  }

  async function createPreview() {
    if (!job || !analysis) return
    setBusy(true); setError(null); setConfirmed(false)
    try {
      const response = await fetch(`/api/reconstruction/jobs/${job.id}/preview`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ targetSceneId: updatingExisting ? scene.sceneId : null, expectedRevision: updatingExisting ? scene.revision : null, analysis }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Preview failed (${response.status})`))
      setPreview(body as Preview)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to prepare scene preview') }
    finally { setBusy(false) }
  }

  async function applyPreview() {
    if (!job || !preview || !confirmed) return
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/reconstruction/jobs/${job.id}/apply`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ confirmed: true, expectedRevision: preview.baseRevision }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Scene apply failed (${response.status})`))
      const result = body as Applied
      onApplied(JSON.parse(result.document) as SceneDocument)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Unable to apply the reviewed scene') }
    finally { setBusy(false) }
  }

  return <main className="reconstruction-workspace">
    <header className="reconstruction-header">
      <div><span className="eyebrow">ROOMFORGE · PHASE 03 + 04 FOUNDATION</span><h1>Reconstruct from a room photo</h1></div>
      <button className="quiet-button" onClick={onClose}>Back to scene editor</button>
    </header>
    <div className="reconstruction-grid">
      <section className="reconstruction-card upload-card">
        <span className="field-label">1 · SOURCE IMAGE</span>
        <label className="room-image-upload">Choose a room image (PNG, JPEG or WebP, max 10 MB)
          <input aria-label="Room image" type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => { setFile(event.currentTarget.files?.[0] ?? null); setImage(null); setAnalysis(null); setPreview(null) }} />
        </label>
        {file && <p className="file-name">{file.name} · {Math.ceil(file.size / 1024)} KB</p>}
        <button className="save-button" onClick={() => void uploadAndAnalyze()} disabled={!file || busy || isActive}>{busy ? 'Working…' : image ? 'Upload another image' : 'Upload and analyze (mock)'}</button>
        {image && <div className="source-image-wrap"><img src={image.url} alt="Uploaded room source" /><span>{image.width} × {image.height} px · validated and stored</span></div>}
        <p className="mock-notice">This run uses deterministic mock analysis. It is not real AI inference. Camera intrinsics and absolute scale remain unknown until reviewed.</p>
      </section>

      <section className="reconstruction-card job-card" aria-label="Reconstruction job status">
        <span className="field-label">2 · DURABLE RECONSTRUCTION JOB</span>
        {!job && <p>Upload an image to create a persistent job.</p>}
        {job && <>
          <div className={`job-status status-${job.status}`}><strong>{job.status}</strong><span>{job.stage.replaceAll('_', ' ')}</span></div>
          <progress aria-label="Reconstruction progress" max={100} value={job.progress} />
          <div className="job-meta"><span>{job.progress}%</span><span>Attempt {job.attemptCount}/{job.maxAttempts}</span><span>{job.id}</span></div>
          {isActive && <button className="quiet-button" onClick={() => void cancelJob()}>Cancel job</button>}
          {job.status === 'failed' && job.attemptCount < job.maxAttempts && <button className="quiet-button" onClick={() => void retryJob()}>Retry job</button>}
          {job.errorMessage && <p className="reconstruction-error" role="alert">{job.errorCode}: {job.errorMessage}</p>}
          {job.status === 'succeeded' && <p className="mock-provenance">Completed by {analysis?.providerId ?? 'roomforge-mock'} · deterministic mock output</p>}
        </>}
        <div className="capability-list" aria-label="Available reconstruction capabilities">
          {capabilities.map((capability) => <div key={capability.name}><span className={capability.available ? 'capability-dot available' : 'capability-dot'} />
            <strong>{capability.name.replaceAll('-', ' ')}</strong><small>{capability.available ? 'mock' : 'unavailable'}</small></div>)}
        </div>
      </section>

      {analysis && <section className="reconstruction-card review-card">
        <span className="field-label">3 · REVIEW ANALYSIS · {analysis.providerId} / {analysis.providerVersion}</span>
        {image && <div className="analysis-image"><img src={image.url} alt="Room with mock detected regions" /><svg viewBox={`0 0 ${analysis.imageWidth} ${analysis.imageHeight}`} role="img" aria-label="Detected room regions">
          {analysis.surfaces.map((surface) => <polygon key={surface.id} points={surface.polygon.points.map((point) => `${point.x},${point.y}`).join(' ')} className="surface-polygon" />)}
          {analysis.objects.map((item) => <polygon key={item.id} points={item.polygon.points.map((point) => `${point.x},${point.y}`).join(' ')} className="object-polygon" />)}
        </svg></div>}
        <div className="analysis-review-list">
          {analysis.surfaces.map((surface) => <div className="analysis-row" key={surface.id}><strong>{surface.semanticRole.replaceAll('_', ' ')}</strong><span>{surface.confidence.toFixed(2)} confidence · {surface.provenance}</span>
            <small>{surface.estimatedWidthMeters ?? 'unknown'} × {surface.estimatedHeightMeters ?? 'unknown'} m estimate</small>
            <label>Match existing surface (optional)<select aria-label={`Match surface ${surface.semanticRole}`} value={surface.linkedSurfaceId ?? ''} disabled={!updatingExisting} onChange={(event) => updateSurface(surface.id, event.currentTarget.value || null)}>
              <option value="">Create a new surface with a new ID</option>{scene.objects.flatMap((object) => object.surfaces.map((entry) => <option key={entry.id} value={entry.id}>{entry.semanticRole} · {object.name} · preserve ID</option>))}
            </select></label></div>)}
          {analysis.objects.map((item) => <article className={`detected-object${item.included ? '' : ' excluded'}`} key={item.id}>
            <label className="include-toggle"><input aria-label={`Include ${item.name}`} type="checkbox" checked={item.included} onChange={(event) => {
              const included = event.currentTarget.checked
              updateObject(item.id, (current) => ({ ...current, included }))
            }} /> Include</label>
            <label>Object type<input aria-label={`Object type ${item.name}`} value={item.type} onChange={(event) => {
              const type = event.currentTarget.value
              updateObject(item.id, (current) => ({ ...current, type }))
            }} /></label>
            <label>Display name<input aria-label={`Object name ${item.name}`} value={item.name} onChange={(event) => {
              const name = event.currentTarget.value
              updateObject(item.id, (current) => ({ ...current, name }))
            }} /></label>
            <div className="detected-dimensions"><span className="field-label">DIMENSIONS (METERS · ESTIMATED)</span><div>{(['Width', 'Height', 'Depth'] as const).map((dimension, index) => <label key={dimension}>{dimension}<input aria-label={`${dimension} ${item.name}`} type="number" min="0.01" max="100" step="0.01" value={item.dimensionsMeters[index]} onChange={(event) => {
              const value = Number(event.currentTarget.value)
              updateObject(item.id, (current) => {
              const dimensionsMeters = [...current.dimensionsMeters] as [number, number, number]
              dimensionsMeters[index] = value
              return { ...current, dimensionsMeters }
              })
            }} /></label>)}</div></div>
            <label>Match existing object (optional)<select aria-label={`Match object ${item.name}`} value={item.linkedObjectId ?? ''} disabled={!updatingExisting} onChange={(event) => {
              const linkedObjectId = event.currentTarget.value || null
              updateObject(item.id, (current) => ({ ...current, linkedObjectId }))
            }}>
              <option value="">Create a new object with a new ID</option>{scene.objects.map((existing) => <option key={existing.id} value={existing.id}>{existing.name} · preserve ID</option>)}
            </select></label>
            <small>{item.confidence.toFixed(2)} confidence · {item.provenance} · ObjectId {item.linkedObjectId ?? 'created on apply'}</small>
          </article>)}
        </div>
        <div className="assumption-review">
          <span className="field-label">CAMERA AND SCALE ASSUMPTIONS</span>
          <label>Camera model<input aria-label="Camera assumption" value={analysis.assumptions.cameraModel} onChange={(event) => updateAssumptions({ cameraModel: event.currentTarget.value })} /></label>
          <p>Enter a known span and its image length to record a scale anchor. Perspective and hidden geometry remain uncertain; review object dimensions separately.</p>
          <div className="assumption-fields"><label>Reference length (pixels)<input aria-label="Reference pixel length" type="number" min="0.01" step="1" value={analysis.assumptions.referencePixelLength ?? ''} onChange={(event) => updateCalibration(event.currentTarget.value ? Number(event.currentTarget.value) : null, analysis.assumptions.referenceLengthMeters)} /></label>
            <label>Known length (meters)<input aria-label="Known reference length" type="number" min="0.01" step="0.01" value={analysis.assumptions.referenceLengthMeters ?? ''} onChange={(event) => updateCalibration(analysis.assumptions.referencePixelLength, event.currentTarget.value ? Number(event.currentTarget.value) : null)} /></label>
            <label>Derived meters per pixel<input aria-label="Meters per pixel" type="number" value={analysis.assumptions.metersPerPixel ?? ''} readOnly /></label></div>
          <label>Scale status<select aria-label="Scale assumption" value={analysis.assumptions.scaleStatus} onChange={(event) => updateAssumptions({ scaleStatus: event.currentTarget.value })}><option value="unknown">Unknown</option><option value="estimated">Estimated</option><option value="user_supplied">User supplied</option><option value="calibrated">Calibrated</option></select></label>
          <label>Review notes<textarea aria-label="Assumption notes" value={analysis.assumptions.notes.join('\n')} onChange={(event) => updateAssumptions({ notes: event.currentTarget.value.split('\n').filter(Boolean) })} /></label>
        </div>
        <label className="target-toggle"><input type="checkbox" checked={updatingExisting} onChange={(event) => {
          const checked = event.currentTarget.checked
          setUpdatingExisting(checked); setPreview(null); setConfirmed(false)
          if (!checked) setAnalysis((current) => current ? {
            ...current,
            objects: current.objects.map((item) => ({ ...item, linkedObjectId: null })),
            surfaces: current.surfaces.map((item) => ({ ...item, linkedSurfaceId: null })),
          } : current)
        }} /> Update current scene (revision {scene.revision}); only explicitly matched objects are changed.</label>
        <button className="save-button" onClick={() => void createPreview()} disabled={busy}>{busy ? 'Building preview…' : 'Preview candidate scene'}</button>
      </section>}

      {preview && <section className="reconstruction-card preview-card" aria-label="Reconstruction scene preview">
        <span className="field-label">4 · PREVIEW · SCENE HAS NOT BEEN SAVED</span>
        <p>{preview.createsNewScene ? 'A new scene will be created.' : 'The existing scene will update only after confirmation.'} Base revision {preview.baseRevision}.</p>
        <div className="candidate-view"><Suspense fallback={<p>Preparing 3D preview…</p>}><SceneViewer scene={JSON.parse(preview.document) as SceneDocument} selectedSurfaceId={null} selectedObjectId={null} transformMode="translate" onSelect={() => undefined} onSelectObject={() => undefined} onTransform={() => undefined} /></Suspense></div>
        <label className="confirm-toggle"><input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.currentTarget.checked)} /> I reviewed this candidate and explicitly approve saving it as a Scene JSON revision.</label>
        <button className="save-button" onClick={() => void applyPreview()} disabled={!confirmed || busy}>{busy ? 'Applying…' : preview.createsNewScene ? 'Confirm and create new scene' : 'Confirm and update scene'}</button>
      </section>}
      {error && <p className="reconstruction-error" role="alert">{error}</p>}
    </div>
  </main>
}
