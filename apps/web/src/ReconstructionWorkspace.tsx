import { lazy, Suspense, useCallback, useEffect, useRef, useState } from 'react'
import type { SceneAnalysisResult } from './reconstructionTypes'
import type { ReconstructionAssumptions } from './reconstructionTypes'
import type { SceneDocument } from './sceneTypes'
import { AnnotationEditor } from './AnnotationEditor'
import type { AnnotationDocument, UpdateAnnotationDocument } from './AnnotationEditor'
import './ReconstructionWorkspace.css'

const SceneViewer = lazy(() => import('./SceneViewer').then((module) => ({ default: module.SceneViewer })))

type Props = { scene: SceneDocument; onClose: () => void; onApplied: (scene: SceneDocument) => void }
type UploadedImage = { imageId: string; url: string; width: number; height: number; contentType: string }
const jobStatusLabels: Record<string, string> = { queued: 'Đang chờ', processing: 'Đang xử lý', succeeded: 'Hoàn tất', failed: 'Thất bại', cancelled: 'Đã hủy', completed: 'Hoàn tất' }
const stageLabels: Record<string, string> = { queued: 'Đang chờ', uploading: 'Đang tải ảnh', segmentation: 'Phân vùng ảnh', analyzing: 'Phân tích ảnh', normalizing: 'Chuẩn hóa kết quả', completed: 'Hoàn tất', succeeded: 'Hoàn tất' }
type Job = { id: string; status: string; stage: string; progress: number; attemptCount: number; maxAttempts: number; errorCode: string | null; errorMessage: string | null; analysisJson: string | null }
type ObjectMeshJob = { id: string; objectId: string; sceneId: string; expectedSceneRevision: number; status: string; submissionState: string; providerJobId: string | null; outputAssetStorageId: string | null; errorCode: string | null; errorMessage: string | null; progress: number }
type Preview = { sceneId: string; baseRevision: number; document: string; createsNewScene: boolean }
type Applied = { sceneId: string; revision: number; created: boolean; document: string }
type TopologyEdge = { id: string; regionId: string; start: { x: number; y: number }; end: { x: number; y: number }; role: string; provenance: string }
type RoomTopology = { id: string; revision: number; annotationRevision: number; edges: TopologyEdge[]; diagnostics: string[] }
type GeometryResult = { geometrySpec: { instances: { stableId: string; name: string; category: string; positionMeters: number[]; rotationQuaternion: number[]; visible: boolean; dimensions: { meters: number | null; status: string; provenance: string }[] }[]; diagnostics: string[] } | null; stageResults: { stage: string; status: string; diagnostics: string[]; assumptions: string[] }[] }
type GeometryCandidate = { candidateId: string; baseSceneRevision: number; geometrySpec: NonNullable<GeometryResult['geometrySpec']> & { instances: (NonNullable<GeometryResult['geometrySpec']>['instances'][number] & { category: string; positionMeters: number[]; rotationQuaternion: number[]; visible: boolean })[] } }
type AlignmentCameraDraft = { fx: string; fy: string; cx: string; cy: string; position: [string, string, string]; rotation: [string, string, string, string] }
type AlignmentResult = { alignment: { rmsErrorPixels: number; visibleSampleCount: number; unprojectableSampleCount: number; uncertaintyNotes: string[] }; diagnostics: string[] }
const topologyRoles = ['unknown_candidate', 'floor_wall', 'wall_wall', 'wall_ceiling', 'opening', 'occluded']
const topologyRoleLabels: Record<string, string> = { unknown_candidate: 'Chưa xác định', floor_wall: 'Giao tuyến sàn – tường', wall_wall: 'Giao tuyến hai tường', wall_ceiling: 'Giao tuyến tường – trần', opening: 'Ô cửa', occluded: 'Bị che khuất' }

function errorText(body: unknown, fallback: string) {
  if (body && typeof body === 'object' && 'detail' in body && typeof body.detail === 'string') return body.detail
  if (body && typeof body === 'object' && 'error' in body && typeof body.error === 'string') return body.error
  return fallback
}

function getCropBounds(points: { x: number; y: number }[]) {
  const xs = points.map((point) => point.x)
  const ys = points.map((point) => point.y)
  const x = Math.max(0, Math.floor(Math.min(...xs)))
  const y = Math.max(0, Math.floor(Math.min(...ys)))
  return { x, y, width: Math.max(0, Math.ceil(Math.max(...xs)) - x), height: Math.max(0, Math.ceil(Math.max(...ys)) - y) }
}

