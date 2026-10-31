import { describe, expect, it } from 'vitest'
import { faceSizeMeters, textureRepeat, updateMaterial, updateSurfaceUv } from './materialEditor'
import type { SceneDocument } from './sceneTypes'

const scene = {
  materials: [
    { id: 'stone', name: 'Stone', baseColor: '#fff', roughness: 0.4, metalness: 0, tileSizeMeters: [0.6, 0.6], maps: {} },
    { id: 'oak', name: 'Oak', baseColor: '#aaa', roughness: 0.8, metalness: 0, tileSizeMeters: [0.2, 1], maps: {} },
  ],
  objects: [{ id: 'counter', dimensions: [3, 0.1, 3.6], surfaces: [
    { id: 'top', faceId: 'top', materialId: 'stone' }, { id: 'edge', faceId: 'front', materialId: 'oak' },
  ] }],
} as unknown as SceneDocument

describe('physical material scale', () => {
  it('maps a 0.6 m tile to five by six repeats on a 3.0 by 3.6 m face', () => {
    const faceSize = faceSizeMeters(scene.objects[0], 'top')
    expect(faceSize).toEqual([3, 3.6])
    expect(textureRepeat(faceSize, [0.6, 0.6])).toEqual([5, 6])
  })
})

describe('material editor scene mutations', () => {
  it('changes only selected surface UV data and leaves semantic IDs intact', () => {
    const updated = updateSurfaceUv(scene, 'top', { rotationDegrees: 45, offset: [0.2, 0], repeat: [2, 1] })
    expect(updated.objects[0].surfaces[0].uv).toEqual({ rotationDegrees: 45, offset: [0.2, 0], repeat: [2, 1] })
    expect(updated.objects[0].surfaces[1]).toEqual(scene.objects[0].surfaces[1])
    expect(updated.objects[0].id).toBe(scene.objects[0].id)
    expect(updated.objects[0].surfaces.map((surface) => surface.id)).toEqual(['top', 'edge'])
  })

  it('keeps material definitions independent from per-surface assignment references', () => {
    const edited = updateMaterial(scene, 'stone', { roughness: 0.2, maps: { normal: '/api/assets/textures/0123456789abcdef0123456789abcdef' } })
    expect(edited.materials[0].roughness).toBe(0.2)
    expect(edited.objects[0].surfaces.map((surface) => surface.materialId)).toEqual(['stone', 'oak'])
  })
})
