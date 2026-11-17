import type { Material, SceneDocument, SceneObject } from './sceneTypes'

const id = (n: number) => `d0000000-0000-4000-8000-${String(n).padStart(12, '0')}`
const material = (n: number, name: string, baseColor: string, roughness = 0.65): Material => ({ id: id(n), name, baseColor, roughness, metalness: 0, tileSizeMeters: [0.6, 0.6], maps: {} })
export const kitchenSceneId = id(1)
export function createDemoKitchen(): SceneDocument {
  const materials = [material(10, 'Đá kem', '#ddd5c5', 0.3), material(11, 'Gỗ sồi', '#ad8156'), material(12, 'Sơn xanh olive', '#727c66'), material(13, 'Tường trắng ấm', '#ebe6dc'), material(14, 'Gạch nền', '#b8b3a7'), material(15, 'Kim loại tối', '#333b38', 0.28)]
  materials[5].metalness = 0.75
  let serial = 100
  const objects: SceneObject[] = []
  function box(name: string, category: string, position: SceneObject['position'], dimensions: SceneObject['dimensions'], faces: [string, string, number][]) {
    objects.push({ id: id(serial++), name, category, geometry: { kind: 'box' }, position, dimensions, rotation: [0, 0, 0, 1], visible: true, locked: false, measurementStatus: 'estimated', surfaces: faces.map(([faceId, semanticRole, mat]) => ({ id: id(serial++), faceId, semanticRole, materialId: materials[mat].id, uv: { rotationDegrees: 0, offset: [0, 0], repeat: [1, 1] } })) })
  }
  box('Sàn bếp', 'architecture', [0, -0.06, 0], [4.4, 0.12, 3.8], [['top', 'floor', 4]])
  box('Tường sau', 'architecture', [0, 1.4, -1.9], [4.4, 2.8, 0.1], [['front', 'wall', 3]])
  box('Tường trái', 'architecture', [-2.2, 1.4, 0], [0.1, 2.8, 3.8], [['right', 'wall', 3]])
  box('Thân tủ bếp', 'furniture', [0, 0.45, -1.45], [3.6, 0.84, 0.7], [['front', 'cabinet', 1], ['right', 'cabinet', 1], ['left', 'cabinet', 1]])
  box('Mặt bàn bếp', 'structure', [0, 0.9, -1.43], [3.7, 0.065, 0.8], [['top', 'countertop_top', 0], ['front', 'countertop_edge', 1], ['left', 'countertop_edge', 1], ['right', 'countertop_edge', 1]])
  for (let i = 0; i < 6; i++) {
    const x = -1.5 + i * 0.6
    box(`Cánh tủ ${i + 1}`, 'furniture', [x, 0.46, -1.075], [0.585, 0.76, 0.035], [['front', 'cabinet_front', 2]])
    box(`Tay nắm ${i + 1}`, 'furniture', [x + 0.19, 0.69, -1.04], [0.018, 0.14, 0.025], [['front', 'object', 5]])
  }
  box('Ốp tường bếp', 'architecture', [0, 1.27, -1.825], [3.7, 0.65, 0.035], [['front', 'backsplash', 0]])
  for (let i = 0; i < 3; i++) {
    box(`Tủ treo ${i + 1}`, 'furniture', [-1.22 + i * 1.22, 2.05, -1.62], [1.19, 0.72, 0.45], [['front', 'cabinet_front', 1], ['bottom', 'cabinet', 1], ['right', 'cabinet', 1]])
  }
  box('Bàn đảo', 'furniture', [0.3, 0.44, 0.45], [1.7, 0.86, 0.8], [['front', 'cabinet_front', 2], ['right', 'cabinet', 2], ['left', 'cabinet', 2]])
  box('Mặt bàn đảo', 'structure', [0.3, 0.9, 0.45], [1.9, 0.065, 1], [['top', 'countertop_top', 0], ['front', 'countertop_edge', 0], ['right', 'countertop_edge', 0], ['left', 'countertop_edge', 0]])
  return { schemaVersion: 1, sceneId: kitchenSceneId, projectId: id(2), revision: 0, units: 'meters', world: { handedness: 'right', up: 'Y', floorPlane: 'XZ', origin: [0, 0, 0] }, camera: { position: [5, 3.5, 6], target: [0, 1, 0] }, sourceImageId: null, calibration: { status: 'unknown', anchors: [], notes: 'Mẫu bếp tham số tự dựng; kích thước minh họa, không suy ra từ ảnh. Không sử dụng AI hoặc asset bên thứ ba.' }, materials, objects, evidence: [] }
}
