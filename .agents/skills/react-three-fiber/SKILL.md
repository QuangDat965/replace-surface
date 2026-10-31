---
name: react-three-fiber
description: Build or repair the RoomForge React Three Fiber scene viewer, selection, and editor interactions.
---

# React Three Fiber viewer

Read `docs/SCENE_CONTRACT.md` and the active phase. Map semantic objects to meshes with meter-scale dimensions and Y-up/XZ coordinates. Bind click targets to SurfaceId, including individual faces; preserve object IDs through edits. Keep React editor state separate from persisted scene revisions. Test camera controls, selection, material assignment, error states, and save/reload. A visible control must perform its action or be explicitly disabled.
