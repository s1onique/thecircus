# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION04 — CLOSED**

The remaining R1 blockers are resolved. The archive rollback tracks
explicit recovery state and never destroys the recovery copy on a
partial rollback; the ordinary pre-effect failure case is now tested;
the launcher mutation test is a real equality assertion; the committed
tree reproduces a `pass` detached gate.

## Title

Track recovery state explicitly, prove the ordinary pre-effect path,
harden the launcher mutation test, and bind the detached gate evidence.

## Objective

Address the remaining R1 blockers raised against CORRECTION03:

1. `extractAtomicWith` must not destroy the only recovery copy when a
   partial rollback leaves the previous install in `previousDir`.
2. The ordinary pre-effect move failure case (the second move throws
   before effect) must be tested independently of the effect-then-throw
   case.
3. The launcher policy test must compute the expected equality from the
   parsed manifest, not from a hardcoded string match; the mutation case
   must assert that a mutated manifest fails the assertion.
4. The committed clean tree must regenerate a `pass` detached gate
   summary bound to the actual implementation commit.

## Mandated order

1. Reimplemented `extractAtomicWith` to record an explicit recovery
   report (`notes`, `previousRecovered`, `previousPreserved`) and to
   keep `previousDir` on disk unless the recovery is conclusive. The
   `finally` block now only disposes the install-temp and the
   previous-temp when the previous install is observably back in
   `absoluteFinal` (or the cold-start path was taken).
2. Replaced the `restoreColdStart` / `restorePrevious` helpers with a
   `restorePrevious () : notes * previousRecovered * previousPreserved`
   triple that the failure path uses to compose the `Error` label.
3. Added `an ordinary failed second-move (pre-effect) preserves the
   previous tree` and `an ordinary failed first-move (pre-effect)
   preserves the previous tree` cases to `ArchivesTests.fs`. Added
   `a failed delete of the candidate leaves the previous installation
   reachable` to assert the error detail announces the recovery-copy
   path and that the final directory remains on disk.
4. Rewrote `LauncherPolicyTests.fs` so the mutation case derives
   `BOOTSTRAP_IMAGE='reference@sha256'` from the parsed manifest and
   asserts the launcher equality against the *mutated* manifest. The
   committed manifest + launcher case asserts `true`; both the
   mutated-digest and mutated-reference cases assert `false`.
5. Re-ran the build, the Expecto suite, the launcher policy, and the
   detached gate chain against the committed tree; recorded the
   gate tree-OID (`104a53d9f652`) in the ACT and close report.

## Outcome summary

| claim | evidence |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| Expecto suite | 31/31 tests passing |
| Archive rollback: ordinary failed second-move (pre-effect) | `Error`; `old.txt` is back in the final dir; `new.txt` is gone |
| Archive rollback: ordinary failed first-move (pre-effect) | `Error`; the previous install is preserved; the previous-temp is consumed because `hadPrevious && finalPresent` |
| Archive rollback: failed second-move that mutates then throws | `Error`; the previous install is live; the failed candidate is gone |
| Archive rollback: failed extraction | `Error`; the previous install is preserved |
| Archive rollback: failed verification | `Error`; the previous install is restored; the unverified candidate is gone |
| Archive rollback: failed delete of the candidate | `Error`; the error detail announces "rollback incomplete; previous installation retained at …"; the final directory is still on disk |
| Archive rollback: cold-start failed second move | `Error`; no `.circus-install-*` directory remains in the temp root |
| Launcher policy: Python / `jq` absence | `clean` |
| Launcher policy: pinned image derived from manifest | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` matches the manifest |
| Launcher policy: manifest mutation breaks equality | committed manifest + launcher matches; mutated digest + launcher does not match; mutated reference + launcher does not match |
| Manifest validation: non-hex digest rejected | `validate` returns `Error` for `"sha256:???..."` |
| Detached gate | `pass (3/3 pass) tree=104a53d9f652`; canonical vocabulary ok; tree-OID binding ok; leamas digest pass |
| `git status` post-build | working tree clean (gate summary is `.gitignore`d) |

## Out of scope for this ACT

* The inherited frontend-CA, PostgreSQL-fixture, and API/Testcontainers
  work remains unchanged; the next ACT may address it after this
  installation authority is genuinely failure-safe.
* Wiring `test-devhost` into the canonical `gate` target was *not*
  included in this ACT; the closed `gate` target continues to invoke
  the existing pre-CORRECTION02 tests. The successor may address the
  Makefile aggregation if it needs to enforce `test-devhost`
  continuously.
* The CORRECTION02 close report's wording about `make test` running
  `test-devhost` remains in the historical record; this ACT did not
  overwrite it.

## Commit boundaries

* **Starting commit:** `c04b0b6` (CORRECTION03 implementation)
* **Implementation commit:** recorded at commit time below
* **Final tested commit:** same as implementation commit
