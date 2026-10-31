export type Material = {
  id: string
  name: string
  baseColor: string
  roughness: number
  metalness: number
  tileSizeMeters: [number, number]
  maps: Record<string, string>
}

export type Surface = {
  id: string
  semanticRole: string
  faceId: string
  materialId: string
  uv?: { rotationDegrees: number; offset: [number, number]; repeat?: [number, number] }
}

export type SceneObject = {
  id: string
  name: string
  category: string
  geometry: { kind: string }
  position: [number, number, number]
  rotation: [number, number, number, number]
  dimensions: [number, number, number]
  visible: boolean
  locked: boolean
  measurementStatus: string
  surfaces: Surface[]
}

export type SceneDocument = {
  schemaVersion: number
  sceneId: string
  projectId: string
  revision: number
  units: 'meters'
  world: { handedness: string; up: 'Y'; floorPlane: 'XZ'; origin: [number, number, number] }
  camera: { position: [number, number, number]; target: [number, number, number] }
  sourceImageId: string | null
  calibration: { status: string; anchors: unknown[]; notes?: string }
  materials: Material[]
  objects: SceneObject[]
  deletedObjects?: SceneObject[]
  evidence: unknown[]
}

export const boxFaceIds = ['right', 'left', 'top', 'bottom', 'front', 'back']

export function findSurfaceOwner(scene: SceneDocument, surfaceId: string) {
  for (const object of scene.objects) {
    const surface = object.surfaces.find((entry) => entry.id === surfaceId)
    if (surface) return { object, surface, material: scene.materials.find((entry) => entry.id === surface.materialId) }
  }
  return null
}

export function assignMaterialToSurface(scene: SceneDocument, surfaceId: string, materialId: string) {
  if (!scene.materials.some((material) => material.id === materialId)) throw new Error('Material is not part of this scene')
  const updated = structuredClone(scene)
  const surface = updated.objects.flatMap((object) => object.surfaces).find((entry) => entry.id === surfaceId)
  if (!surface) throw new Error('Surface is not part of this scene')
  surface.materialId = materialId
  return updated
}
