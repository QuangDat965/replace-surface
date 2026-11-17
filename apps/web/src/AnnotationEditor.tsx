import { useEffect, useMemo, useRef, useState } from 'react'
import { Circle, Image as CanvasImage, Layer, Line, Stage } from 'react-konva'
import type Konva from 'konva'
import useImage from 'use-image'
import { fitImageToStage, imageToStage, snapImagePoint, stageToImage, validateImagePolygon, validateReferenceSpan } from './geometry/annotationCoordinates'
import type { ImagePoint } from './geometry/annotationCoordinates'

export type AnnotationRegion = { id: string; semanticRole: string; polygon: { points: ImagePoint[] }; provenance: 'user_supplied'; confidence: null }
export type AnnotationReferenceSpan = { id: string; start: ImagePoint; end: ImagePoint; knownMeters: number; provenance: 'user_supplied' }
export type AnnotationCameraHint = { focalLengthPixels: number | null; principalPoint: ImagePoint | null }
export type AnnotationDocument = {
  contractVersion: number; annotationId: string; projectId: string; sourceImageId: string; revision: number
  imageWidth: number; imageHeight: number; regions: AnnotationRegion[]; referenceSpans: AnnotationReferenceSpan[]
  cameraHint: AnnotationCameraHint; notes: string[]
}
export type UpdateAnnotationDocument = Pick<AnnotationDocument, 'regions' | 'referenceSpans' | 'cameraHint' | 'notes'>

type Props = {
  image: { url: string; width: number; height: number }
  annotation: AnnotationDocument
  onSave: (update: UpdateAnnotationDocument) => Promise<void>
}

const roles = ['floor', 'wall', 'object', 'ceiling', 'opening', 'other']
const roleLabels: Record<string, string> = { floor: 'Sàn', wall: 'Tường', object: 'Đồ vật', ceiling: 'Trần', opening: 'Ô cửa', other: 'Khác' }

