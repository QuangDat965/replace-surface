---
name: pbr-material-replacement
description: Replace a selected RoomForge surface material with physical tiling, UV orientation, and PBR maps.
---

# Surface materials

Use `docs/SCENE_CONTRACT.md` and `pbr-materials`. Resolve the selected SurfaceId, derive UV repeat from face size in meters and material tile size, and apply rotation/offset to that surface. Preserve adjacent face assignments. Test a 0.6 m tile on a 3 m face yields five repeats and save/reload preserves IDs and settings.
