---
name: scene-graph-modeling
description: Change canonical SceneDocument JSON, object and surface identity, revision checks, or serialization.
---

# Semantic scene graph

Use `docs/SCENE_CONTRACT.md` as the contract and `scene-modeling` for detailed invariants. Assign UUIDs only to new entities, preserve existing IDs, and validate references and schema versions at API boundaries. Store meters, Y-up/XZ coordinates, measurement status, and provenance. Test JSON round trips and conflict handling for scene mutations.
