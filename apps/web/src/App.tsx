import { lazy, Suspense, useCallback, useEffect, useMemo, useState } from 'react'
import type { ChangeEvent } from 'react'
import type { Material, SceneDocument, SceneObject, Surface } from './sceneTypes'
import { assignMaterialToSurface, findSurfaceOwner } from './sceneTypes'
import { faceSizeMeters, textureRepeat, updateMaterial, updateSurfaceUv } from './materialEditor'
import {
  archiveOptionalObject, isStructuralObject, restoreArchivedObject, rotationDegrees, rotationQuaternion,
  updateObjectTransform, updateObjectVisibility,
} from './objectEditor'
import type { ObjectTransform, TransformMode } from './objectEditor'
import { sceneSummary } from './sceneSummary'
import { ReconstructionWorkspace } from './ReconstructionWorkspace'
import './App.css'

const sampleSceneId = '11111111-1111-4111-8111-111111111111'
const semanticLabels: Record<string, string> = { floor: 'Sàn', wall: 'Tường', ceiling: 'Trần', countertop: 'Mặt bàn', cabinet: 'Tủ', furniture: 'Đồ nội thất', object: 'Đồ vật' }
const SceneViewer = lazy(() => import('./SceneViewer').then((module) => ({ default: module.SceneViewer })))
const mapFields = [
  ['baseColor', 'Ảnh màu nền'], ['roughness', 'Bản đồ độ nhám'], ['metalness', 'Bản đồ kim loại'], ['normal', 'Bản đồ pháp tuyến'],
] as const
function displaySemantic(value: string) { return semanticLabels[value] ?? value.replaceAll('_', ' ') }

function NumericField({ label, value, min, max, step, onCommit }: { label: string; value: number; min: number; max: number; step: number; onCommit: (value: number) => void }) {
  const [draft, setDraft] = useState(String(value))
  const [previousValue, setPreviousValue] = useState(value)
  if (value !== previousValue) {
    setPreviousValue(value)
    setDraft(String(value))
  }
  function commitDraft() {
    const next = Number(draft)
    if (!Number.isFinite(next) || next < min || next > max) setDraft(String(value))
    else if (next !== value) onCommit(next)
  }
  return <input
    aria-label={label}
    type="number"
    min={min}
    max={max}
    step={step}
    value={draft}
    onChange={(event) => setDraft(event.currentTarget.value)}
    onBlur={commitDraft}
    onKeyDown={(event) => {
      if (event.key !== 'Enter') return
      event.preventDefault()
      const next = Number(event.currentTarget.value)
      if (Number.isFinite(next) && next >= min && next <= max && next !== value) onCommit(next)
      else if (!Number.isFinite(next) || next < min || next > max) setDraft(String(value))
    }}
  />
}