export function ReconstructionWorkspace({ scene, onClose, onApplied }: Props) {
  const [file, setFile] = useState<File | null>(null)
  const [image, setImage] = useState<UploadedImage | null>(null)
  const [annotation, setAnnotation] = useState<AnnotationDocument | null>(null)
  const [topology, setTopology] = useState<RoomTopology | null>(null)
  const [alignmentCamera, setAlignmentCamera] = useState<AlignmentCameraDraft>({ fx: '', fy: '', cx: '', cy: '', position: ['', '', ''], rotation: ['0', '0', '0', '1'] })
  const [alignmentWorldEdges, setAlignmentWorldEdges] = useState<Record<string, [string, string, string, string, string, string]>>({})
  const [alignmentResult, setAlignmentResult] = useState<AlignmentResult | null>(null)
  const [geometryFloorId, setGeometryFloorId] = useState('')
  const [geometrySpanId, setGeometrySpanId] = useState('')
  const [geometryResult, setGeometryResult] = useState<GeometryResult | null>(null)
  const [geometryOverrides, setGeometryOverrides] = useState<Record<string, string>>({})
  const [geometryCandidate, setGeometryCandidate] = useState<GeometryCandidate | null>(null)
  const [confirmGeometryApply, setConfirmGeometryApply] = useState(false)
  const [job, setJob] = useState<Job | null>(null)
  const [objectMeshJobs, setObjectMeshJobs] = useState<Record<string, ObjectMeshJob>>({})
  const [meshGenerationBusyId, setMeshGenerationBusyId] = useState<string | null>(null)
  const [meshApplyConfirmed, setMeshApplyConfirmed] = useState<Record<string, boolean>>({})
  const [analysis, setAnalysis] = useState<SceneAnalysisResult | null>(null)
  const [preview, setPreview] = useState<Preview | null>(null)
  const [updatingExisting, setUpdatingExisting] = useState(false)
  const [confirmed, setConfirmed] = useState(false)
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const workspaceLoadGeneration = useRef(0)
  const [capabilities, setCapabilities] = useState<{ name: string; available: boolean; providerId: string; description: string }[]>([])
  const meshCapability = capabilities.find((capability) => capability.name === 'image-to-3d')
  const isActive = job?.status === 'queued' || job?.status === 'processing'
  const existingObjectIds = new Set(scene.objects.map((object) => object.id))
  const geometryPreviewScene = geometryCandidate ? {
    ...scene,
    objects: [...scene.objects, ...geometryCandidate.geometrySpec.instances.filter((instance) => !existingObjectIds.has(instance.stableId)).flatMap((instance) => {
      const dimensions = instance.dimensions.map((dimension) => dimension.meters)
      if (dimensions.length !== 3 || dimensions.some((value) => value === null)) return []
      return [{ id: instance.stableId, name: instance.name, category: instance.category, geometry: { kind: 'box' as const },
        position: instance.positionMeters as [number, number, number], rotation: instance.rotationQuaternion as [number, number, number, number],
        dimensions: dimensions as [number, number, number], visible: instance.visible, locked: false,
        measurementStatus: instance.dimensions.some((dimension) => dimension.status === 'estimated') ? 'estimated' : 'measured', surfaces: [] }]
    })],
  } satisfies SceneDocument : null

  const refreshJob = useCallback(async () => {
    if (!job) return
    const response = await fetch(`/api/reconstruction/jobs/${job.id}`)
    if (!response.ok) throw new Error(`Không lấy được trạng thái tác vụ (${response.status})`)
    const current = await response.json() as Job
    setJob(current)
    if (current.status === 'succeeded' && current.analysisJson) {
      setAnalysis(JSON.parse(current.analysisJson) as SceneAnalysisResult)
      setError(null)
    }
  }, [job])

  useEffect(() => {
    const active = Object.values(objectMeshJobs).filter((meshJob) => meshJob.status === 'queued' || meshJob.status === 'processing')
    if (active.length === 0) return
    const timer = window.setInterval(() => {
      void Promise.all(active.map(async (meshJob) => {
        const response = await fetch(`/api/object-mesh-jobs/${meshJob.id}`)
        if (!response.ok) return null
        return await response.json() as ObjectMeshJob
      })).then((updated) => {
        setObjectMeshJobs((current) => {
          const next = { ...current }
          for (const item of updated) if (item) next[item.objectId] = item
          return next
        })
      }).catch(() => undefined)
    }, 2000)
    return () => window.clearInterval(timer)
  }, [objectMeshJobs])

  async function createObjectMesh(item: SceneAnalysisResult['objects'][number]) {
    if (!job || !image) return
    const objectId = item.linkedObjectId ?? item.id
    const existing = scene.objects.find((object) => object.id === objectId)
    if (!existing || existing.locked || ['architecture', 'structure'].includes(existing.category)) {
      setError('Hãy áp dụng đồ vật có thể di chuyển này vào cảnh trước khi tạo mô hình.')
      return
    }
    const bounds = getCropBounds(item.polygon.points)
    const right = Math.min(image.width, bounds.x + bounds.width)
    const bottom = Math.min(image.height, bounds.y + bounds.height)
    setMeshGenerationBusyId(objectId)
    setError(null)
    try {
      const response = await fetch(`/api/reconstruction/jobs/${job.id}/objects/${objectId}/mesh`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sceneId: scene.sceneId, objectId, expectedSceneRevision: scene.revision,
          crop: { x: bounds.x, y: bounds.y, width: right - bounds.x, height: bottom - bounds.y }, idempotencyKey: crypto.randomUUID(), confirmed: true }),
      })
      const body = await response.json().catch(() => null) as ObjectMeshJob | { detail?: string; error?: string } | null
      if (!response.ok) throw new Error(errorText(body, `Yêu cầu tạo mô hình thất bại (${response.status})`))
      const meshJob = body as ObjectMeshJob
      setObjectMeshJobs((current) => ({ ...current, [objectId]: meshJob }))
      setMeshApplyConfirmed((current) => ({ ...current, [objectId]: false }))
      setError(null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Không tạo được tác vụ dựng mô hình')
    } finally { setMeshGenerationBusyId(null) }
  }

  async function applyObjectMesh(objectId: string, meshJob: ObjectMeshJob) {
    if (!meshApplyConfirmed[objectId]) return
    setError(null)
    try {
      const response = await fetch(`/api/object-mesh-jobs/${meshJob.id}/apply`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ confirmed: true, expectedRevision: scene.revision }),
      })
      const body = await response.json().catch(() => null) as Applied | { detail?: string; error?: string } | null
      if (!response.ok) throw new Error(errorText(body, `Gắn mô hình thất bại (${response.status})`))
      onApplied(JSON.parse((body as Applied).document) as SceneDocument)
      setError(null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Không gắn được mô hình đã tạo')
    }
  }

  useEffect(() => {
    let cancelled = false
    void fetch('/api/reconstruction/capabilities').then((response) => response.ok ? response.json() : []).then((items) => {
      if (!cancelled) setCapabilities(items as typeof capabilities)
    }).catch(() => undefined)
    return () => { cancelled = true }
  }, [])

  useEffect(() => {
    let cancelled = false
    const generation = ++workspaceLoadGeneration.current
    const key = `roomforge:last-annotation:${scene.projectId}`
    const annotationId = window.localStorage.getItem(key)
    if (!annotationId) return () => { cancelled = true }
    void fetch(`/api/projects/${scene.projectId}/annotations/${annotationId}`)
      .then(async (response) => {
        if (response.status === 404) window.localStorage.removeItem(key)
        return response.ok ? await response.json() as AnnotationDocument : null
      })
      .then((document) => {
        if (!document || cancelled || generation !== workspaceLoadGeneration.current) return
        setAnnotation(document)
        setImage({ imageId: document.sourceImageId, url: `/api/assets/images/${document.sourceImageId}`,
          width: document.imageWidth, height: document.imageHeight, contentType: 'application/octet-stream' })
      })
      .catch(() => { if (!cancelled) window.localStorage.removeItem(key) })
    return () => { cancelled = true }
  }, [scene.projectId])

  useEffect(() => {
    if (!annotation) return
    let cancelled = false
    const key = `roomforge:topology:${annotation.annotationId}`
    const topologyId = window.localStorage.getItem(key)
    if (!topologyId) return () => { cancelled = true }
    void fetch(`/api/projects/${scene.projectId}/topologies/${topologyId}`)
      .then(async (response) => response.ok ? await response.json() as RoomTopology : null)
      .then((loaded) => {
        if (cancelled) return
        if (!loaded || loaded.annotationRevision !== annotation.revision) {
          window.localStorage.removeItem(key); setTopology(null); return
        }
        setTopology(loaded)
      })
      .catch(() => { if (!cancelled) setTopology(null) })
    return () => { cancelled = true }
  }, [annotation, scene.projectId])

  useEffect(() => {
    if (!annotation) return
    let cancelled = false
    const candidateId = window.localStorage.getItem(`roomforge:geometry-candidate:${annotation.annotationId}`)
    if (!candidateId) return () => { cancelled = true }
    void fetch(`/api/projects/${scene.projectId}/geometry-candidates/${candidateId}`)
      .then(async (response) => response.ok ? await response.json() as GeometryCandidate : null)
      .then((candidate) => { if (!cancelled && candidate) setGeometryCandidate(candidate) })
      .catch(() => undefined)
    return () => { cancelled = true }
  }, [annotation, scene.projectId])

  useEffect(() => {
    if (!isActive) return
    const timer = window.setInterval(() => { void refreshJob().catch((cause: unknown) => setError(cause instanceof Error ? cause.message : 'Không cập nhật được trạng thái tác vụ')) }, 700)
    return () => window.clearInterval(timer)
  }, [isActive, refreshJob])

  async function uploadAndAnalyze() {
    if (!file) return
    workspaceLoadGeneration.current++
    setBusy(true); setError(null); setPreview(null); setAnalysis(null); setJob(null); setAnnotation(null); setTopology(null)
    try {
      const storedImage = await uploadSelectedImage()
      const created = await fetch(`/api/projects/${scene.projectId}/reconstruction-jobs`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ imageId: storedImage.imageId, idempotencyKey: crypto.randomUUID() }),
      })
      const createdBody = await created.json().catch(() => null)
      if (!created.ok) throw new Error(errorText(createdBody, `Không bắt đầu được quá trình dựng cảnh (${created.status})`))
      setJob(createdBody as Job)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không tải lên hoặc phân tích được ảnh') }
    finally { setBusy(false) }
  }

  async function uploadForManualAnnotation() {
    if (!file) return
    workspaceLoadGeneration.current++
    window.localStorage.removeItem(`roomforge:last-annotation:${scene.projectId}`)
    setBusy(true); setError(null); setPreview(null); setAnalysis(null); setJob(null); setAnnotation(null); setTopology(null)
    try {
      const storedImage = await uploadSelectedImage()
      const created = await fetch(`/api/projects/${scene.projectId}/annotations`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ sourceImageId: storedImage.imageId }),
      })
      const body = await created.json().catch(() => null)
      if (!created.ok) throw new Error(errorText(body, `Không tạo được chú thích (${created.status})`))
      const document = body as AnnotationDocument
      setAnnotation(document)
      window.localStorage.setItem(`roomforge:last-annotation:${scene.projectId}`, document.annotationId)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không chuẩn bị được chú thích thủ công') }
    finally { setBusy(false) }
  }

  async function uploadSelectedImage() {
    if (!file) throw new Error('Trước tiên hãy chọn một ảnh.')
    const data = new FormData()
    data.append('image', file)
    const uploaded = await fetch(`/api/projects/${scene.projectId}/images`, { method: 'POST', body: data })
    const body = await uploaded.json().catch(() => null)
    if (!uploaded.ok) throw new Error(errorText(body, `Tải ảnh lên thất bại (${uploaded.status})`))
    const storedImage = body as UploadedImage
    setImage(storedImage)
    return storedImage
  }

  async function saveAnnotation(update: UpdateAnnotationDocument) {
    if (!annotation) return
    const response = await fetch(`/api/projects/${scene.projectId}/annotations/${annotation.annotationId}`, {
      method: 'PUT', headers: { 'Content-Type': 'application/json', 'If-Match': `"${annotation.revision}"` },
      body: JSON.stringify(update),
    })
    const body = await response.json().catch(() => null)
    if (response.status === 409) throw new Error(`Chú thích đã được thay đổi trên máy chủ (phiên bản ${body?.currentRevision ?? 'không rõ'}). Hãy tải lại ảnh trước khi chỉnh sửa tiếp.`)
    if (!response.ok) throw new Error(errorText(body, `Lưu chú thích thất bại (${response.status})`))
    setAnnotation(body as AnnotationDocument)
    setTopology(null)
    setGeometryResult(null)
    setGeometryCandidate(null); setConfirmGeometryApply(false)
    window.localStorage.removeItem(`roomforge:geometry-candidate:${annotation.annotationId}`)
  }

  async function createTopology() {
    if (!annotation) return
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/annotations/${annotation.annotationId}/topology`, { method: 'POST' })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Tạo cấu trúc thất bại (${response.status})`))
      const created = body as RoomTopology
      setTopology(created)
      window.localStorage.setItem(`roomforge:topology:${annotation.annotationId}`, created.id)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không tạo được cấu trúc phòng') }
    finally { setBusy(false) }
  }

  function updateTopologyEdge(edgeId: string, role: string) {
    setTopology((current) => current ? { ...current, edges: current.edges.map((edge) => edge.id === edgeId ? { ...edge, role, provenance: 'user_supplied' } : edge) } : current)
  }

  async function saveTopology() {
    if (!topology) return
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/topologies/${topology.id}`, {
        method: 'PATCH', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ expectedRevision: topology.revision, edges: topology.edges.filter((edge) => edge.provenance === 'user_supplied').map(({ id, role }) => ({ edgeId: id, role })) }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Lưu cấu trúc thất bại (${response.status})`))
      setTopology(body as RoomTopology)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không lưu được chỉnh sửa giao tuyến') }
    finally { setBusy(false) }
  }

  async function verifyPhotoAlignment() {
    if (!annotation || !topology) return
    const cameraValues = [alignmentCamera.fx, alignmentCamera.fy, alignmentCamera.cx, alignmentCamera.cy, ...alignmentCamera.position, ...alignmentCamera.rotation].map(Number)
    if (cameraValues.some((value) => !Number.isFinite(value)) || cameraValues[0]! <= 0 || cameraValues[1]! <= 0) {
      setError('Enter finite thông số nội tại máy ảnh, position and quaternion values before checking alignment.')
      return
    }
    const worldEdges = topology.edges.flatMap((edge) => {
      const coordinates = alignmentWorldEdges[edge.id]
      if (!coordinates || coordinates.some((value) => value.trim() === '' || !Number.isFinite(Number(value)))) return []
      return [{ edgeId: edge.id, startMeters: coordinates.slice(0, 3).map(Number), endMeters: coordinates.slice(3).map(Number) }]
    })
    if (worldEdges.length === 0) { setError('Hãy nhập tọa độ theo mét của ít nhất một giao tuyến.'); return }
    setBusy(true); setError(null); setAlignmentResult(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/annotations/${annotation.annotationId}/topologies/${topology.id}/alignment`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ camera: { id: crypto.randomUUID(), fx: cameraValues[0], fy: cameraValues[1], cx: cameraValues[2], cy: cameraValues[3], positionMeters: cameraValues.slice(4, 7), rotationCameraToWorld: cameraValues.slice(7, 11), intrinsicsStatus: 'user_hypothesis', provenance: 'user_supplied' }, worldEdges }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Kiểm tra căn chỉnh ảnh thất bại (${response.status})`))
      setAlignmentResult(body as AlignmentResult)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không kiểm tra được căn chỉnh ảnh') }
    finally { setBusy(false) }
  }

  function setAlignmentEdgeCoordinate(edgeId: string, index: number, value: string) {
    setAlignmentWorldEdges((current) => {
      const coordinates = current[edgeId] ?? ['', '', '', '', '', '']
      const updated = [...coordinates] as [string, string, string, string, string, string]
      updated[index] = value
      return { ...current, [edgeId]: updated }
    })
  }

  function setAlignmentCameraValue(field: 'fx' | 'fy' | 'cx' | 'cy', value: string) {
    setAlignmentCamera((current) => ({ ...current, [field]: value }))
  }

  function setAlignmentCameraVector(kind: 'position' | 'rotation', index: number, value: string) {
    setAlignmentCamera((current) => {
      if (kind === 'position') {
        const position = [...current.position] as [string, string, string]
        position[index] = value
        return { ...current, position }
      }
      const rotation = [...current.rotation] as [string, string, string, string]
      rotation[index] = value
      return { ...current, rotation }
    })
  }

  async function previewGeometry() {
    if (!annotation || !topology) return
    const floorRegionId = geometryFloorId || annotation.regions.find((region) => region.semanticRole === 'floor')?.id
    const calibrationSpanId = geometrySpanId || annotation.referenceSpans[0]?.id
    if (!floorRegionId || !calibrationSpanId) { setError('Add a floor polygon and a measured reference span before estimating geometry.'); return }
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/annotations/${annotation.annotationId}/geometry`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ topologyId: topology.id, floorRegionId, calibrationSpanId }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Xem trước hình học thất bại (${response.status})`))
      setGeometryResult(body as GeometryResult)
      setGeometryCandidate(null); setConfirmGeometryApply(false)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không ước lượng được hình học') }
    finally { setBusy(false) }
  }

  async function createGeometryCandidate() {
    if (!annotation || !topology || !geometryResult?.geometrySpec) return
    const floorRegionId = geometryFloorId || annotation.regions.find((region) => region.semanticRole === 'floor')?.id
    const calibrationSpanId = geometrySpanId || annotation.referenceSpans[0]?.id
    if (!floorRegionId || !calibrationSpanId) return
    const dimensionOverrides = Object.entries(geometryOverrides).flatMap(([key, value]) => {
      const meters = Number(value)
      if (!Number.isFinite(meters) || meters <= 0) return []
      const [instanceId, dimensionIndex] = key.split(':')
      return [{ instanceId, dimensionIndex: Number(dimensionIndex), meters }]
    })
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/annotations/${annotation.annotationId}/geometry-candidates`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ topologyId: topology.id, sceneId: scene.sceneId, expectedSceneRevision: scene.revision, floorRegionId, calibrationSpanId, dimensionOverrides }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Lưu phương án thất bại (${response.status})`))
      const candidate = body as GeometryCandidate
      setGeometryCandidate(candidate); setConfirmGeometryApply(false)
      window.localStorage.setItem(`roomforge:geometry-candidate:${annotation.annotationId}`, candidate.candidateId)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không lưu được phương án hình học') }
    finally { setBusy(false) }
  }

  async function applyGeometryCandidate() {
    if (!geometryCandidate || !confirmGeometryApply) return
    setBusy(true); setError(null)
    try {
      const response = await fetch(`/api/projects/${scene.projectId}/geometry-candidates/${geometryCandidate.candidateId}/apply`, {
        method: 'POST', headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ confirmed: true, expectedRevision: geometryCandidate.baseSceneRevision }),
      })
      const body = await response.json().catch(() => null)
      if (!response.ok) throw new Error(errorText(body, `Áp dụng hình học thất bại (${response.status})`))
      onApplied(JSON.parse(body.document) as SceneDocument)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không áp dụng được phương án hình học') }
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

  function updateSurfacePolygon(id: string, pointIndex: number, axis: 'x' | 'y', rawValue: string) {
    const value = Number(rawValue)
    if (!Number.isFinite(value)) return
    setAnalysis((current) => current ? { ...current, surfaces: current.surfaces.map((surface) => {
      if (surface.id !== id) return surface
      const points = surface.polygon.points.map((point, index) => index === pointIndex ? { ...point, [axis]: value } : point)
      return { ...surface, polygon: { points }, provenance: surface.provenance.includes('user-corrected') ? surface.provenance : `${surface.provenance}; user-corrected` }
    }) } : current)
    setPreview(null); setConfirmed(false)
  }

  function updateObjectPolygon(id: string, pointIndex: number, axis: 'x' | 'y', rawValue: string) {
    const value = Number(rawValue)
    if (!Number.isFinite(value)) return
    setAnalysis((current) => current ? { ...current, objects: current.objects.map((item) => {
      if (item.id !== id) return item
      const points = item.polygon.points.map((point, index) => index === pointIndex ? { ...point, [axis]: value } : point)
      return { ...item, polygon: { points }, provenance: item.provenance.includes('user-corrected') ? item.provenance : `${item.provenance}; user-corrected` }
    }) } : current)
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
      if (!response.ok) throw new Error(errorText(body, `Xem trước thất bại (${response.status})`))
      setPreview(body as Preview)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không chuẩn bị được bản xem trước cảnh') }
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
      if (!response.ok) throw new Error(errorText(body, `Áp dụng cảnh thất bại (${response.status})`))
      const result = body as Applied
      onApplied(JSON.parse(result.document) as SceneDocument)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không áp dụng được cảnh đã duyệt') }
    finally { setBusy(false) }
  }

  return <main className="reconstruction-workspace">
    <header className="reconstruction-header">
      <div><span className="eyebrow">ROOMFORGE · DỰNG CẢNH TỪ ẢNH</span><h1>Dựng cảnh từ ảnh căn phòng</h1></div>
      <button className="quiet-button" onClick={onClose}>Quay lại trình chỉnh sửa</button>
    </header>
    <div className="reconstruction-grid">
      <section className="reconstruction-card upload-card">
        <span className="field-label">1 · ẢNH NGUỒN</span>
        <label className="room-image-upload">Chọn ảnh căn phòng (PNG, JPEG hoặc WebP, tối đa 10 MB)
          <input aria-label="Ảnh căn phòng" type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => { setFile(event.currentTarget.files?.[0] ?? null); setImage(null); setAnnotation(null); setAnalysis(null); setPreview(null) }} />
        </label>
        {file && <p className="file-name">{file.name} Â· {Math.ceil(file.size / 1024)} KB</p>}
        <div className="upload-actions">
          <button className="save-button" onClick={() => void uploadAndAnalyze()} disabled={!file || busy || isActive}>{busy ? 'Workingâ€¦' : 'Tải lên và phân tích thử'}</button>
          <button className="quiet-button" onClick={() => void uploadForManualAnnotation()} disabled={!file || busy || isActive}>{busy ? 'Workingâ€¦' : 'Tải lên để chú thích thủ công'}</button>
        </div>
        {image && <div className="source-image-wrap"><img src={image.url} alt="Ảnh căn phòng đã tải lên" /><span>{image.width} Ã— {image.height} px Â· đã kiểm tra và lưu</span></div>}
        <p className="mock-notice">Lần chạy này dùng phân tích mô phỏng có kết quả cố định, không phải AI thật. Thông số máy ảnh và tỷ lệ kích thước thực vẫn chưa xác định cho đến khi được kiểm tra.</p>
      </section>

      {image && annotation && <section className="reconstruction-card manual-annotation-card">
        <span className="field-label">CHÚ THÍCH THỦ CÔNG · PHIÊN BẢN {annotation.revision}</span>
        <p>Tọa độ và đoạn tham chiếu được lưu theo điểm ảnh của ảnh nguồn. Kích thước do người dùng nhập.</p>
        <AnnotationEditor key={`${annotation.annotationId}:${annotation.revision}`} image={image} annotation={annotation} onSave={saveAnnotation} />
        <section className="topology-editor" aria-label="Chỉnh sửa cấu trúc phòng">
          <div className="topology-heading"><strong>Các giao tuyến trong phòng</strong><button className="quiet-button" onClick={() => void createTopology()} disabled={busy}>Tạo giao tuyến từ chú thích hiện tại</button></div>
          {!topology && <p>Các giao tuyến chưa được xác định cho đến khi bạn phân loại.</p>}
          {topology && <>
            <p>Phiên bản giao tuyến {topology.revision}; phiên bản chú thích nguồn {topology.annotationRevision}. Hãy lưu chú thích trước nếu có thay đổi.</p>
            {topology.diagnostics.map((diagnostic) => <small key={diagnostic}>{diagnostic}</small>)}
            <section className="photo-alignment-editor" aria-label="Photo alignment controls">
              <strong>Giả định căn chỉnh ảnh và máy ảnh</strong>
              <small>Nhập thông số nội tại của máy ảnh theo điểm ảnh, vị trí theo mét và quaternion từ máy ảnh sang hệ tọa độ thế giới. Nhập tọa độ hai đầu mút thực cho các giao tuyến. Sai số RMS chỉ kiểm tra độ nhất quán khi chiếu ngược, không xác nhận độ chính xác ngoài đời.</small>
              <div className="alignment-camera-fields">
                {(['fx', 'fy', 'cx', 'cy'] as const).map((field) => <label key={field}>{field} (px)
                  <input aria-label={`Camera ${field}`} type="number" step="any" value={alignmentCamera[field]} onChange={(event) => setAlignmentCameraValue(field, event.currentTarget.value)} />
                </label>)}
                {alignmentCamera.position.map((value, index) => <label key={`position-${index}`}>Vị trí máy ảnh {['X', 'Y', 'Z'][index]} (m)
                  <input aria-label={`Vị trí máy ảnh ${index + 1} meters`} type="number" step="any" value={value} onChange={(event) => setAlignmentCameraVector('position', index, event.currentTarget.value)} />
                </label>)}
                {alignmentCamera.rotation.map((value, index) => <label key={`rotation-${index}`}>Quaternion máy ảnh {['X', 'Y', 'Z', 'W'][index]}
                  <input aria-label={`Quaternion máy ảnh ${index + 1}`} type="number" step="any" value={value} onChange={(event) => setAlignmentCameraVector('rotation', index, event.currentTarget.value)} />
                </label>)}
              </div>
              <div className="alignment-world-edges">
                {topology.edges.map((edge, edgeIndex) => <fieldset key={edge.id}><legend>Edge {edgeIndex + 1} tọa độ hai đầu mút (m)</legend>
                  {(['Start', 'End'] as const).flatMap((endpoint, endpointIndex) => ['X', 'Y', 'Z'].map((axis, axisIndex) => {
                    const coordinateIndex = endpointIndex * 3 + axisIndex
                    return <label key={`${endpoint}-${axis}`}>{endpoint} {axis}
                      <input aria-label={`Edge ${edgeIndex + 1} ${endpoint} ${axis} meters`} type="number" step="any" value={alignmentWorldEdges[edge.id]?.[coordinateIndex] ?? ''} onChange={(event) => setAlignmentEdgeCoordinate(edge.id, coordinateIndex, event.currentTarget.value)} />
                    </label>
                  }))}
                </fieldset>)}
              </div>
              <button type="button" className="quiet-button" disabled={busy} onClick={() => void verifyPhotoAlignment()}>Kiểm tra căn chỉnh ảnh</button>
              {alignmentResult && <div role="status" className="alignment-report">
                <strong>Sai số RMS khi chiếu ngược: {Number.isFinite(alignmentResult.alignment.rmsErrorPixels) ? `${alignmentResult.alignment.rmsErrorPixels.toFixed(2)} px` : 'chưa khả dụng'}</strong>
                <span>{alignmentResult.alignment.visibleSampleCount} điểm mẫu nhìn thấy Â· {alignmentResult.alignment.unprojectableSampleCount} điểm mẫu không thể chiếu</span>
                {alignmentResult.alignment.uncertaintyNotes.map((note) => <small key={note}>{note}</small>)}
                {alignmentResult.diagnostics.map((diagnostic) => <small key={diagnostic}>{diagnostic}</small>)}
              </div>}
            </section>
            <div className="topology-edge-list">{topology.edges.map((edge, index) => <label key={edge.id}>Giao tuyến {index + 1} · vùng {edge.regionId.slice(0, 8)} Â· ({Math.round(edge.start.x)}, {Math.round(edge.start.y)}) â†’ ({Math.round(edge.end.x)}, {Math.round(edge.end.y)})
              <select aria-label={`Topology edge ${index + 1} role`} value={edge.role} disabled={busy} onChange={(event) => updateTopologyEdge(edge.id, event.currentTarget.value)}>
                {topologyRoles.map((role) => <option key={role} value={role}>{topologyRoleLabels[role]}</option>)}
              </select>
            </label>)}</div>
            <button className="save-button" onClick={() => void saveTopology()} disabled={busy || !topology.edges.some((edge) => edge.provenance === 'user_supplied')}>Lưu chỉnh sửa giao tuyến</button>
            <div className="geometry-preview-controls">
              <label>Đường bao sàn<select aria-label="Vùng sàn cho hình học" value={geometryFloorId || annotation.regions.find((region) => region.semanticRole === 'floor')?.id || ''} onChange={(event) => setGeometryFloorId(event.currentTarget.value)}>
                {annotation.regions.filter((region) => region.semanticRole === 'floor').map((region) => <option key={region.id} value={region.id}>{region.id.slice(0, 8)}</option>)}
              </select></label>
              <label>Đoạn tham chiếu đã đo<select aria-label="Đoạn tham chiếu cho hình học" value={geometrySpanId || annotation.referenceSpans[0]?.id || ''} onChange={(event) => setGeometrySpanId(event.currentTarget.value)}>
                {annotation.referenceSpans.map((span) => <option key={span.id} value={span.id}>{span.knownMeters} m Â· {span.id.slice(0, 8)}</option>)}
              </select></label>
              <button className="quiet-button" onClick={() => void previewGeometry()} disabled={busy}>Xem trước hình học tham số</button>
            </div>
            {geometryResult && <div className="geometry-preview-result" role="region" aria-label="Kết quả xem trước hình học">
              {geometryResult.stageResults.map((stage) => <div key={stage.stage}><strong>{stageLabels[stage.stage] ?? stage.stage.replaceAll('_', ' ')} · {{ succeeded: 'Hoàn tất', insufficient_data: 'Thiếu dữ liệu', unsupported: 'Chưa hỗ trợ', failed: 'Thất bại', ready: 'Sẵn sàng' }[stage.status] ?? stage.status}</strong>{stage.diagnostics.map((diagnostic) => <small key={diagnostic}>{diagnostic}</small>)}</div>)}
              {geometryResult.geometrySpec?.instances.map((instance) => <div key={instance.stableId}><strong>{instance.name}</strong><small>{instance.dimensions.map((dimension) => `${dimension.meters ?? 'chưa biết'} m (${dimension.status === 'unknown' ? 'chưa biết' : dimension.status === 'estimated' ? 'ước tính' : dimension.status})`).join(' × ')}</small></div>)}
              {geometryResult.geometrySpec?.instances.flatMap((instance) => instance.dimensions.map((dimension, index) => dimension.status === 'unknown'
                ? <label key={`${instance.stableId}:${index}`}>{instance.name} kích thước {index + 1} (mét)
                  <input aria-label={`${instance.name} kích thước ${index + 1} mét`} type="number" min="0.01" max="1000" step="0.01" value={geometryOverrides[`${instance.stableId}:${index}`] ?? ''} onChange={(event) => { const value = event.currentTarget.value; setGeometryOverrides((current) => ({ ...current, [`${instance.stableId}:${index}`]: value })) }} />
                </label> : null))}
              {geometryResult.geometrySpec && <button className="quiet-button" onClick={() => void createGeometryCandidate()} disabled={busy}>Lưu phương án cảnh mới</button>}
            </div>}
            {geometryCandidate && <div className="candidate-apply-controls"><strong>Phương án {geometryCandidate.candidateId} · dựa trên phiên bản cảnh {geometryCandidate.baseSceneRevision}</strong>
              {geometryPreviewScene && <div className="candidate-view" aria-label="3D geometry candidate preview"><Suspense fallback={<div className="canvas-loading">Đang chuẩn bị bản xem trước…</div>}>
                <SceneViewer scene={geometryPreviewScene} selectedSurfaceId={null} selectedObjectId={null} transformMode="translate" onSelect={() => undefined} onSelectObject={() => undefined} onTransform={() => undefined} />
              </Suspense></div>}
              <label><input type="checkbox" checked={confirmGeometryApply} disabled={busy} onChange={(event) => setConfirmGeometryApply(event.currentTarget.checked)} /> Áp dụng hình học này vào cảnh sau khi kiểm tra phiên bản và kích thước.</label>
              <button className="save-button" disabled={busy || !confirmGeometryApply} onClick={() => void applyGeometryCandidate()}>Áp dụng hình học đã xác nhận</button>
            </div>}
          </>}
        </section>
      </section>}

      <section className="reconstruction-card job-card" aria-label="Trạng thái tác vụ dựng cảnh">
        <span className="field-label">2 Â· TÁC VỤ DỰNG CẢNH ĐƯỢC LƯU</span>
        {!job && <p>Tải ảnh lên để tạo tác vụ được lưu lại.</p>}
        {job && <>
          <div className={`job-status status-${job.status}`}><strong>{jobStatusLabels[job.status] ?? job.status}</strong><span>{stageLabels[job.stage] ?? job.stage.replaceAll('_', ' ')}</span></div>
          <progress aria-label="Tiến độ dựng cảnh" max={100} value={job.progress} />
          <div className="job-meta"><span>{job.progress}%</span><span>Lần thử {job.attemptCount}/{job.maxAttempts}</span><span>{job.id}</span></div>
          {isActive && <button className="quiet-button" onClick={() => void cancelJob()}>Hủy tác vụ</button>}
          {job.status === 'failed' && job.attemptCount < job.maxAttempts && <button className="quiet-button" onClick={() => void retryJob()}>Chạy lại tác vụ</button>}
          {job.errorMessage && <p className="reconstruction-error" role="alert">{job.errorCode}: {job.errorMessage}</p>}
          {job.status === 'succeeded' && analysis && <p className="mock-provenance">Nguồn phân tích cảnh: {analysis.providerId}; {analysis.assumptions.notes.find((note) => note.startsWith('Segmentation provenance:'))?.replace('Segmentation provenance:', 'Nguồn phân vùng:') ?? 'chưa có thông tin nguồn phân vùng'}</p>}
        </>}
        <div className="capability-list" aria-label="Các chức năng dựng cảnh hiện có">
          {capabilities.map((capability) => <div key={capability.name}><span className={capability.available ? 'capability-dot available' : 'capability-dot'} />
            <strong>{capability.name === 'scene-reconstruction' ? 'Dựng cảnh' : capability.name === 'object-segmentation' ? 'Phân vùng đồ vật' : capability.name === 'image-to-3d' ? 'Ảnh thành mô hình 3D' : capability.name.replaceAll('-', ' ')}</strong><small>{capability.available ? capability.providerId : 'chưa khả dụng'}</small></div>)}
          {capabilities.some((capability) => capability.name === 'object-segmentation' && capability.providerId === 'fal-ai/sam-3/image') &&
            <p className="provider-disclosure">Cấu hình này gửi ảnh căn phòng đến fal và đơn vị cung cấp mô hình. Chỉ dùng ảnh bạn có quyền chia sẻ; kiểm tra điều khoản dữ liệu và thời hạn lưu trữ trước khi tải ảnh nhạy cảm lên.</p>}
        </div>
      </section>

      {analysis && <section className="reconstruction-card review-card">
        <span className="field-label">3 · DUYỆT KẾT QUẢ PHÂN TÍCH Â· {analysis.providerId} / {analysis.providerVersion}</span>
        {image && <div className="analysis-image"><img src={image.url} alt="Ảnh căn phòng với các vùng nhận diện để duyệt" /><svg viewBox={`0 0 ${analysis.imageWidth} ${analysis.imageHeight}`} role="img" aria-label="Các vùng nhận diện trong phòng">
          {analysis.surfaces.map((surface) => <polygon key={surface.id} points={surface.polygon.points.map((point) => `${point.x},${point.y}`).join(' ')} className="surface-polygon" />)}
          {analysis.objects.map((item) => <polygon key={item.id} points={item.polygon.points.map((point) => `${point.x},${point.y}`).join(' ')} className="object-polygon" />)}
        </svg></div>}
        <div className="analysis-review-list">
          {analysis.surfaces.map((surface) => <div className="analysis-row" key={surface.id}><strong>{topologyRoleLabels[surface.semanticRole] ?? surface.semanticRole.replaceAll('_', ' ')}</strong><span>{surface.confidence.toFixed(2)} độ tin cậy Â· {surface.provenance === 'mock' ? 'mô phỏng' : surface.provenance === 'provider-box' ? 'hộp từ nhà cung cấp' : surface.provenance === 'user-corrected' ? 'người dùng đã chỉnh' : surface.provenance}</span>
            <small>{surface.estimatedWidthMeters ?? 'chưa biết'} × {surface.estimatedHeightMeters ?? 'chưa biết'} m · ước tính</small>
            <fieldset className="polygon-editor"><legend>Chỉnh đa giác trên ảnh (điểm ảnh)</legend>{surface.polygon.points.map((point, index) => <div key={index}><span>Điểm {index + 1}</span>
              <label>X<input aria-label={`${surface.semanticRole} point ${index + 1} X`} type="number" min="0" max={analysis.imageWidth} step="1" value={point.x} onChange={(event) => updateSurfacePolygon(surface.id, index, 'x', event.currentTarget.value)} /></label>
              <label>Y<input aria-label={`${surface.semanticRole} point ${index + 1} Y`} type="number" min="0" max={analysis.imageHeight} step="1" value={point.y} onChange={(event) => updateSurfacePolygon(surface.id, index, 'y', event.currentTarget.value)} /></label></div>)}</fieldset>
            <label>Ghép với bề mặt hiện có (tùy chọn)<select aria-label={`Ghép bề mặt ${surface.semanticRole}`} value={surface.linkedSurfaceId ?? ''} disabled={!updatingExisting} onChange={(event) => updateSurface(surface.id, event.currentTarget.value || null)}>
              <option value="">Tạo bề mặt mới với mã ID mới</option>{scene.objects.flatMap((object) => object.surfaces.map((entry) => <option key={entry.id} value={entry.id}>{entry.semanticRole} Â· {object.name} Â· giữ mã ID</option>))}
            </select></label></div>)}
          {analysis.objects.map((item) => <article className={`detected-object${item.included ? '' : ' excluded'}`} key={item.id}>
            <label className="include-toggle"><input aria-label={`Giữ lại ${item.name}`} type="checkbox" checked={item.included} onChange={(event) => {
              const included = event.currentTarget.checked
              updateObject(item.id, (current) => ({ ...current, included }))
            }} /> Giữ lại</label>
            <label>Loại đồ vật<input aria-label={`Loại đồ vật ${item.name}`} value={item.type} onChange={(event) => {
              const type = event.currentTarget.value
              updateObject(item.id, (current) => ({ ...current, type }))
            }} /></label>
            <label>Tên hiển thị<input aria-label={`Tên đồ vật ${item.name}`} value={item.name} onChange={(event) => {
              const name = event.currentTarget.value
              updateObject(item.id, (current) => ({ ...current, name }))
            }} /></label>
            <div className="detected-dimensions"><span className="field-label">KÍCH THƯỚC (MÉT · ƯỚC TÍNH)</span><div>{(['Rộng', 'Cao', 'Sâu'] as const).map((dimension, index) => <label key={dimension}>{dimension}<input aria-label={`${dimension} ${item.name}`} type="number" min="0.01" max="100" step="0.01" value={item.dimensionsMeters[index]} onChange={(event) => {
              const value = Number(event.currentTarget.value)
              updateObject(item.id, (current) => {
              const dimensionsMeters = [...current.dimensionsMeters] as [number, number, number]
              dimensionsMeters[index] = value
              return { ...current, dimensionsMeters }
              })
            }} /></label>)}</div></div>
            <fieldset className="polygon-editor"><legend>Chỉnh đa giác trên ảnh (điểm ảnh)</legend>{item.polygon.points.map((point, index) => <div key={index}><span>Điểm {index + 1}</span>
              <label>X<input aria-label={`${item.name} point ${index + 1} X`} type="number" min="0" max={analysis.imageWidth} step="1" value={point.x} onChange={(event) => updateObjectPolygon(item.id, index, 'x', event.currentTarget.value)} /></label>
              <label>Y<input aria-label={`${item.name} point ${index + 1} Y`} type="number" min="0" max={analysis.imageHeight} step="1" value={point.y} onChange={(event) => updateObjectPolygon(item.id, index, 'y', event.currentTarget.value)} /></label></div>)}</fieldset>
            <label>Ghép với đồ vật hiện có (tùy chọn)<select aria-label={`Ghép đồ vật ${item.name}`} value={item.linkedObjectId ?? ''} disabled={!updatingExisting} onChange={(event) => {
              const linkedObjectId = event.currentTarget.value || null
              updateObject(item.id, (current) => ({ ...current, linkedObjectId }))
            }}>
              <option value="">Tạo đồ vật mới với mã ID mới</option>{scene.objects.map((existing) => <option key={existing.id} value={existing.id}>{existing.name} Â· giữ mã ID</option>)}
            </select></label>
            <small>{item.confidence.toFixed(2)} độ tin cậy Â· {(item.provenance === 'mock' ? 'mô phỏng' : item.provenance)} Â· mã đồ vật đề xuất ổn định {item.linkedObjectId ?? item.id}</small>
            {job?.status === 'succeeded' && image && item.included && <div className="mesh-generation-panel">
              <span className="field-label">MÔ HÌNH ĐỒ VẬT TÙY CHỌN Â· {meshCapability?.providerId ?? 'đã tắt nhà cung cấp'}</span>
              <p>Ảnh cắt theo khung đa giác đã duyệt ({getCropBounds(item.polygon.points).width} × {getCropBounds(item.polygon.points).height} điểm ảnh). Khi bật, ảnh cắt được gửi đến fal Hunyuan3D. Giá niêm yết là 0,225 USD mỗi lần tạo; máy chủ giữ trước 0,50 USD trong hạn mức chung 20 USD. Hãy xem điều khoản truyền dữ liệu trước khi bật.</p>
              {!meshCapability?.available && <small>{meshCapability?.description ?? 'Cần bật nhà cung cấp trên máy chủ để tạo tác vụ dựng mô hình.'}</small>}
              <button className="quiet-button" disabled={!meshCapability?.available || !scene.objects.some((object) => object.id === (item.linkedObjectId ?? item.id)) || meshGenerationBusyId === (item.linkedObjectId ?? item.id)} onClick={() => void createObjectMesh(item)}>
                {meshGenerationBusyId === (item.linkedObjectId ?? item.id) ? 'Đang đưa vào hàng đợi…' : 'Tạo mô hình từ ảnh cắt đã duyệt'}
              </button>
              {objectMeshJobs[item.linkedObjectId ?? item.id] && <div className="mesh-job-status">
                <small>Tác vụ mô hình {jobStatusLabels[objectMeshJobs[item.linkedObjectId ?? item.id].status] ?? objectMeshJobs[item.linkedObjectId ?? item.id].status} · trạng thái gửi {objectMeshJobs[item.linkedObjectId ?? item.id].submissionState} · {objectMeshJobs[item.linkedObjectId ?? item.id].progress}%</small>
                {objectMeshJobs[item.linkedObjectId ?? item.id].errorMessage && <small role="alert">{objectMeshJobs[item.linkedObjectId ?? item.id].errorMessage}</small>}
                {objectMeshJobs[item.linkedObjectId ?? item.id].status === 'completed' && <>
                  <label className="include-toggle"><input type="checkbox" checked={meshApplyConfirmed[item.linkedObjectId ?? item.id] ?? false} onChange={(event) => setMeshApplyConfirmed((current) => ({ ...current, [item.linkedObjectId ?? item.id]: event.currentTarget.checked }))} /> Gắn mô hình vào đồ vật có mã {item.linkedObjectId ?? item.id} trong phiên bản cảnh {scene.revision}</label>
                  <button className="save-button" disabled={!meshApplyConfirmed[item.linkedObjectId ?? item.id]} onClick={() => void applyObjectMesh(item.linkedObjectId ?? item.id, objectMeshJobs[item.linkedObjectId ?? item.id])}>Xác nhận và gắn mô hình</button>
                </>}
              </div>}
              {scene.objects.find((object) => object.id === (item.linkedObjectId ?? item.id))?.meshAsset && <small>Mô hình đã được gắn; vẫn có thể chỉnh vị trí và trạng thái hiển thị của đồ vật.</small>}
            </div>}
          </article>)}
        </div>
        <div className="assumption-review">
          <span className="field-label">GIẢ ĐỊNH MÁY ẢNH VÀ TỶ LỆ</span>
          <label>Mô hình máy ảnh<input aria-label="Giả định máy ảnh" value={analysis.assumptions.cameraModel} onChange={(event) => updateAssumptions({ cameraModel: event.currentTarget.value })} /></label>
          <p>Nhập chiều dài đã biết và độ dài tương ứng trên ảnh để ghi nhận mốc tỷ lệ. Phối cảnh và phần hình học bị khuất vẫn chưa chắc chắn; hãy kiểm tra riêng kích thước từng đồ vật.</p>
          <div className="assumption-fields"><label>Chiều dài mốc (điểm ảnh)<input aria-label="Chiều dài mốc (điểm ảnh)" type="number" min="0.01" step="1" value={analysis.assumptions.referencePixelLength ?? ''} onChange={(event) => updateCalibration(event.currentTarget.value ? Number(event.currentTarget.value) : null, analysis.assumptions.referenceLengthMeters)} /></label>
            <label>Chiều dài đã biết (mét)<input aria-label="Chiều dài mốc đã biết" type="number" min="0.01" step="0.01" value={analysis.assumptions.referenceLengthMeters ?? ''} onChange={(event) => updateCalibration(analysis.assumptions.referencePixelLength, event.currentTarget.value ? Number(event.currentTarget.value) : null)} /></label>
            <label>Mét trên mỗi điểm ảnh<input aria-label="Mét trên mỗi điểm ảnh" type="number" value={analysis.assumptions.metersPerPixel ?? ''} readOnly /></label></div>
          <label>Trạng thái tỷ lệ<select aria-label="Giả định tỷ lệ" value={analysis.assumptions.scaleStatus} onChange={(event) => updateAssumptions({ scaleStatus: event.currentTarget.value })}><option value="unknown">Chưa biết</option><option value="estimated">Ước tính</option><option value="user_supplied">Do người dùng nhập</option><option value="calibrated">Đã cân chỉnh</option></select></label>
          <label>Ghi chú duyệt<textarea aria-label="Ghi chú giả định" value={analysis.assumptions.notes.join('\n')} onChange={(event) => updateAssumptions({ notes: event.currentTarget.value.split('\n').filter(Boolean) })} /></label>
        </div>
        <label className="target-toggle"><input type="checkbox" checked={updatingExisting} onChange={(event) => {
          const checked = event.currentTarget.checked
          setUpdatingExisting(checked); setPreview(null); setConfirmed(false)
          if (!checked) setAnalysis((current) => current ? {
            ...current,
            objects: current.objects.map((item) => ({ ...item, linkedObjectId: null })),
            surfaces: current.surfaces.map((item) => ({ ...item, linkedSurfaceId: null })),
          } : current)
        }} /> Cập nhật cảnh hiện tại (phiên bản {scene.revision}); chỉ các đồ vật được ghép rõ ràng mới bị thay đổi.</label>
        <button className="save-button" onClick={() => void createPreview()} disabled={busy}>{busy ? 'Đang dựng bản xem trước…' : 'Xem trước cảnh đề xuất'}</button>
      </section>}

      {preview && <section className="reconstruction-card preview-card" aria-label="Xem trước cảnh dựng">
        <span className="field-label">4 · XEM TRƯỚC · CẢNH CHƯA ĐƯỢC LƯU</span>
        <p>{preview.createsNewScene ? 'Một cảnh mới sẽ được tạo.' : 'Cảnh hiện tại chỉ được cập nhật sau khi xác nhận.'} Phiên bản gốc {preview.baseRevision}.</p>
        <div className="candidate-view"><Suspense fallback={<p>Đang chuẩn bị bản xem trước 3D…</p>}><SceneViewer scene={JSON.parse(preview.document) as SceneDocument} selectedSurfaceId={null} selectedObjectId={null} transformMode="translate" onSelect={() => undefined} onSelectObject={() => undefined} onTransform={() => undefined} /></Suspense></div>
        <label className="confirm-toggle"><input type="checkbox" checked={confirmed} onChange={(event) => setConfirmed(event.currentTarget.checked)} /> Tôi đã xem và xác nhận lưu phương án này thành một phiên bản dữ liệu cảnh JSON.</label>
        <button className="save-button" onClick={() => void applyPreview()} disabled={!confirmed || busy}>{busy ? 'Đang áp dụng…' : preview.createsNewScene ? 'Xác nhận và tạo cảnh mới' : 'Xác nhận và cập nhật cảnh'}</button>
      </section>}
      {error && <p className="reconstruction-error" role="alert">{error}</p>}
    </div>
  </main>
}
