# Test

| Goal | Command |
|---|---|
| All tests | `dotnet test FSharpened.slnx` |
| One test | `dotnet test --filter "FullyQualifiedName~<TestName>"` |
| With coverage | `dotnet test FSharpened.slnx --collect:"XPlat Code Coverage"` |
| Platform matrix | via CI; locally follow `.agents/skills/run-platform-matrix.md` |
| AOT | per-RID publish smoke in `docs/setup/build.md`; mobile lane in `tests/aot-mobile-sim/` |
| Benchmarks | `.agents/skills/run-benchmarks.md` |

Test rules: every pillar gets contract tests before implementation; every fallback path has a test that forces the failure; every backend has a "deliberately fail" test (Invariant 9). Tests must not assume an OS: use `FSharpened.Platform`, not `#if`.
