---
name: camera-and-scale-calibration
description: Turn photo annotations and user measurements into approximate room scale and camera constraints.
---

# Camera and scale calibration

Use `docs/SCENE_CONTRACT.md` and `camera-calibration`. Record image coordinates, anchors, assumptions, and uncertainty. A single RGB image has no reliable absolute scale; request or accept a known measurement before labeling dimensions calibrated. Keep relative depth distinct from meters, reject invalid lengths, and make corrections editable. Verify the changed anchor affects intended scene dimensions only.
