import { describe, expect, it } from 'vitest'
import { sceneSummary } from './sceneSummary'
import { assignMaterialToSurface, type SceneDocument } from './sceneTypes'

describe('sceneSummary', () => {
  it('counts authored scene parts', () => {
    expect(sceneSummary({ schemaVersion: 1, units: 'meters', revision: 1, objects: [{ surfaces: [{ id: 'a' }] }], materials: [{}] }))
      .toEqual({ objects: 1, surfaces: 1, materials: 1, revision: 1 })
  })
  it('rejects unsupported scene versions', () => {
    expect(() => sceneSummary({ schemaVersion: 2, units: 'meters', revision: 1, objects: [], materials: [] })).toThrow()
  })
})

describe('assignMaterialToSurface', () => {
  it('changes only the chosen SurfaceId and keeps the original unchanged', () => {
    const fixture = {
      materials: [{ id: 'stone' }, { id: 'oak' }],
      objects: [{ surfaces: [{ id: 'floor-surface', materialId: 'stone' }, { id: 'wall-surface', materialId: 'stone' }] }],
    } as unknown as SceneDocument
    const changed = assignMaterialToSurface(fixture, 'floor-surface', 'oak')
    expect(changed.objects[0].surfaces.map((surface) => surface.materialId)).toEqual(['oak', 'stone'])
    expect(fixture.objects[0].surfaces.map((surface) => surface.materialId)).toEqual(['stone', 'stone'])
  })
})
