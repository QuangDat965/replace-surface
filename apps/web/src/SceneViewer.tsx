import { useEffect, useRef, useState } from 'react'
import { Canvas } from '@react-three/fiber'
import { Edges, Grid, OrbitControls, TransformControls } from '@react-three/drei'
import type { ThreeEvent } from '@react-three/fiber'
import type { Group } from 'three'
import { NoColorSpace, RepeatWrapping, SRGBColorSpace, TextureLoader } from 'three'
import type { Texture } from 'three'
import type { Material, SceneDocument, SceneObject, Surface } from './sceneTypes'
import { boxFaceIds } from './sceneTypes'
import { faceSizeMeters, textureRepeat } from './materialEditor'
import type { ObjectTransform } from './objectEditor'
import type { TransformMode } from './objectEditor'

type Props = {
  scene: SceneDocument
  selectedSurfaceId: string | null
  selectedObjectId: string | null
  transformMode: TransformMode
  onSelect: (surface: Surface) => void
  onSelectObject: (object: SceneObject) => void
  onTransform: (objectId: string, transform: ObjectTransform) => void
}
type PbrMaps = { map?: Texture; normalMap?: Texture; roughnessMap?: Texture; metalnessMap?: Texture }

function SurfaceMaterial({ material, object, surface, selected, attach }: { material: Material | undefined; object: SceneObject; surface: Surface | undefined; selected: boolean; attach: string }) {
  const [textureState, setTextureState] = useState<{ signature: string; maps: PbrMaps }>({ signature: '', maps: {} })
  const uv = surface?.uv
  const rotationRadians = (uv?.rotationDegrees ?? 0) * Math.PI / 180
  const offsetU = uv?.offset?.[0] ?? 0
  const offsetV = uv?.offset?.[1] ?? 0
  const repeatMultiplier = uv?.repeat ?? [1, 1]
  const dimensions = surface && material ? textureRepeat(faceSizeMeters(object, surface.faceId), material.tileSizeMeters, repeatMultiplier) : [1, 1]
  const repeatU = dimensions[0]
  const repeatV = dimensions[1]
  const baseColorUrl = material?.maps.baseColor
  const normalUrl = material?.maps.normal
  const roughnessUrl = material?.maps.roughness
  const metalnessUrl = material?.maps.metalness
  const mapsSignature = JSON.stringify([baseColorUrl, normalUrl, roughnessUrl, metalnessUrl])

  useEffect(() => {
    const loaded: Texture[] = []
    const next: PbrMaps = {}
    let cancelled = false
    const loader = new TextureLoader()
    const entries = [
      ['baseColor', baseColorUrl], ['normal', normalUrl], ['roughness', roughnessUrl], ['metalness', metalnessUrl],
    ] as const
    const loadTexture = (key: typeof entries[number][0], url: string) => new Promise<void>((resolve) => {
      loader.load(url, (texture) => {
        texture.wrapS = RepeatWrapping
        texture.wrapT = RepeatWrapping
        texture.repeat.set(repeatU, repeatV)
        texture.center.set(0.5, 0.5)
        texture.rotation = rotationRadians
        texture.offset.set(offsetU, offsetV)
        texture.colorSpace = key === 'baseColor' ? SRGBColorSpace : NoColorSpace
        texture.needsUpdate = true
        loaded.push(texture)
        if (key === 'baseColor') next.map = texture
        if (key === 'normal') next.normalMap = texture
        if (key === 'roughness') next.roughnessMap = texture
        if (key === 'metalness') next.metalnessMap = texture
        resolve()
      }, undefined, () => resolve())
    })
    void Promise.all(entries.flatMap(([key, url]) => url ? [loadTexture(key, url)] : [])).then(() => {
      if (cancelled) loaded.forEach((texture) => texture.dispose())
      else setTextureState({ signature: mapsSignature, maps: next })
    })
    return () => { cancelled = true; loaded.forEach((texture) => texture.dispose()) }
  }, [baseColorUrl, mapsSignature, metalnessUrl, normalUrl, offsetU, offsetV, repeatU, repeatV, roughnessUrl, rotationRadians])

  const maps = textureState.signature === mapsSignature ? textureState.maps : {}

  return <meshStandardMaterial
    attach={attach}
    color={selected ? '#f2a65a' : material?.baseColor ?? '#c8ccc7'}
    roughness={material?.roughness ?? 0.8}
    metalness={material?.metalness ?? 0}
    {...maps}
  />
}

