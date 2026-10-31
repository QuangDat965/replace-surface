---
name: camera-calibration
description: Use when relating 2D room photos, perspective, image masks, vanishing points and physical measurement anchors.
---

# Camera Calibration

1. Record pixel coordinates and image dimensions.
2. Infer plane/camera hypotheses with uncertainty, never promise exact scale.
3. Require known distance/height or multiple constraints to calibrate metric scale.
4. Keep metric-vs-relative depth explicit.
5. Offer user correction for plane corners, horizon and reference length.
6. Reject inconsistent or physically impossible constraints with actionable feedback.

Return relevant contract changes, verification results, and a minimal handoff checklist.
