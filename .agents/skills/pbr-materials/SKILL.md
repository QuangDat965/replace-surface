---
name: pbr-materials
description: Use when assigning material textures, tiling, UV coordinates and surface-specific real-world patterns.
---

# Pbr Materials

1. Support base color, roughness, normal, metallic/optional displacement maps.
2. Store real tile width/height in meters and rotation/offset.
3. Derive repeat from physical surface size, not viewport pixels.
4. Respect proper color-space, normal map channel and wrapping; prefer independent material instances when editing one surface.
5. Test 0.6m tile on 3.0 x 3.6m floor gives 5 x 6 repeats.
6. Preserve other face materials when changing countertop top.

Return relevant contract changes, verification results, and a minimal handoff checklist.