function RoomObject({ object, scene, selectedSurfaceId, selectedObjectId, transformMode, onSelect, onSelectObject, onTransform }: Props & { object: SceneObject }) {
  const objectGroup = useRef<Group>(null)
  const byFace = new Map(object.surfaces.map((surface) => [surface.faceId, surface]))
  const materials = boxFaceIds.map((faceId) => {
    const surface = byFace.get(faceId)
    return { surface, material: scene.materials.find((item) => item.id === surface?.materialId) }
  })
  const handleClick = (event: ThreeEvent<MouseEvent>) => {
    const face = event.face?.materialIndex
    const surface = typeof face === 'number' ? materials[face]?.surface : undefined
    if (surface) { event.stopPropagation(); onSelect(surface) }
  }
  const commitTransform = () => {
    const group = objectGroup.current
    if (!group) return
    const dimensions = object.dimensions.map((dimension, index) => Math.max(0.01, dimension * Math.abs(group.scale.toArray()[index]))) as [number, number, number]
    const transform: ObjectTransform = {
      position: group.position.toArray() as [number, number, number],
      rotation: group.quaternion.toArray() as [number, number, number, number],
      dimensions,
    }
    group.scale.set(1, 1, 1)
    onTransform(object.id, transform)
  }
  const content = <group
    ref={objectGroup}
    position={object.position}
    quaternion={object.rotation}
    scale={[1, 1, 1]}
    visible={object.visible}
    onClick={(event: ThreeEvent<MouseEvent>) => { event.stopPropagation(); onSelectObject(object) }}
  >
      <mesh onClick={handleClick} castShadow receiveShadow>
        <boxGeometry args={object.dimensions} />
        {materials.map(({ material, surface }, index) => <SurfaceMaterial
          key={`${object.id}-${index}`}
          attach={`material-${index}`}
          material={material}
          object={object}
          surface={surface}
          selected={surface?.id === selectedSurfaceId}
        />)}
        {selectedObjectId === object.id && <Edges scale={1.01} threshold={15} color="#f2a65a" />}
      </mesh>
    </group>
  return selectedObjectId === object.id && selectedSurfaceId === null && !object.locked
    ? <TransformControls enabled={object.visible} mode={transformMode} onMouseUp={commitTransform}>{content}</TransformControls>
    : content
}

export function SceneViewer({ scene, selectedSurfaceId, selectedObjectId, transformMode, onSelect, onSelectObject, onTransform }: Props) {
  return <Canvas camera={{ position: [5, 3.5, 6], fov: 45 }} shadows dpr={[1, 2]}>
    <color attach="background" args={['#111a17']} />
    <ambientLight intensity={1.2} />
    <directionalLight position={[4, 8, 5]} intensity={2.2} castShadow />
    <group>
      {scene.objects.map((object) => <RoomObject key={object.id} object={object} scene={scene} selectedSurfaceId={selectedSurfaceId} selectedObjectId={selectedObjectId} transformMode={transformMode} onSelect={onSelect} onSelectObject={onSelectObject} onTransform={onTransform} />)}
    </group>
    <Grid position={[0, -0.11, 0]} args={[12, 12]} cellSize={0.5} cellThickness={0.6} cellColor="#45534d" sectionSize={1} sectionColor="#68776f" fadeDistance={16} infiniteGrid />
    <OrbitControls target={[0, 1, 0]} makeDefault minDistance={2} maxDistance={16} />
  </Canvas>
}
