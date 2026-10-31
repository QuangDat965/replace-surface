# Orchestrated development workflow

1. **Discovery**: read root AGENTS and documents; inspect current code and tests; state assumptions and scope.
2. **Spec**: write a 10–20 line implementation plan and exact acceptance test. Choose the minimal needed skills.
3. **Design review**: 3D Engineer checks geometry/coordinates; Backend Engineer checks DTO, persistence and security; AI Integration Engineer checks real vendor HTTP contract and Vision Engineer checks output provenance for AI phases.
4. **Implement**: smallest end-to-end functional vertical slice; do not generate several unfinished services.
5. **Verify**: run appropriate build, automated tests, and manual demo. Separate witnessed results from expected behavior.
6. **Review**: Reviewer agent checks fake features, unit mismatches, stale scene revisions, wrong material UVs, async blocking, uncontrolled third-party AI dependencies.
7. **Handoff**: record file changes, commands, logs/test results, limitations, next phase dependencies and TODO owners.

## Definition of done
- Feature works through visible UI and durable backend or has explicit intentional mock label.
- Tests are added and executed, or skipped with transparent reason.
- Contracts and docs updated when changed.
- No secrets, paid-provider credentials or private image assets committed.
- Demo has a deterministic fixture and run instructions.

## Incremental task pattern
`Review current repository; activate relevant skill; implement <one capability> end-to-end; write tests; run build/tests; output evidence and known limitations. Do not expand into the next phase.`
