# 2D region editor and manual calibration

Act as the RoomForge AI orchestrating coding agent. Follow `AGENTS.md`, `MASTER_PROMPT.md`, relevant `docs/*`, and the appropriate skills. Inspect the current repository state first.

## Scope of this phase
Create a split view: source room photo with React Konva polygon editing and 3D preview. Allow user to outline room floor and relevant wall boundaries, assign semantic roles, draw a reference dimension line and enter its known physical length, edit camera/room hypotheses, convert confirmed geometry to parametric 3D. Document limitations when a single photo does not expose actual floor plan. Ensure user can fix geometry without AI.

## Execution contract
1. Produce a short implementation plan and list of files expected to change.
2. Implement the **smallest complete vertical slice** satisfying this phase; no unrequested next-phase features.
3. Update APIs/contracts/docs and write meaningful tests.
4. Run available build, test and demo commands. Do not claim unrun results.
5. Finish with: changed files, test results, demo instructions, known limitations, and next-phase handoff.