export function AnnotationEditor({ image, annotation, onSave }: Props) {
  const containerRef = useRef<HTMLDivElement>(null)
  const [containerWidth, setContainerWidth] = useState(0)
  const [draft, setDraft] = useState<UpdateAnnotationDocument>(() => ({
    regions: annotation.regions, referenceSpans: annotation.referenceSpans,
    cameraHint: annotation.cameraHint, notes: annotation.notes,
  }))
  const [role, setRole] = useState('floor')
  const [tool, setTool] = useState<'select' | 'polygon' | 'reference'>('select')
  const [points, setPoints] = useState<ImagePoint[]>([])
  const [selectedRegionId, setSelectedRegionId] = useState<string | null>(null)
  const [selectedSpanId, setSelectedSpanId] = useState<string | null>(null)
  const [knownMeters, setKnownMeters] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [photo] = useImage(image.url, 'anonymous')

  useEffect(() => {
    const element = containerRef.current
    if (!element) return
    const observer = new ResizeObserver(([entry]) => setContainerWidth(Math.floor(entry.contentRect.width)))
    observer.observe(element)
    return () => observer.disconnect()
  }, [])

  const stageWidth = containerWidth
  const stageHeight = useMemo(() => stageWidth > 0 ? Math.min(560, stageWidth * image.height / image.width) : 0, [stageWidth, image.height, image.width])
  const rect = useMemo(() => fitImageToStage(stageWidth, stageHeight, image.width, image.height), [stageWidth, stageHeight, image.width, image.height])
  const selectedRegion = draft.regions.find((region) => region.id === selectedRegionId)
  const selectedSpan = draft.referenceSpans.find((span) => span.id === selectedSpanId)
  const polygonErrors = validateImagePolygon(points, image.width, image.height)
  const draftValidationErrors = [
    ...draft.regions.flatMap((region) => validateImagePolygon(region.polygon.points, image.width, image.height)),
    ...draft.referenceSpans.flatMap((span) => validateReferenceSpan(span.start, span.end, span.knownMeters, image.width, image.height)),
  ]
  const pendingSpanMeters = Number(knownMeters)

  function handleStageClick(event: Konva.KonvaEventObject<MouseEvent>) {
    if (tool === 'select' || !event.target.getStage()) return
    const stage = event.target.getStage()!
    const pointer = stage.getPointerPosition()
    if (!pointer) return
    if (pointer.x < rect.offsetX || pointer.x > rect.offsetX + rect.width || pointer.y < rect.offsetY || pointer.y > rect.offsetY + rect.height) {
      setError('Hãy nhấp bên trong ảnh nguồn.')
      return
    }
    const point = snapImagePoint(stageToImage(pointer, rect, image.width, image.height))
    if (tool === 'polygon') {
      setPoints((current) => [...current, point])
      setError(null)
      return
    }
    if (points.length === 0) {
      setPoints([point])
      setError(null)
      return
    }
    const spanError = validateReferenceSpan(points[0]!, point, pendingSpanMeters, image.width, image.height)
    if (spanError.length) { setError(spanError.join(' ')); return }
    setDraft((current) => ({ ...current, referenceSpans: [...current.referenceSpans, {
      id: crypto.randomUUID(), start: points[0]!, end: point, knownMeters: pendingSpanMeters, provenance: 'user_supplied',
    }] }))
    setPoints([]); setTool('select'); setSelectedSpanId(null)
  }

  function finishPolygon() {
    const errors = validateImagePolygon(points, image.width, image.height)
    if (errors.length) { setError(errors.join(' ')); return }
    const region: AnnotationRegion = {
      id: crypto.randomUUID(), semanticRole: role,
      polygon: { points: [...points] }, provenance: 'user_supplied', confidence: null,
    }
    setDraft((current) => ({ ...current, regions: [...current.regions, region] }))
    setSelectedRegionId(region.id); setSelectedSpanId(null); setPoints([]); setTool('select'); setError(null)
  }

  function updateRegionPoint(regionId: string, index: number, stagePoint: ImagePoint) {
    const point = snapImagePoint(stageToImage(stagePoint, rect, image.width, image.height))
    setDraft((current) => ({ ...current, regions: current.regions.map((region) => region.id !== regionId ? region : {
      ...region, polygon: { points: region.polygon.points.map((existing, pointIndex) => pointIndex === index ? point : existing) },
    }) }))
  }

  function updateSpanPoint(spanId: string, endpoint: 'start' | 'end', stagePoint: ImagePoint) {
    const point = snapImagePoint(stageToImage(stagePoint, rect, image.width, image.height))
    setDraft((current) => ({ ...current, referenceSpans: current.referenceSpans.map((span) => span.id !== spanId ? span : { ...span, [endpoint]: point }) }))
  }

  async function save() {
    setBusy(true); setError(null)
    try { await onSave(draft) }
    catch (cause) { setError(cause instanceof Error ? cause.message : 'Không lưu được chú thích') }
    finally { setBusy(false) }
  }

  const pendingPoints = points.map((point) => imageToStage(point, rect)).flatMap((point) => [point.x, point.y])
  const draftSpan = tool === 'reference' && points.length === 1 ? imageToStage(points[0]!, rect) : null

  return <section className="annotation-editor" aria-label="Chú thích ảnh thủ công">
    <div className="annotation-toolbar">
      <label>{selectedRegion ? 'Loại vùng đang chọn' : 'Loại vùng mới'}<select aria-label="Loại vùng chú thích" value={role} onChange={(event) => {
        const nextRole = event.currentTarget.value
        setRole(nextRole)
        if (selectedRegionId) setDraft((current) => ({ ...current, regions: current.regions.map((region) => region.id === selectedRegionId ? { ...region, semanticRole: nextRole } : region) }))
      }}>{roles.map((item) => <option key={item} value={item}>{roleLabels[item]}</option>)}</select></label>
      <button type="button" className={tool === 'select' ? 'selected' : ''} onClick={() => { setTool('select'); setPoints([]) }}>Chọn / chỉnh sửa</button>
      <button type="button" onClick={() => { setSelectedRegionId(null); setSelectedSpanId(null); setRole('floor'); setTool('select'); setPoints([]) }}>Vùng mới</button>
      <button type="button" className={tool === 'polygon' ? 'selected' : ''} onClick={() => { setTool('polygon'); setPoints([]); setSelectedSpanId(null) }}>Vẽ đa giác</button>
      <label>Chiều dài đã biết (m)<input aria-label="Chiều dài mốc đã biết (mét)" type="number" min="0.01" step="0.01" value={knownMeters} onChange={(event) => setKnownMeters(event.currentTarget.value)} /></label>
      <button type="button" className={tool === 'reference' ? 'selected' : ''} onClick={() => { setTool('reference'); setPoints([]); setSelectedRegionId(null) }}>Vẽ đoạn tham chiếu</button>
      {tool === 'polygon' && <><button type="button" onClick={() => setPoints((current) => current.slice(0, -1))} disabled={!points.length}>Hoàn tác điểm</button><button type="button" onClick={finishPolygon} disabled={polygonErrors.length > 0}>Hoàn tất đa giác</button></>}
      {tool === 'reference' && points.length > 0 && <button type="button" onClick={() => setPoints([])}>Xóa đường</button>}
      <button type="button" className="annotation-save" onClick={() => void save()} disabled={busy || draftValidationErrors.length > 0}>{busy ? 'Đang lưu…' : `Lưu phiên bản ${annotation.revision + 1}`}</button>
    </div>
    {tool === 'polygon' && <p className="annotation-help">Nhấp vào ảnh để thêm các đỉnh. Hoàn tất khi đường bao khép kín và không tự cắt nhau.</p>}
    {tool === 'reference' && <p className="annotation-help">Nhấp vào hai đầu mút của đoạn đã đo trên ảnh.</p>}
    <div className="annotation-canvas" ref={containerRef}>
      {stageWidth > 0 && stageHeight > 0 && <Stage width={stageWidth} height={stageHeight} onMouseDown={handleStageClick}>
        <Layer>
          {photo && <CanvasImage image={photo} x={rect.offsetX} y={rect.offsetY} width={rect.width} height={rect.height} listening={false} />}
          {draft.regions.map((region) => {
            const flattened = region.polygon.points.map((point) => imageToStage(point, rect)).flatMap((point) => [point.x, point.y])
            const selected = selectedRegionId === region.id
            return <Line key={region.id} points={flattened} closed fill={region.semanticRole === 'floor' ? '#61cbb344' : '#f2a45b33'}
              stroke={selected ? '#fff18a' : region.semanticRole === 'floor' ? '#67dbc1' : '#ffc17d'} strokeWidth={selected ? 3 : 2}
              listening={tool === 'select'} onClick={(event) => { event.cancelBubble = true; setSelectedRegionId(region.id); setSelectedSpanId(null); setRole(region.semanticRole) }} />
          })}
          {draft.referenceSpans.map((span) => {
            const start = imageToStage(span.start, rect); const end = imageToStage(span.end, rect)
            const selected = selectedSpanId === span.id
            return <Line key={span.id} points={[start.x, start.y, end.x, end.y]} stroke={selected ? '#fff18a' : '#f47775'} strokeWidth={3}
              listening={tool === 'select'} onClick={(event) => { event.cancelBubble = true; setSelectedSpanId(span.id); setSelectedRegionId(null) }} />
          })}
          {tool === 'polygon' && points.length > 0 && <>
            <Line points={pendingPoints} stroke="#fff18a" strokeWidth={2} dash={[6, 4]} />
            {points.map((point, index) => { const display = imageToStage(point, rect); return <Circle key={index} x={display.x} y={display.y} radius={4} fill="#fff18a" /> })}
          </>}
          {draftSpan && <Circle x={draftSpan.x} y={draftSpan.y} radius={5} fill="#f47775" />}
          {tool === 'select' && selectedRegion?.polygon.points.map((point, index) => {
            const display = imageToStage(point, rect)
            return <Circle key={`${selectedRegion.id}-${index}`} x={display.x} y={display.y} radius={7} fill="#fff18a" stroke="#172019" strokeWidth={2} draggable
              onDragEnd={(event) => updateRegionPoint(selectedRegion.id, index, { x: event.target.x(), y: event.target.y() })} />
          })}
          {tool === 'select' && selectedSpan && (['start', 'end'] as const).map((endpoint) => {
            const display = imageToStage(selectedSpan[endpoint], rect)
            return <Circle key={`${selectedSpan.id}-${endpoint}`} x={display.x} y={display.y} radius={7} fill="#fff18a" stroke="#172019" strokeWidth={2} draggable
              onDragEnd={(event) => updateSpanPoint(selectedSpan.id, endpoint, { x: event.target.x(), y: event.target.y() })} />
          })}
        </Layer>
      </Stage>}
    </div>
    {tool === 'polygon' && <p className="annotation-help" aria-live="polite">Đa giác đang vẽ · {points.length} điểm</p>}
    <div className="annotation-list">
      <div><strong>{draft.regions.length} vùng</strong>{draft.regions.map((region) => <button type="button" key={region.id} className={selectedRegionId === region.id ? 'selected' : ''} onClick={() => { setSelectedRegionId(region.id); setSelectedSpanId(null); setRole(region.semanticRole); setTool('select') }}>{roleLabels[region.semanticRole] ?? region.semanticRole} · {region.polygon.points.length} điểm</button>)}</div>
      <div><strong>{draft.referenceSpans.length} đoạn đã đo</strong>{draft.referenceSpans.map((span) => <button type="button" key={span.id} className={selectedSpanId === span.id ? 'selected' : ''} onClick={() => { setSelectedSpanId(span.id); setSelectedRegionId(null); setTool('select') }}>{span.knownMeters} m · đoạn tham chiếu</button>)}</div>
      {(selectedRegion || selectedSpan) && <button type="button" className="annotation-delete" onClick={() => {
        if (selectedRegion) setDraft((current) => ({ ...current, regions: current.regions.filter((region) => region.id !== selectedRegion.id) }))
        if (selectedSpan) setDraft((current) => ({ ...current, referenceSpans: current.referenceSpans.filter((span) => span.id !== selectedSpan.id) }))
        setSelectedRegionId(null); setSelectedSpanId(null)
      }}>Xóa chú thích đang chọn</button>}
    </div>
    {draftValidationErrors.length > 0 && <p className="annotation-validation" role="alert">{[...new Set(draftValidationErrors)].join(' ')}</p>}
    {error && <p className="reconstruction-error" role="alert">{error}</p>}
  </section>
}
