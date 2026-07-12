# Experiment Baseline

## ACT-CIRCUS-FSHARP-ELM-SKELETON01

> **Status:** PASS — both the F# and Elm sides build, test, and gate cleanly.

---

## Toolchain versions

| Tool | Version | Verified |
|------|---------|----------|
| .NET SDK | 10.0.202 | `dotnet --version` |
| .NET Runtime | 10.0.6 | `dotnet --info` |
| Target framework | `net10.0` | `dotnet build Circus.sln -c Release --no-restore` |
| F# language | 10 stable | F# compiler on SDK 10.0.202 |
| Giraffe | 8.2.0 | `Directory.Packages.props` |
| Expecto | 11.1.0 | `Directory.Packages.props` |
| Microsoft.AspNetCore.TestHost | 10.0.0 | `Directory.Packages.props` |
| Elm compiler | 0.19.2 | `./node_modules/.bin/elm --version` (via `elm@0.19.2-0`) |
| elm-test | 0.19.2-0 | `./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm` |
| elm-format | 0.8.7 | `web/package.json` |
| Node.js | v26.0.0 | `node --version` |
| npm | 11.12.1 | `npm --version` |
| Leamas | 0.1.0+dev.df5525c11c72 | `leamas version` |

## Environment

| Measure | Value |
|---------|-------|
| Date | 2026-07-12 |
| OS | macOS 14.7 (Sonoma) |
| Architecture | ARM64 (Apple Silicon, `osx-arm64`) |
| Time zone | Europe/Moscow (UTC+3:00) |
| Leamas commit | `df5525c11c72` |

## Lines of code

Source and tests only. Generated, build-output and lock files excluded.

```bash
find src tests web/src web/tests \
  -type f \( -name '*.fs' -o -name '*.elm' \) -not -path '*/obj/*' | xargs wc -l
```

### F# production

| File | LOC |
|------|-----|
| `src/Circus.Domain/ProductIdentity.fs` | 35 |
| `src/Circus.Api/Http.fs` | 43 |
| `src/Circus.Api/Program.fs` | 88 |
| **F# production total** | **166** |

### F# tests

| File | LOC |
|------|-----|
| `tests/Circus.Domain.Tests/ProductIdentityTests.fs` | 40 |
| `tests/Circus.Domain.Tests/Program.fs` | 8 |
| `tests/Circus.Api.Tests/HttpContractTests.fs` | 159 |
| `tests/Circus.Api.Tests/Program.fs` | 8 |
| **F# test total** | **215** |

### Elm production

| File | LOC |
|------|-----|
| `web/src/Main.elm` | 17 |
| `web/src/App.elm` | 158 |
| `web/src/Api.elm` | 14 |
| `web/src/Product.elm` | 22 |
| `web/src/RemoteData.elm` | 46 |
| **Elm production total** | **257** |

### Elm tests

| File | LOC |
|------|-----|
| `web/tests/ApiTest.elm` | 45 |
| `web/tests/AppTest.elm` | 146 |
| **Elm test total** | **191** |

### Summary

| Measure | Result |
|---------|--------|
| F# production LOC | 166 |
| F# test LOC | 215 |
| Elm production LOC | 257 |
| Elm test LOC | 191 |

## Build timings (representative single run, macOS 14.7 ARM64)

| Measure | Command | Result |
|---------|---------|--------|
| Backend Release build | `time dotnet build Circus.sln -c Release --no-restore` | 0.89 s wall, 0 errors, 3 MSB3277 warnings (see "Compiler warnings" below) |
| Backend `Domain.dll` | `ls -lh src/Circus.Domain/bin/Release/net10.0/Circus.Domain.dll` | 15 KB |
| Backend `Api.dll` | `ls -lh src/Circus.Api/bin/Release/net10.0/Circus.Api.dll` | 24 KB |
| Elm optimised build | `time ./node_modules/.bin/elm make src/Main.elm --optimize --output=dist/app.js` | 0.450 s wall, `Success! Compiled 5 modules.` |
| Optimised `app.js` | `ls -lh web/dist/app.js` | 135 KB |
| `index.html` | `ls -lh web/dist/index.html` | 527 B |
| `styles.css` | `ls -lh web/dist/styles.css` | 1.4 KB |
| Full gate | `time make gate` | 7.722 s wall |

## Tests

### F# Expecto

