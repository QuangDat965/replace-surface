---
name: parametric-geometry
description: Use when generating room shells, floor/wall planes, countertops, extruded geometry or editable primitive meshes.
---

# Parametric Geometry

1. Keep world in meters, +Y up, ground XZ.
2. Start with floor polygon, walls anchored to polygon edges and primitive countertops.
3. Ensure winding, normals, UVs, raycast selection and nonzero dimensions.
4. Surface IDs must survive geometry updates when semantic role stays the same.
5. Test dimensions, transforms, holes/openings where supported, and GLB export.

Return relevant contract changes, verification results, and a minimal handoff checklist.
