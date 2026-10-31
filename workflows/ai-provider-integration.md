# AI provider integration

Read official vendor docs and `docs/AI_PROVIDER_CONTRACT.md`. Record actual capability, auth, payload, status, limits, output and retention rules. If documentation is missing, stop production adapter work and keep a labeled mock. Implement typed C# HTTP client and explicit vendor-to-canonical mapping, durable job state, safe asset download and secret handling. Test fixture success/error cases. Run a live smoke test only when configured and authorized; label unrun live behavior unverified.
