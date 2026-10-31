import { Euler, Quaternion } from 'three'
import type { SceneDocument, SceneObject } from './sceneTypes'

export type ObjectTransform = Pick<SceneObject, 'position' | 'rotation' | 'dimensions'>
export type TransformMode = 'translate' | 'rotate' | 'scale'

export function isStructuralObject(object: SceneObject): boolean {
  return object.locked || object.category === 'architecture' || object.category === 'structure' ||
    object.surfaces.some((surface) => ['floor', 'wall', 'countertop_top', 'countertop_edge'].includes(surface.semanticRole))
}

export function updateObjectTransform(scene: SceneDocument, objectId: string, transform: ObjectTransform): SceneDocument {
  if (transform.position.some((value) => !Number.isFinite(value)) ||
      transform.rotation.some((value) => !Number.isFinite(value)) ||
      transform.dimensions.some((value) => !Number.isFinite(value) || value <= 0)) {
    throw new Error('Object transform values must be finite and dimensions must be positive meters')
  }
  if (!scene.objects.some((object) => object.id === objectId)) throw new Error('Object is not part of this scene')
  return {
    ...scene,
    objects: scene.objects.map((object) => object.id === objectId ? { ...object, ...transform } : object),
  }
}

export function updateObjectVisibility(scene: SceneDocument, objectId: string, visible: boolean): SceneDocument {
  if (!scene.objects.some((object) => object.id === objectId)) throw new Error('Object is not part of this scene')
  return { ...scene, objects: scene.objects.map((object) => object.id === objectId ? { ...object, visible } : object) }
}

export function archiveOptionalObject(scene: SceneDocument, objectId: string): SceneDocument {
  const object = scene.objects.find((entry) => entry.id === objectId)
  if (!object) throw new Error('Object is not part of this scene')
  if (isStructuralObject(object)) throw new Error('Structural or locked objects cannot be deleted')
  return {
    ...scene,
    objects: scene.objects.filter((entry) => entry.id !== objectId),
    deletedObjects: [...(scene.deletedObjects ?? []), object],
  }
}

export function restoreArchivedObject(scene: SceneDocument, objectId: string): SceneDocument {
  const object = scene.deletedObjects?.find((entry) => entry.id === objectId)
  if (!object) throw new Error('Object is not in the deleted objects archive')
  if (scene.objects.some((entry) => entry.id === objectId)) throw new Error('Object already exists in this scene')
  return {
    ...scene,
    objects: [...scene.objects, object],
    deletedObjects: scene.deletedObjects!.filter((entry) => entry.id !== objectId),
  }
}

export function rotationDegrees(rotation: SceneObject['rotation']): [number, number, number] {
  const euler = new Euler().setFromQuaternion(new Quaternion(...rotation), 'XYZ')
  return [euler.x, euler.y, euler.z].map((value) => value * 180 / Math.PI) as [number, number, number]
}

export function rotationQuaternion(degrees: [number, number, number]): SceneObject['rotation'] {
  const euler = new Euler(...degrees.map((value) => value * Math.PI / 180) as [number, number, number], 'XYZ')
  const quaternion = new Quaternion().setFromEuler(euler)
  return [quaternion.x, quaternion.y, quaternion.z, quaternion.w]
}
