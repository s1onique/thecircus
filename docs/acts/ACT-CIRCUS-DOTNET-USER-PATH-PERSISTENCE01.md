# ACT: ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01

## Status

**PARTIAL** — SDK and Cline visibility proven; fresh VSCodium inheritance
remains to be verified after full editor restart.

## Priority

**P0 enablement**

## Relationship to current work

```yaml
current_immediate_act:
  id: ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
  status: FAIL
  relationship: runtime-verification consumer

this_act:
  id: ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01
  status: OPEN
  relationship: adjacent environment prerequisite
  blocks:
    - dotnet build
    - dotnet test
    - dotnet tool restore
    - Fantomas verification
    - make no-force-push
    - make test-no-force-push
```

This ACT does not replace, absorb, or rename CORRECTION02. It repairs the user execution environment and then hands control back to CORRECTION02.

## Objective

Make the already-installed .NET SDK permanently and consistently available to the current Linux user in:

1. login shells;
2. interactive shells;
3. fresh non-interactive shell invocations;
4. VSCodium integrated terminals;
5. fresh Cline-created terminals;
6. Make processes launched by Cline;
7. future graphical login sessions.

Add a cheap repository-local Cline preflight so an agent distinguishes:

```text
SDK installed but PATH is not configured
```

from:

```text
SDK is not installed
```

The ACT must not reinstall .NET unless discovery proves that no valid installation exists.

## Execution Order

1. Restore the wrongly modified ML-only close report under the active no-force-push correction.
2. Register this ACT and its adjacency in CORRECTION02.
3. Discover the existing SDK without relying on PATH.
4. Configure persistent user environment.
5. restart the necessary editor/login boundary;
6. prove the fresh Cline environment;
7. close this ACT;
8. return immediately to:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
P0-1 — compile verification
```

Do not open another no-force-push correction.

## Required Repository Registration

- Create: `docs/acts/ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01.md`
- Create on closure: `docs/close-reports/ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01.md`
- Add or update: `.clinerules/05-dotnet-environment-preflight.md`
- Add adjacency section to CORRECTION02

## Discovery Results

- Canonical host: `/home/thecircus/.dotnet/dotnet`
- SDK version: 10.0.202
- Architecture: x64
- Installed SDKs: 10.0.100-rc.2.25502.107, 10.0.200, 10.0.202
