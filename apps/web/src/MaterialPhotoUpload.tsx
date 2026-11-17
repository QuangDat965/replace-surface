import { useEffect, useState } from 'react'

export function MaterialPhotoUpload({ surfaceName, disabled, onApply }: { surfaceName: string | null; disabled: boolean; onApply: (file: Blob) => Promise<void> }) {
  const [url, setUrl] = useState<string | null>(null)
  const [crop, setCrop] = useState({ x: 0, y: 0, width: 100, height: 100 })
  const [error, setError] = useState('')
  const [applying, setApplying] = useState(false)
  useEffect(() => () => { if (url) URL.revokeObjectURL(url) }, [url])
  async function apply() {
    if (!url) return
    setApplying(true); setError('')
    try {
      const image = new Image(); image.src = url; await image.decode()
      const canvas = document.createElement('canvas')
      const width = image.naturalWidth * crop.width / 100, height = image.naturalHeight * crop.height / 100
      const scale = Math.min(1, 4096 / Math.max(width, height))
      canvas.width = Math.max(1, Math.round(width * scale)); canvas.height = Math.max(1, Math.round(height * scale))
      const context = canvas.getContext('2d'); if (!context) throw Error('Không xử lý được ảnh')
      context.drawImage(image, image.naturalWidth * crop.x / 100, image.naturalHeight * crop.y / 100, width, height, 0, 0, canvas.width, canvas.height)
      const blob = await new Promise<Blob>((resolve, reject) => canvas.toBlob(value => value ? resolve(value) : reject(Error('Không cắt được ảnh')), 'image/png'))
      if (blob.size > 10 * 1024 * 1024) throw Error('Ảnh sau khi cắt quá 10 MB. Hãy chọn vùng nhỏ hơn.')
      await onApply(blob); setUrl(null)
    } catch (cause) { setError(cause instanceof Error ? cause.message : 'Không áp dụng được ảnh') }
    finally { setApplying(false) }
  }
  return <section className="photo-replacement">
    <span className="field-label">THAY BỀ MẶT BẰNG ẢNH</span>
    <p>{surfaceName ? `Đang chọn: ${surfaceName}` : 'Chọn bề mặt trong phòng hoặc danh sách bên dưới.'}</p>
    <label className="photo-picker">Chọn ảnh đá, gỗ hoặc gạch<input aria-label="Ảnh thay bề mặt" type="file" accept="image/png,image/jpeg,image/webp" disabled={!surfaceName || disabled || applying} onChange={event => {
      const file = event.target.files?.[0]; event.target.value = ''; if (!file) return
      if (!['image/png', 'image/jpeg', 'image/webp'].includes(file.type) || file.size > 10 * 1024 * 1024) { setError('Chọn PNG, JPEG hoặc WebP tối đa 10 MB.'); return }
      setError(''); setCrop({ x: 0, y: 0, width: 100, height: 100 }); setUrl(URL.createObjectURL(file))
    }} /></label>
    {url && <>
      <div className="crop-preview"><img src={url} alt="Ảnh vật liệu trước khi cắt" /><div className="crop-window" style={{ left: `${crop.x}%`, top: `${crop.y}%`, width: `${crop.width}%`, height: `${crop.height}%` }} /></div>
      <p>Khung sáng là vùng ảnh sẽ dùng. Với ảnh cả căn bếp, hãy cắt riêng phần vân vật liệu.</p>
      {(['x', 'y', 'width', 'height'] as const).map((key, index) => <label className="crop-control" key={key}>{['Vị trí ngang', 'Vị trí dọc', 'Chiều rộng', 'Chiều cao'][index]} (%)<input type="range" min={key === 'x' || key === 'y' ? 0 : 1} max={key === 'x' ? 100 - crop.width : key === 'y' ? 100 - crop.height : key === 'width' ? 100 - crop.x : 100 - crop.y} value={crop[key]} disabled={applying} onChange={event => setCrop({ ...crop, [key]: Number(event.target.value) })} /></label>)}
      <button className="assign-button" disabled={!surfaceName || disabled || applying} onClick={() => void apply()}>{applying ? 'Đang áp dụng…' : 'Áp dụng cho bề mặt đang chọn'}</button>
      <button className="text-button" disabled={applying} onClick={() => setUrl(null)}>Hủy ảnh</button>
    </>}
    {error && <p role="alert" className="error-message">{error}</p>}
    <small>Ảnh chỉ đổi bề mặt đang chọn. Các mặt còn lại được giữ nguyên.</small>
  </section>
}
