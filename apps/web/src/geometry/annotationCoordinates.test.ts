import { describe, expect, it } from 'vitest'
import { fitImageToStage, imageToStage, snapImagePoint, stageToImage, validateImagePolygon, validateReferenceSpan } from './annotationCoordinates'

describe('annotation coordinates', () => {
  it('round trips points for a letterboxed portrait image', () => {
    const rect = fitImageToStage(800, 500, 400, 800)
    expect(rect.offsetX).toBe(275)
    const original = { x: 137.5, y: 612.25 }
    expect(stageToImage(imageToStage(original, rect), rect, 400, 800)).toEqual(original)
  })

  it('snaps points to the nearest image pixel', () => {
    expect(snapImagePoint({ x: 4.6, y: 8.2 })).toEqual({ x: 5, y: 8 })
  })

  it('accepts a simple polygon and rejects crossings and out of bounds points', () => {
    expect(validateImagePolygon([{ x: 10, y: 10 }, { x: 90, y: 10 }, { x: 90, y: 90 }, { x: 10, y: 90 }], 100, 100)).toEqual([])
    expect(validateImagePolygon([{ x: 10, y: 10 }, { x: 90, y: 90 }, { x: 10, y: 90 }, { x: 90, y: 10 }], 100, 100)).toContain('Polygon edges cannot cross or touch.')
    expect(validateImagePolygon([{ x: 10, y: 10 }, { x: 110, y: 10 }, { x: 90, y: 90 }], 100, 100)).toContain('Polygon points must lie within the source image.')
  })

  it('requires an in-image, nonzero reference span and positive meters', () => {
    expect(validateReferenceSpan({ x: 2, y: 5 }, { x: 52, y: 5 }, 2.5, 100, 100)).toEqual([])
    expect(validateReferenceSpan({ x: -1, y: 5 }, { x: 1, y: 5 }, 0, 100, 100)).toHaveLength(2)
  })
})