```text
$ dotnet run --project tests/Circus.Domain.Tests -c Release --no-build --no-restore
EXPECTO! 4 tests run in 00:00:00.0306 for ProductIdentity – 4 passed, 0 ignored, 0 failed, 0 errored. Success!

$ dotnet run --project tests/Circus.Api.Tests -c Release --no-build --no-restore
EXPECTO! 10 tests run in 00:00:00.0958 for HTTP Contracts – 10 passed, 0 ignored, 0 failed, 0 errored. Success!
```

| Suite | Tests | Passed | Failed | Errored |
|-------|-------|--------|--------|---------|
| `Circus.Domain.Tests.ProductIdentity` | 4 | 4 | 0 | 0 |
| `Circus.Api.Tests.HTTP Contracts` | 10 | 10 | 0 | 0 |
| **Total F#** | **14** | **14** | **0** | **0** |

### Elm

```text
$ ./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm
elm-test 0.19.2-0
Running 17 tests.

TEST RUN PASSED
Duration: 143 ms
Passed:   17
Failed:   0
```

| Suite | Tests | Passed | Failed | Errored |
|-------|-------|--------|--------|---------|
| `ApiTest.decoderTests` | 5 | 5 | 0 | 0 |
| `AppTest.transitionTests` | 6 | 6 | 0 | 0 |
| `AppTest.viewTests` | 6 | 6 | 0 | 0 |
| **Total Elm** | **17** | **17** | **0** | **0** |

## Compiler warnings

| Source | Count | Notes |
|--------|-------|-------|
| Backend `dotnet build` | 3 | `MSB3277` FSharp.Core version conflicts between Giraffe 8.2.0 (transitive FSharp.Core 6.0.0) and the Circus projects (FSharp.Core 10.1.0). MSBuild picks the lower version. Not an error. Documented as a non-blocking follow-up. |
| Backend `warning FS*` | 0 | No F# compiler warnings. |
| Elm compiler | 0 | No warnings emitted. |

## Smoke verification (run via `make smoke` → `scripts/smoke.sh`)

```text
GET /health/live       → 200  application/json 17 bytes
GET /api/v1/about      → 200  application/json 142 bytes
GET /styles.css        → 200  text/css            (1.4 KB)
GET /                  → 200  text/html           (index.html)
GET /app.js            → 200  text/javascript     135 KB
```

All five endpoints return 200. The smoke script enforces these via exact `assert_equal` / `assert_contains` checks in `set -euo pipefail` mode, with response bodies captured into a private temporary directory that is removed by the trap on exit.

## Read-only gate evidence

```text
$ git status --porcelain=v1 > /tmp/pre.txt   (14 lines)
$ time make gate                            (7.722 s)
=== Native gate passed ===
$ git status --porcelain=v1 > /tmp/post.txt  (14 lines)
$ diff /tmp/pre.txt /tmp/post.txt           (empty)
```

The gate did not modify tracked or untracked repository state. The `dotnet restore Circus.sln --locked-mode` step fails the gate if `packages.lock.json` is stale rather than updating it.

## NuGet diagnostic trail (resolved)

Earlier in this ACT the TLS handshake to `api.nuget.org` hung after the Server Hello. The diagnostic trail is now historical:

```text
$ dotnet nuget list source
Registered Sources:
  1.  nuget.org [Enabled]
      https://api.nuget.org/v3/index.json

$ dotnet restore Circus.sln --use-lock-file --disable-parallel --disable-build-servers
  Restored /Volumes/UserData/Users/chistyakov/Projects/SPbNIX/thecircus/src/Circus.Domain/Circus.Domain.fsproj (in 3 ms).
  Restored /Volumes/UserData/Users/chistyakov/Projects/SPbNIX/thecircus/tests/Circus.Domain.Tests/Circus.Domain.Tests.fsproj (in 1.25 sec).
  Restored /Volumes/UserData/Users/chistyakov/Projects/SPbNIX/thecircus/tests/Circus.Api.Tests/Circus.Api.Tests.fsproj (in 7 ms).
  Restored /Volumes/UserData/Users/chistyakov/Projects/SPbNIX/thecircus/src/Circus.Api/Circus.Api.fsproj (in 11 ms).
```

Lock files generated by `dotnet restore --use-lock-file` and present in the working tree:

```text
src/Circus.Api/packages.lock.json
src/Circus.Domain/packages.lock.json
tests/Circus.Api.Tests/packages.lock.json
tests/Circus.Domain.Tests/packages.lock.json
```

