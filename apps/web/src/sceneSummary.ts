export type SceneSummary = { objects: number; surfaces: number; materials: number; revision: number }

export function sceneSummary(value: unknown): SceneSummary {
  if (!value || typeof value !== 'object') throw new Error('Invalid sample scene')
  const scene = value as Record<string, unknown>
  if (scene.schemaVersion !== 1 || scene.units !== 'meters' || !Array.isArray(scene.objects) || !Array.isArray(scene.materials) || typeof scene.revision !== 'number')
    throw new Error('Unsupported sample scene contract')
  return {
    objects: scene.objects.length,
    surfaces: scene.objects.reduce<number>((count, object) => count + (typeof object === 'object' && object !== null && Array.isArray((object as Record<string, unknown>).surfaces) ? ((object as Record<string, unknown>).surfaces as unknown[]).length : 0), 0),
    materials: scene.materials.length,
    revision: scene.revision,
  }
}