function App() {
  const [scene, setScene] = useState<SceneDocument | null>(null)
  const [past, setPast] = useState<SceneDocument[]>([])
  const [future, setFuture] = useState<SceneDocument[]>([])
  const [selectedSurfaceId, setSelectedSurfaceId] = useState<string | null>(null)
  const [selectedObjectId, setSelectedObjectId] = useState<string | null>(null)
  const [transformMode, setTransformMode] = useState<TransformMode>('translate')
  const [deleteConfirmationId, setDeleteConfirmationId] = useState<string | null>(null)
  const [activeMaterialId, setActiveMaterialId] = useState<string | null>(null)
  const [status, setStatus] = useState('Đang tải phòng mẫu…')
  const [error, setError] = useState<string | null>(null)
  const [busy, setBusy] = useState(true)
  const [uploadingMap, setUploadingMap] = useState<string | null>(null)
  const [reconstructionOpen, setReconstructionOpen] = useState(false)

  const loadScene = useCallback(async (showSpinner = true, targetSceneId = sampleSceneId) => {
    if (showSpinner) { setBusy(true); setError(null) }
    try {
      const savedResponse = await fetch(`/api/scenes/${targetSceneId}`)
      let loaded: SceneDocument
      if (savedResponse.ok) {
        loaded = await savedResponse.json() as SceneDocument
        sceneSummary(loaded)
        setStatus(`Đã tải cảnh · phiên bản ${loaded.revision}`)
      } else if (savedResponse.status === 404 && targetSceneId === sampleSceneId) {
        const sampleResponse = await fetch('/api/sample-scene')
        if (!sampleResponse.ok) throw new Error(`Không tải được cảnh mẫu (${sampleResponse.status})`)
        const sample = await sampleResponse.json() as SceneDocument
        sceneSummary(sample)
        loaded = { ...sample, revision: 0 }
        setStatus('Phòng mẫu đã sẵn sàng · lưu để tạo phiên bản đầu tiên')
      } else {
        const body = await savedResponse.json().catch(() => null)
        throw new Error(body?.detail ?? `Không tải được cảnh (${savedResponse.status})`)
      }
      setScene(loaded)
      setPast([])
      setFuture([])
      setActiveMaterialId(loaded.materials[0]?.id ?? null)
      setSelectedSurfaceId(null)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Không tải được cảnh')
      setStatus('Cảnh không khả dụng')
    } finally { setBusy(false) }
  }, [])

  // Fetch the canonical persisted scene when the editor mounts.
  // oxlint-disable-next-line react/set-state-in-effect
  useEffect(() => { void loadScene(false) }, [loadScene])

  const selected = useMemo(() => scene && selectedSurfaceId ? findSurfaceOwner(scene, selectedSurfaceId) : null, [scene, selectedSurfaceId])
  const selectedObject = scene?.objects.find((object) => object.id === selectedObjectId) ?? null
  const activeMaterial = scene?.materials.find((material) => material.id === activeMaterialId) ?? null
  const selectedFaceSize = selected ? faceSizeMeters(selected.object, selected.surface.faceId) : null
  const selectedRepeats = selected && selected.material && selectedFaceSize
    ? textureRepeat(selectedFaceSize, selected.material.tileSizeMeters, selected.surface.uv?.repeat ?? [1, 1]) : null

  function commit(next: SceneDocument, message = 'Đã thay đổi vật liệu · hãy lưu để ghi lại') {
    if (!scene) return
    setPast((entries) => [...entries, scene].slice(-50))
    setFuture([])
    setScene(next)
    setStatus(message)
  }

  function assignMaterial(materialId: string) {
    if (!scene || !selectedSurfaceId) return
    commit(assignMaterialToSurface(scene, selectedSurfaceId, materialId), 'Đã gán vật liệu cho bề mặt · hãy lưu để ghi lại')
  }

  function editMaterial(patch: Partial<Omit<Material, 'id'>>) {
    if (scene && activeMaterialId) commit(updateMaterial(scene, activeMaterialId, patch))
  }

  function editSurfaceUv(patch: NonNullable<Surface['uv']>) {
    if (scene && selectedSurfaceId) commit(updateSurfaceUv(scene, selectedSurfaceId, patch))
  }

  function selectObject(object: SceneObject) {
    setSelectedObjectId(object.id)
    setSelectedSurfaceId(null)
    setDeleteConfirmationId(null)
  }

  function selectSurface(surface: Surface) {
    if (!scene) return
    const owner = findSurfaceOwner(scene, surface.id)
    setSelectedSurfaceId(surface.id)
    if (owner) setSelectedObjectId(owner.object.id)
    setDeleteConfirmationId(null)
    setStatus(`Đã chọn ${displaySemantic(surface.semanticRole)}`)
  }

  function editObjectTransform(transform: ObjectTransform) {
    if (scene && selectedObjectId) commit(updateObjectTransform(scene, selectedObjectId, transform), 'Đã thay đổi vị trí hoặc kích thước · hãy lưu để ghi lại')
  }

  function editObjectRotation(axis: number, value: number) {
    if (!selectedObject) return
    const degrees = rotationDegrees(selectedObject.rotation)
    degrees[axis] = value
    editObjectTransform({ ...selectedObject, rotation: rotationQuaternion(degrees) })
  }

  function toggleObjectVisibility(object: SceneObject) {
    if (scene) commit(updateObjectVisibility(scene, object.id, !object.visible), `${object.visible ? 'Đã ẩn đồ vật' : 'Đã hiện đồ vật'} · hãy lưu để ghi lại`)
  }

  function deleteSelectedObject() {
    if (!scene || !selectedObject || isStructuralObject(selectedObject)) return
    commit(archiveOptionalObject(scene, selectedObject.id), 'Đã lưu trữ đồ vật tùy chọn · dùng Khôi phục hoặc Hoàn tác để lấy lại')
    setSelectedObjectId(null)
    setSelectedSurfaceId(null)
    setDeleteConfirmationId(null)
  }

  function restoreObject(objectId: string) {
    if (!scene) return
    const restored = restoreArchivedObject(scene, objectId)
    commit(restored, 'Đã khôi phục đồ vật tùy chọn · hãy lưu để ghi lại')
    setSelectedObjectId(objectId)
    setSelectedSurfaceId(null)
  }

  function addMaterial() {
    if (!scene) return
    const material: Material = {
      id: crypto.randomUUID(), name: `Vật liệu mới ${scene.materials.length + 1}`, baseColor: '#c8a37c', roughness: 0.7,
      metalness: 0, tileSizeMeters: [0.6, 0.6], maps: {},
    }
    commit({ ...scene, materials: [...scene.materials, material] }, 'Đã thêm vật liệu mới · hãy lưu để ghi lại')
    setActiveMaterialId(material.id)
  }

  async function uploadTexture(event: ChangeEvent<HTMLInputElement>, mapKey: string) {
    const file = event.target.files?.[0]
    event.target.value = ''
    if (!file || !activeMaterial || !scene) return
    setUploadingMap(mapKey)
    setError(null)
    try {
      const response = await fetch('/api/assets/textures', { method: 'POST', headers: { 'Content-Type': file.type }, body: file })
      const result = await response.json().catch(() => null)
      if (!response.ok) throw new Error(result?.error ?? result?.detail ?? `Tải ảnh vật liệu thất bại (${response.status})`)
      editMaterial({ maps: { ...activeMaterial.maps, [mapKey]: result.url as string } })
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Tải ảnh vật liệu thất bại')
    } finally { setUploadingMap(null) }
  }

  function undo() {
    if (!scene || past.length === 0) return
    setFuture((entries) => [scene, ...entries])
    setScene(past[past.length - 1])
    setPast((entries) => entries.slice(0, -1))
    setStatus('Đã hoàn tác chỉnh sửa · hãy lưu để ghi lại')
  }

  function redo() {
    if (!scene || future.length === 0) return
    setPast((entries) => [...entries, scene])
    setScene(future[0])
    setFuture((entries) => entries.slice(1))
    setStatus('Đã làm lại chỉnh sửa · hãy lưu để ghi lại')
  }

  async function saveScene() {
    if (!scene) return
    setBusy(true)
    setError(null)
    try {
      const documentJson = JSON.stringify(scene)
      setStatus(`Đang lưu dữ liệu cảnh · ${documentJson.length} byte`)
      const response = await fetch(`/api/scenes/${scene.sceneId}`, {
        method: 'PUT',
        headers: { 'Content-Type': 'application/json', 'If-Match': `"${scene.revision}"` },
        body: documentJson,
      })
      if (response.status === 409) throw new Error('Cảnh đã được thay đổi trong phiên khác. Hãy tải phiên bản mới nhất rồi thực hiện lại chỉnh sửa.')
      if (!response.ok) {
        const body = await response.json().catch(() => null)
        throw new Error(body?.detail ?? `Lưu thất bại (${response.status})`)
      }
      const saved = await response.json() as SceneDocument
      setScene(saved)
      setPast([])
      setFuture([])
      setStatus(`Đã lưu · phiên bản ${saved.revision}`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : 'Không lưu được cảnh')
    } finally { setBusy(false) }
  }

  async function exportScene(format: 'json' | 'glb') {
    if (!scene) return
    setError(null)
    try {
      const response = await fetch(`/api/scenes/${scene.sceneId}/export.${format}`)
      if (!response.ok) {
        const detail = await response.json().catch(() => null) as { detail?: string } | null
        throw new Error(detail?.detail ?? `Xuất tệp thất bại (${response.status}). Hãy lưu cảnh trước khi xuất.`)
      }
      const download = URL.createObjectURL(await response.blob())
      const link = document.createElement('a')
      link.href = download
      link.download = `roomforge-${scene.sceneId}-r${scene.revision}.${format}`
      link.click()
      URL.revokeObjectURL(download)
      setStatus(`Đã xuất cảnh ${format.toUpperCase()} · phiên bản ${scene.revision}`)
    } catch (cause) {
      setError(cause instanceof Error ? cause.message : `Không xuất được cảnh ${format.toUpperCase()}`)
    }
  }

  if (!scene) return <main className="loading"><div className="brand-mark">RF</div><p>{status}</p>{error && <p role="alert">{error}</p>}<button onClick={() => void loadScene()} disabled={busy}>Thử lại</button></main>

  if (reconstructionOpen) return <ReconstructionWorkspace scene={scene} onClose={() => setReconstructionOpen(false)} onApplied={(applied) => {
    setScene(applied)
    setPast([]); setFuture([]); setSelectedObjectId(null); setSelectedSurfaceId(null)
    setActiveMaterialId(applied.materials[0]?.id ?? null)
    setStatus(`Đã lưu kết quả dựng cảnh · phiên bản ${applied.revision}`)
    setReconstructionOpen(false)
  }} />

  return <main className="workspace">
    <header className="topbar">
      <div className="brand"><div className="brand-mark">RF</div><div><strong>RoomForge</strong><span>Không gian thiết kế</span></div></div>
      <div className="project-title"><span className="crumb">Dự án&nbsp; / &nbsp;</span> Phòng mẫu <span className="saved-indicator" aria-label={status}>{status.startsWith('Đã lưu') ? 'Đã lưu' : 'Đang sửa'}</span></div>
      <div className="top-actions">
        <button className="quiet-button" onClick={undo} disabled={busy || past.length === 0} aria-label="Hoàn tác chỉnh sửa">Hoàn tác</button>
        <button className="quiet-button" onClick={redo} disabled={busy || future.length === 0} aria-label="Làm lại chỉnh sửa">Làm lại</button>
        <button className="quiet-button" onClick={() => setReconstructionOpen(true)} disabled={busy}>Dựng cảnh</button>
        <button className="quiet-button" onClick={() => void loadScene(true, scene.sceneId)} disabled={busy}>Tải lại</button>
        <button className="quiet-button" onClick={() => void exportScene('json')} disabled={busy} aria-label="Xuất dữ liệu cảnh JSON đã lưu">Xuất JSON</button>
        <button className="quiet-button" onClick={() => void exportScene('glb')} disabled={busy} aria-label="Xuất cảnh GLB đã lưu">Xuất GLB</button>
        <button className="save-button" onClick={() => void saveScene()} disabled={busy}>{busy ? 'Đang xử lý…' : 'Lưu cảnh'}</button>
      </div>
    </header>
    <div className="body-layout">
      <aside className="left-rail"><button className="rail-button active" aria-label="Trình dựng phòng">◈</button><button className="rail-button" aria-label="Vật liệu" title="Thư viện vật liệu và công cụ chỉnh PBR">▧</button><button className="rail-button" aria-label="Chú thích ảnh" title="Chú thích ảnh">◤</button></aside>
      <section className="viewport-panel" aria-label="Khung nhìn phòng 3D">
        <div className="viewport-heading"><div><span className="eyebrow">KHUNG NHÌN 3D</span><h1>Phòng mẫu</h1></div><span className="unit-pill">MÉT · TRỤC Y HƯỚNG LÊN</span></div>
        <div className="scene-canvas"><Suspense fallback={<div className="canvas-loading">Đang chuẩn bị phòng 3D…</div>}><SceneViewer scene={scene} selectedSurfaceId={selectedSurfaceId} selectedObjectId={selectedObjectId} transformMode={transformMode} onSelect={selectSurface} onSelectObject={selectObject} onTransform={(objectId, transform) => commit(updateObjectTransform(scene, objectId, transform), 'Đã thay đổi vị trí hoặc kích thước · hãy lưu để ghi lại')} /></Suspense><div className="canvas-hint">Kéo để xoay <i /> Cuộn để phóng to/thu nhỏ <i /> Chọn đồ vật hoặc bề mặt để chỉnh sửa</div></div>
        <div className="scene-footer"><span>Cảnh mẫu</span><span>{scene.objects.length} đồ vật · {scene.objects.reduce((count, object) => count + object.surfaces.length, 0)} bề mặt có thể chọn</span><span>Phiên bản {scene.revision}</span></div>
      </section>
      <aside className="inspector">
        <div className="inspector-head"><span className="eyebrow">CHỈNH SỬA PHÒNG</span><h2>{selected ? displaySemantic(selected.surface.semanticRole) : selectedObject?.name ?? 'Chọn đồ vật hoặc bề mặt'}</h2></div>
        {selected && <div className="selection-card"><span className="selection-dot" /><div><strong>{selected.object.name}</strong><small>{selected.surface.id}</small></div></div>}

        <section className="object-library" aria-label="Đồ vật">
          <div className="section-title"><span className="field-label">ĐỒ VẬT ({scene.objects.length})</span></div>
          {scene.objects.map((object) => <div className={`object-row${object.id === selectedObjectId ? ' selected' : ''}`} key={object.id}>
            <button className="object-select" aria-label={`Chọn đồ vật ${object.name}`} aria-pressed={object.id === selectedObjectId} onClick={() => selectObject(object)}>
              <strong>{object.name}</strong><small>{object.category}{object.locked ? ' · đã khóa' : ''}</small>
            </button>
            <button className="object-icon-button" aria-label={`${object.visible ? 'Ẩn' : 'Hiện'} ${object.name}`} title={`${object.visible ? 'Ẩn' : 'Hiện'} đồ vật`} onClick={() => toggleObjectVisibility(object)}>{object.visible ? '◉' : '○'}</button>
            <button className="object-icon-button danger" aria-label={`Xóa ${object.name}`} title={isStructuralObject(object) ? 'Không thể xóa cấu kiện kiến trúc' : 'Lưu trữ đồ vật tùy chọn'} disabled={isStructuralObject(object)} onClick={() => { selectObject(object); setDeleteConfirmationId(object.id) }}>×</button>
          </div>)}
          {(scene.deletedObjects?.length ?? 0) > 0 && <div className="deleted-objects" aria-label="Đồ vật đã lưu trữ">
            <span className="field-label">ĐỒ VẬT ĐÃ LƯU TRỮ</span>
            {scene.deletedObjects!.map((object) => <div className="object-row" key={object.id}><span><strong>{object.name}</strong><small>Đã lưu trữ · giữ nguyên mã ID</small></span><button className="text-button" aria-label={`Khôi phục ${object.name}`} onClick={() => restoreObject(object.id)}>Khôi phục</button></div>)}
          </div>}
        </section>

        {selectedObject && <section className="object-properties" aria-label="Thuộc tính đồ vật đang chọn">
          <span className="field-label">BIẾN ĐỔI ĐỒ VẬT · MÉT</span>
          <small className="object-id">{selectedObject.id}</small>
          <div className="transform-modes" role="group" aria-label="Chế độ biến đổi">
            <button aria-label="Di chuyển" aria-pressed={transformMode === 'translate'} onClick={() => setTransformMode('translate')}>Di chuyển</button>
            <button aria-label="Xoay" aria-pressed={transformMode === 'rotate'} onClick={() => setTransformMode('rotate')}>Xoay</button>
            <button aria-label="Đổi kích thước" aria-pressed={transformMode === 'scale'} onClick={() => setTransformMode('scale')}>Đổi cỡ</button>
          </div>
          <div className="two-fields">
            {(['X', 'Y', 'Z'] as const).map((axis, index) => <label key={`position-${axis}`}><span className="field-label">Vị trí {axis} (m)</span><NumericField label={`Vị trí ${axis} (m)`} min={-100} max={100} step={0.01} value={selectedObject.position[index]} onCommit={(value) => { const position = [...selectedObject.position] as [number, number, number]; position[index] = value; editObjectTransform({ ...selectedObject, position }) }} /></label>)}
          </div>
          <div className="two-fields">
            {(['X', 'Y', 'Z'] as const).map((axis, index) => <label key={`rotation-${axis}`}><span className="field-label">Góc xoay {axis} (°)</span><NumericField label={`Góc xoay ${axis} (độ)`} min={-36000} max={36000} step={1} value={rotationDegrees(selectedObject.rotation)[index]} onCommit={(value) => editObjectRotation(index, value)} /></label>)}
          </div>
          <div className="two-fields">
            {(['Rộng', 'Cao', 'Sâu'] as const).map((axis, index) => <label key={`dimension-${axis}`}><span className="field-label">{axis} (m)</span><NumericField label={`${axis} (m)`} min={0.01} max={100} step={0.01} value={selectedObject.dimensions[index]} onCommit={(value) => { const dimensions = [...selectedObject.dimensions] as [number, number, number]; dimensions[index] = value; editObjectTransform({ ...selectedObject, dimensions }) }} /></label>)}
          </div>
          <button className="visibility-button" onClick={() => toggleObjectVisibility(selectedObject)}>{selectedObject.visible ? 'Ẩn đồ vật' : 'Hiện đồ vật'}</button>
          {isStructuralObject(selectedObject) ? <p className="protected-note">Không thể xóa cấu kiện kiến trúc.</p> : <button className="delete-object-button" onClick={() => setDeleteConfirmationId(selectedObject.id)}>Xóa đồ vật tùy chọn</button>}
          {deleteConfirmationId === selectedObject.id && <div className="delete-confirmation" role="alertdialog" aria-label="Xác nhận xóa đồ vật"><strong>Lưu trữ {selectedObject.name}?</strong><span>Đồ vật và mã ID ổn định vẫn có thể khôi phục. Dùng Hoàn tác hoặc Khôi phục để lấy lại.</span><div><button onClick={() => setDeleteConfirmationId(null)}>Hủy</button><button onClick={deleteSelectedObject}>Xác nhận xóa</button></div></div>}
        </section>}

        <section className="material-library" aria-label="Thư viện vật liệu">
          <div className="section-title"><span className="field-label">THƯ VIỆN VẬT LIỆU</span><button className="text-button" onClick={addMaterial}>+ Thêm</button></div>
          <div className="material-cards">
            {scene.materials.map((material) => <button key={material.id} className={`material-card${material.id === activeMaterialId ? ' active' : ''}`} aria-label={`Chỉnh sửa vật liệu ${material.name}`} aria-pressed={material.id === activeMaterialId} onClick={() => setActiveMaterialId(material.id)}>
              <span className="material-swatch" style={{
                backgroundColor: material.baseColor,
                backgroundImage: material.maps.baseColor ? `url("${material.maps.baseColor}")` : undefined,
                backgroundSize: material.maps.baseColor ? `${Math.max(14, 22 * material.tileSizeMeters[0] / material.tileSizeMeters[1])}px 22px` : undefined,
                backgroundRepeat: material.maps.baseColor ? 'repeat' : undefined,
              }} />
              <span><strong>{material.name}</strong><small>{material.tileSizeMeters[0]} × {material.tileSizeMeters[1]} m</small></span>
            </button>)}
          </div>
          {selected && activeMaterial && <button className="assign-button" onClick={() => assignMaterial(activeMaterial.id)} disabled={selected.surface.materialId === activeMaterial.id}>Gán cho bề mặt đang chọn</button>}
        </section>

        {activeMaterial && <section className="material-properties" aria-label="Thuộc tính vật liệu">
          <label className="field-label" htmlFor="material-name">Tên vật liệu</label>
          <input id="material-name" value={activeMaterial.name} onChange={(event) => editMaterial({ name: event.target.value })} maxLength={80} />
          <label className="field-label" htmlFor="material-color">Màu nền</label>
          <div className="color-field"><input id="material-color" type="color" value={activeMaterial.baseColor} onChange={(event) => editMaterial({ baseColor: event.target.value })} /><span>{activeMaterial.baseColor.toUpperCase()}</span></div>
          <div className="two-fields">
            <label><span className="field-label">Rộng viên gạch (m)</span><NumericField label="Rộng viên gạch (m)" min={0.01} max={20} step={0.01} value={activeMaterial.tileSizeMeters[0]} onCommit={(value) => editMaterial({ tileSizeMeters: [value, activeMaterial.tileSizeMeters[1]] })} /></label>
            <label><span className="field-label">Cao viên gạch (m)</span><NumericField label="Cao viên gạch (m)" min={0.01} max={20} step={0.01} value={activeMaterial.tileSizeMeters[1]} onCommit={(value) => editMaterial({ tileSizeMeters: [activeMaterial.tileSizeMeters[0], value] })} /></label>
          </div>
          <label className="range-field"><span className="range-label"><span className="field-label">Độ nhám</span><output>{activeMaterial.roughness.toFixed(2)}</output></span><input aria-label="Độ nhám" type="range" min="0" max="1" step="0.01" value={activeMaterial.roughness} onChange={(event) => editMaterial({ roughness: Number(event.target.value) })} /></label>
          <label className="range-field"><span className="range-label"><span className="field-label">Độ kim loại</span><output>{activeMaterial.metalness.toFixed(2)}</output></span><input aria-label="Độ kim loại" type="range" min="0" max="1" step="0.01" value={activeMaterial.metalness} onChange={(event) => editMaterial({ metalness: Number(event.target.value) })} /></label>
          <div className="texture-maps"><span className="field-label">BẢN ĐỒ VẬT LIỆU PBR</span>
            {mapFields.map(([mapKey, title]) => <label className="texture-upload" key={mapKey}>{title}<span>{activeMaterial.maps[mapKey] ? 'Đã tải lên · thay ảnh' : uploadingMap === mapKey ? 'Đang tải…' : 'Tải ảnh lên'}</span><input aria-label={title} type="file" accept="image/png,image/jpeg,image/webp" onChange={(event) => void uploadTexture(event, mapKey)} disabled={uploadingMap !== null} /></label>)}
          </div>
        </section>}

        {selected && selectedFaceSize && selectedRepeats && <section className="surface-uv" aria-label="Căn chỉnh ảnh vật liệu trên bề mặt">
          <span className="field-label">CĂN CHỈNH ẢNH VẬT LIỆU</span>
          <div className="two-fields">
            <label><span className="field-label">Góc xoay (°)</span><NumericField label="Góc xoay ảnh vật liệu" min={-36000} max={36000} step={1} value={selected.surface.uv?.rotationDegrees ?? 0} onCommit={(value) => editSurfaceUv({ rotationDegrees: value, offset: selected.surface.uv?.offset ?? [0, 0], repeat: selected.surface.uv?.repeat ?? [1, 1] })} /></label>
            <label><span className="field-label">Dịch chuyển U</span><NumericField label="Dịch chuyển vật liệu U" min={0} max={1} step={0.05} value={selected.surface.uv?.offset[0] ?? 0} onCommit={(value) => editSurfaceUv({ rotationDegrees: selected.surface.uv?.rotationDegrees ?? 0, offset: [value, selected.surface.uv?.offset[1] ?? 0], repeat: selected.surface.uv?.repeat ?? [1, 1] })} /></label>
          </div>
          <div className="two-fields">
            <label><span className="field-label">Dịch chuyển V</span><NumericField label="Dịch chuyển vật liệu V" min={0} max={1} step={0.05} value={selected.surface.uv?.offset[1] ?? 0} onCommit={(value) => editSurfaceUv({ rotationDegrees: selected.surface.uv?.rotationDegrees ?? 0, offset: [selected.surface.uv?.offset[0] ?? 0, value], repeat: selected.surface.uv?.repeat ?? [1, 1] })} /></label>
          </div>
          <div className="two-fields">
            <label><span className="field-label">Lặp theo U</span><NumericField label="Lặp ảnh vật liệu U" min={0.01} max={100} step={0.1} value={selected.surface.uv?.repeat?.[0] ?? 1} onCommit={(value) => editSurfaceUv({ rotationDegrees: selected.surface.uv?.rotationDegrees ?? 0, offset: selected.surface.uv?.offset ?? [0, 0], repeat: [value, selected.surface.uv?.repeat?.[1] ?? 1] })} /></label>
            <label><span className="field-label">Lặp theo V</span><NumericField label="Lặp ảnh vật liệu V" min={0.01} max={100} step={0.1} value={selected.surface.uv?.repeat?.[1] ?? 1} onCommit={(value) => editSurfaceUv({ rotationDegrees: selected.surface.uv?.rotationDegrees ?? 0, offset: selected.surface.uv?.offset ?? [0, 0], repeat: [selected.surface.uv?.repeat?.[0] ?? 1, value] })} /></label>
          </div>
          <output className="repeat-summary">Số viên theo kích thước thực: {selectedRepeats[0].toFixed(2)} × {selectedRepeats[1].toFixed(2)} trên bề mặt {selectedFaceSize[0].toFixed(2)} × {selectedFaceSize[1].toFixed(2)} m</output>
        </section>}

        {error && <p className="error-message" role="alert">{error}</p>}
        <div className="surface-list" aria-label="Các bề mặt trong phòng">
          <span className="field-label">BỀ MẶT TRONG PHÒNG</span>
          {scene.objects.flatMap((object) => object.surfaces.map((surface) => ({ object, surface }))).map(({ object, surface }) =>
            <button key={surface.id} className={`surface-row${surface.id === selectedSurfaceId ? ' selected' : ''}`} aria-pressed={surface.id === selectedSurfaceId} onClick={() => { setSelectedSurfaceId(surface.id); setStatus(`Đã chọn ${displaySemantic(surface.semanticRole)}`) }}>
              <span>{displaySemantic(surface.semanticRole)}</span><small>{object.name}</small>
            </button>)}
        </div>
        <div className="inspector-status" aria-live="polite"><span className={error ? 'status-dot error' : 'status-dot'} />{status}</div>
      </aside>
    </div>
  </main>
}

export default App
