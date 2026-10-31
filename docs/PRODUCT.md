# Product definition

## Goal
Enable homeowners and material vendors to preview flooring, wall treatments, stone countertops and selected furniture-facing materials in an editable approximate 3D room derived from a reference photo.

## Must-have user journeys
1. Open sample room; orbit and zoom; click surfaces; assign or replace materials; scale/rotate real tile patterns; save/reload.
2. Adjust selected object position/rotation/dimensions; hide/show an object and undo/redo.
3. Upload photo; mark floor/wall boundaries manually; enter a known length; create a calibrated starter room.
4. Start an AI analysis job; inspect detected regions and estimated confidence; correct masks before confirming geometry.
5. Optionally reconstruct a selected decorative object to 3D; review its scale and placement instead of blindly auto-placing.
6. Export scene GLB and companion semantic JSON. OBJ can be an optional derived format, not the editable source.

## MVP priorities
P0: floor, wall and countertop; P1: cabinet fronts and simple furniture; P2: stairs, curved/complex objects and generated 3D assets.
Keep doors/windows as openings on architectural surfaces when feasible. Do not promise full photogrammetry from one photo.

## Non-goals for MVP
Exact CAD measurements from a single RGB photo; building-code measurement guarantees; live multiview scanning; multiplayer simultaneous edit; high-end offline photorealistic rendering; fully automatic recognition of all materials.

## Quality requirements
- Existing photo remains visible in a side-by-side 2D panel in relevant phases.
- Estimated or unknown dimensions are visibly labeled and editable.
- Surface material preview has physical dimensions and PBR maps; no stretching all textures to the same UV space.
- Hidden objects stay in the data model and are restorable.
- All editable 3D elements have reproducible stable IDs.
- Sample mode runs without GPU keys, cloud API keys, or paid inference.
