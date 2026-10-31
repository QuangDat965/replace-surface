---
name: dotnet-clean-architecture
description: Apply RoomForge's .NET 10 module boundaries when adding API endpoints, use cases, persistence, or background processing.
---

# .NET boundaries

Read `docs/ARCHITECTURE.md` and the active phase. Domain owns scene invariants and has no infrastructure dependency. Application owns use cases and provider-neutral contracts. Infrastructure owns EF, storage, and vendor HTTP. Api maps requests, validates boundary input, and returns stable errors. Add only abstractions needed by a current use case. Use cancellation tokens for I/O, nullable annotations, DI, and structured logs. Verify layer references and affected tests before claiming completion.
