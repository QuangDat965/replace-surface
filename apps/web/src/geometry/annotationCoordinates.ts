export type ImagePoint = { x: number; y: number }
export type ImagePolygon = { points: ImagePoint[] }
export type DisplayRect = { scale: number; offsetX: number; offsetY: number; width: number; height: number }

export function fitImageToStage(stageWidth: number, stageHeight: number, imageWidth: number, imageHeight: number): DisplayRect {
  if (![stageWidth, stageHeight, imageWidth, imageHeight].every(Number.isFinite) ||
      stageWidth <= 0 || stageHeight <= 0 || imageWidth <= 0 || imageHeight <= 0) {
    return { scale: 0, offsetX: 0, offsetY: 0, width: 0, height: 0 }
  }
  const scale = Math.min(stageWidth / imageWidth, stageHeight / imageHeight)
  const width = imageWidth * scale
  const height = imageHeight * scale
  return { scale, offsetX: (stageWidth - width) / 2, offsetY: (stageHeight - height) / 2, width, height }
}

export function stageToImage(point: ImagePoint, rect: DisplayRect, imageWidth: number, imageHeight: number): ImagePoint {
  if (rect.scale <= 0) return { x: 0, y: 0 }
  return {
    x: clamp((point.x - rect.offsetX) / rect.scale, 0, imageWidth),
    y: clamp((point.y - rect.offsetY) / rect.scale, 0, imageHeight),
  }
}

export function imageToStage(point: ImagePoint, rect: DisplayRect): ImagePoint {
  return { x: rect.offsetX + point.x * rect.scale, y: rect.offsetY + point.y * rect.scale }
}

export function snapImagePoint(point: ImagePoint, gridSize = 1): ImagePoint {
  if (!Number.isFinite(gridSize) || gridSize <= 0) return point
  return { x: Math.round(point.x / gridSize) * gridSize, y: Math.round(point.y / gridSize) * gridSize }
}

export function validateImagePolygon(points: readonly ImagePoint[], imageWidth: number, imageHeight: number): string[] {
  const errors: string[] = []
  if (points.length < 3) return ['A polygon needs at least three points.']
  if (points.length > 1000) errors.push('A polygon cannot exceed 1000 points.')
  if (points.some((point) => !Number.isFinite(point.x) || !Number.isFinite(point.y) || point.x < 0 || point.y < 0 || point.x > imageWidth || point.y > imageHeight)) {
    errors.push('Polygon points must lie within the source image.')
  }
  if (points.some((point, index) => distanceSquared(point, points[(index + 1) % points.length]!) < 1)) {
    errors.push('Adjacent polygon points must be at least one pixel apart.')
  }
  if (Math.abs(signedDoubleArea(points)) < 1) errors.push('Polygon area must be at least half a square pixel.')
  if (hasSelfIntersection(points)) errors.push('Polygon edges cannot cross or touch.')
  return errors
}

export function validateReferenceSpan(start: ImagePoint, end: ImagePoint, meters: number, imageWidth: number, imageHeight: number): string[] {
  const errors: string[] = []
  if ([start, end].some((point) => !Number.isFinite(point.x) || !Number.isFinite(point.y) || point.x < 0 || point.y < 0 || point.x > imageWidth || point.y > imageHeight)) {
    errors.push('Reference endpoints must lie within the source image.')
  }
  if (distanceSquared(start, end) < 1) errors.push('Reference endpoints must be at least one pixel apart.')
  if (!Number.isFinite(meters) || meters <= 0) errors.push('Known length must be a positive meter value.')
  return errors
}

function signedDoubleArea(points: readonly ImagePoint[]) {
  return points.reduce((area, point, index) => {
    const next = points[(index + 1) % points.length]!
    return area + point.x * next.y - next.x * point.y
  }, 0)
}

function hasSelfIntersection(points: readonly ImagePoint[]) {
  for (let first = 0; first < points.length; first++) {
    const firstNext = (first + 1) % points.length
    for (let second = first + 1; second < points.length; second++) {
      const secondNext = (second + 1) % points.length
      if (firstNext === second || secondNext === first) continue
      if (segmentsIntersect(points[first]!, points[firstNext]!, points[second]!, points[secondNext]!)) return true
    }
  }
  return false
}

function segmentsIntersect(a: ImagePoint, b: ImagePoint, c: ImagePoint, d: ImagePoint) {
  const abC = orientation(a, b, c)
  const abD = orientation(a, b, d)
  const cdA = orientation(c, d, a)
  const cdB = orientation(c, d, b)
  if (((abC > 0 && abD < 0) || (abC < 0 && abD > 0)) && ((cdA > 0 && cdB < 0) || (cdA < 0 && cdB > 0))) return true
  return (Math.abs(abC) < 1e-9 && onSegment(a, b, c)) || (Math.abs(abD) < 1e-9 && onSegment(a, b, d)) ||
    (Math.abs(cdA) < 1e-9 && onSegment(c, d, a)) || (Math.abs(cdB) < 1e-9 && onSegment(c, d, b))
}

function orientation(a: ImagePoint, b: ImagePoint, c: ImagePoint) {
  return (b.x - a.x) * (c.y - a.y) - (b.y - a.y) * (c.x - a.x)
}

function onSegment(a: ImagePoint, b: ImagePoint, point: ImagePoint) {
  return point.x >= Math.min(a.x, b.x) && point.x <= Math.max(a.x, b.x) &&
    point.y >= Math.min(a.y, b.y) && point.y <= Math.max(a.y, b.y)
}

function distanceSquared(a: ImagePoint, b: ImagePoint) {
  return (a.x - b.x) ** 2 + (a.y - b.y) ** 2
}

function clamp(value: number, min: number, max: number) {
  return Math.max(min, Math.min(max, value))
}
