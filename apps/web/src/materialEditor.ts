import type { Material, SceneDocument, SceneObject } from './sceneTypes'

export type FaceSizeMeters = [number, number]

export function faceSizeMeters(object: SceneObject, faceId: string): FaceSizeMeters {
  const [width, height, depth] = object.dimensions
  return faceId === 'top' || faceId === 'bottom' ? [width, depth]
    : faceId === 'left' || faceId === 'right' ? [depth, height]
      : [width, height]
}

export function textureRepeat(faceSize: FaceSizeMeters, tileSizeMeters: FaceSizeMeters, multiplier: FaceSizeMeters = [1, 1]): FaceSizeMeters {
  return [faceSize[0] / tileSizeMeters[0] * multiplier[0], faceSize[1] / tileSizeMeters[1] * multiplier[1]]
}

export function updateMaterial(scene: SceneDocument, materialId: string, patch: Partial<Omit<Material, 'id'>>): SceneDocument {
  return {
    ...scene,
    materials: scene.materials.map((material) => material.id === materialId ? { ...material, ...patch } : material),
  }
}

export function updateSurfaceUv(scene: SceneDocument, surfaceId: string, patch: NonNullable<SceneObject['surfaces'][number]['uv']>): SceneDocument {
  return {
    ...scene,
    objects: scene.objects.map((object) => ({
      ...object,
      surfaces: object.surfaces.map((surface) => surface.id === surfaceId ? { ...surface, uv: { ...surface.uv, ...patch } } : surface),
    })),
  }
}
