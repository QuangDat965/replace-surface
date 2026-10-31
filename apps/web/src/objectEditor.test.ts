import { describe, expect, it } from 'vitest'
import {
  archiveOptionalObject, isStructuralObject, restoreArchivedObject, rotationDegrees, rotationQuaternion,
  updateObjectTransform, updateObjectVisibility,
} from './objectEditor'
import type { SceneDocument } from './sceneTypes'

const scene = {
  schemaVersion: 1, sceneId: 'scene', projectId: 'project', revision: 1, units: 'meters',
  world: { handedness: 'right', up: 'Y', floorPlane: 'XZ', origin: [0, 0, 0] },
  camera: { position: [5, 3, 6], target: [0, 1, 0] }, sourceImageId: null,
  calibration: { status: 'unknown', anchors: [] }, materials: [{ id: 'mat', name: 'Stone', baseColor: '#fff', roughness: 0.5, metalness: 0, tileSizeMeters: [1, 1], maps: {} }],
  objects: [
    { id: 'floor-id', name: 'Floor', category: 'architecture', geometry: { kind: 'box' }, position: [0, 0, 0], rotation: [0, 0, 0, 1], dimensions: [4, 0.1, 3], visible: true, locked: false, measurementStatus: 'user_supplied', surfaces: [{ id: 'floor-surface', semanticRole: 'floor', faceId: 'top', materialId: 'mat', uv: { rotationDegrees: 15, offset: [0.2, 0.1] } }] },
    { id: 'chair-id', name: 'Chair', category: 'furniture', geometry: { kind: 'box' }, position: [0, 0.5, 0], rotation: [0, 0, 0, 1], dimensions: [0.5, 0.9, 0.5], visible: true, locked: false, measurementStatus: 'estimated', surfaces: [] },
  ], evidence: [],
} as unknown as SceneDocument

describe('object editing scene mutations', () => {
  it('edits only the selected object and preserves object and surface identity, assignments and UV', () => {
    const original = scene.objects[0]
    const updated = updateObjectTransform(scene, original.id, {
      position: [1, 0.2, -1], rotation: rotationQuaternion([0, 45, 0]), dimensions: [5, 0.12, 3.2],
    })
    const changed = updated.objects[0]
    expect(changed.id).toBe(original.id)
    expect(changed.surfaces).toEqual(original.surfaces)
    expect(changed.surfaces[0].materialId).toBe(original.surfaces[0].materialId)
    expect(changed.surfaces[0].uv).toEqual(original.surfaces[0].uv)
    expect(updated.objects[1]).toEqual(scene.objects[1])
    expect(rotationDegrees(changed.rotation)[1]).toBeCloseTo(45)
  })

  it('toggles visibility without removing the object or changing IDs', () => {
    const hidden = updateObjectVisibility(scene, 'chair-id', false)
    expect(hidden.objects[1].visible).toBe(false)
    expect(hidden.objects[1].id).toBe('chair-id')
    expect(updateObjectVisibility(hidden, 'chair-id', true).objects[1].visible).toBe(true)
  })

  it('archives/restores optional objects with stable object identity and rejects structural deletion', () => {
    expect(isStructuralObject(scene.objects[0])).toBe(true)
    expect(isStructuralObject(scene.objects[1])).toBe(false)
    expect(() => archiveOptionalObject(scene, 'floor-id')).toThrow(/Structural or locked/)
    const archived = archiveOptionalObject(scene, 'chair-id')
    expect(archived.objects.map((object) => object.id)).toEqual(['floor-id'])
    expect(archived.deletedObjects?.[0].id).toBe('chair-id')
    const restored = restoreArchivedObject(archived, 'chair-id')
    expect(restored.objects.map((object) => object.id)).toEqual(['floor-id', 'chair-id'])
    expect(restored.deletedObjects).toEqual([])
    expect(restored.objects[1]).toEqual(scene.objects[1])
  })

  it('rejects invalid dimensions and non-finite transform values', () => {
    expect(() => updateObjectTransform(scene, 'chair-id', { position: [0, 0, 0], rotation: [0, 0, 0, 1], dimensions: [0, 1, 1] })).toThrow(/finite and dimensions/)
    expect(() => updateObjectTransform(scene, 'chair-id', { position: [Number.NaN, 0, 0], rotation: [0, 0, 0, 1], dimensions: [1, 1, 1] })).toThrow(/finite and dimensions/)
  })
})