## Elm diagnostic trail (resolved)

```text
$ ./node_modules/.bin/elm --version
0.19.2

$ ./node_modules/.bin/elm-test --compiler ./node_modules/.bin/elm
elm-test 0.19.2-0
Running 17 tests.

TEST RUN PASSED
Duration: 143 ms
```

## Gate status

| Component | State |
|-----------|-------|
| `make factorize` | PASS — `doctrine verify: OK` |
| `make restore` | PASS — `dotnet tool restore`, `dotnet restore --locked-mode`, `npm ci` |
| `make format-check` | PASS — fantomas `--check` clean, elm-format `--validate` returns `[]` |
| `make build-backend` | PASS — `Build succeeded`, 0 errors, 3 MSB3277 warnings |
| `make test-backend` | PASS — 4/4 + 10/10 = 14/14 Expecto tests |
| `make build-web` | PASS — `Success! Compiled 5 modules.`, `app.js` 135 KB |
| `make test-web` | PASS — 17/17 elm-test cases in 143 ms |
| `make smoke` | PASS — all five HTTP probes return 200 |
| `make gate` | PASS — `=== Native gate passed ===`, read-only, 7.7 s wall |

## Friction analysis

| Area | Observation |
|------|-------------|
| Central package management | Used `<PackageVersion>` entries in `Directory.Packages.props` and explicit `<PackageReference>` per project. |
| Test project output type | `dotnet test` did not pick up the Expecto `[<EntryPoint>]` because no test adapter was wired. Switched to `OutputType=Exe` and `dotnet run --project ...`. |
| Expecto entry point | The shipped 11.1.0 signature is `Tests.runTestsInAssemblyWithCLIArgs : CLIArguments seq -> string[] -> int`. The test program supplies the `tests` value explicitly via `Tests.runTestsWithCLIArgs`. |
| Giraffe HttpHandler | The `json` combinator is `HttpHandler`, not `HttpFunc`. The correct form is `fun (_next : HttpFunc) (ctx : HttpContext) -> (json body : HttpHandler) _next ctx`. |
| Static-file middleware | `StaticFileOptions` and `PhysicalFileProvider` are both `IDisposable`; the F# compiler with `TreatWarningsAsErrors=true` requires the `new` prefix. |
| TestServer construction | `Host.CreateDefaultBuilder().ConfigureWebHost(web.UseTestServer()...).Build()` does not register `TestServer` in DI in .NET 10. The current pattern is `new TestServer(WebHostBuilder().UseTestServer()...)`. The deprecation is restricted to `HttpContractTests.fs` via `#nowarn "0044"` with an explanatory comment. |
| Elm `Event.expect` API | The 2.2.1 `Test.Html.Event.expect` signature is `msg -> Event msg -> Expectation`. The retry pattern is `query |> Event.simulate Event.click |> Event.expect msg`. |
| Elm `source-directories` | The application manifest keeps only `src/`; tests live in `web/tests/` and are discovered by `elm-test` via `test-dependencies`. |
| Elm version mismatch | The initially installed `elm-test@0.19.1-revision17` bundled elm 0.19.1; switching to `elm-test@0.19.2-0` matched the project's pinned 0.19.2 compiler. |
| Elm package downloads | The `package.elm-lang.org` CDN intermittently dropped connections during this ACT. Once the CDN responded, the project-local binaries (`./node_modules/.bin/elm` and `./node_modules/.bin/elm-test`) ran without further network access. |
| Web/dist discovery | The compiled `web/dist` is found via a fixed ladder of candidate paths up from `AppContext.BaseDirectory`. No absolute machine-specific path is used. |
| FSharp.Core MSB3277 | The R2 attempt to force-pin FSharp.Core via `<PackageReference Update="FSharp.Core" />` triggered a NuGet restore stall. The update is documented but **not** included in the committed code; F# application projects are designed to consume the SDK's implicit `FSharp.Core`, which advances with the SDK. |

## Follow-up actions

1. Investigate the `MSB3277` FSharp.Core conflict by upgrading Giraffe to a version built against FSharp.Core 10.x (outside this ACT's tool pinning).
2. Migrate the test host off `WebHostBuilder` when Giraffe gains a documented `WebApplicationFactory` integration.
3. Capture incremental backend build timing after a real source change.
4. Run `make format` once on first checkout; the gate fails when files diverge from the formatter's expectations.