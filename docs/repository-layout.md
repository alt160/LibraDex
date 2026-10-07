# Repository layout

Production source is grouped under `src/`, preserving its existing folders. `LibraDex.csproj` and `README.md` stay at the repository root; solution references, build commands, public APIs, package identity, and catalog formats do not change.

Test/harness projects retain their existing locations and are excluded from the runtime library. Package-consumer validation maps `LibraDex` exclusively to the candidate feed, with an isolated package cache, so an already-published package of the same version cannot substitute for the candidate.

A source-layout-only update does not replace a published NuGet package or move its release tag. Existing package source links continue to reference the original release commit.
