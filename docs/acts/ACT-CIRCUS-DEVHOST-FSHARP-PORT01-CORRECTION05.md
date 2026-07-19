# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05 — CLOSED**

The last recovery-copy deletion defect is fixed. The `extractAtomicWith`
cleanup now keys off an explicit decision (`canDeletePrevious` and
`canDeleteInstall`) rather than ambient filesystem existence. The
devhost suite is wired into the canonical `gate` target. The detached
gate evidence is bound to the implementation commit.

## Title

Stop deleting the recovery copy in `finally`, prove it in a test,
wire the devhost suite into `gate`, and bind the closure evidence to a
real commit.

## Objective

Address the four remaining R1 blockers raised against CORRECTION04:

1. `extractAtomicWith` must not destroy the only recovery copy when a
   partial rollback leaves the previous install in `previousDir`.
2. The failed-delete test must inspect the recovery-copy directory
   itself, not just the failed candidate.
3. The canonical `gate` target must invoke the devhost test suite.
4. The committed clean tree must reproduce a `pass` detached gate
   summary bound to the actual implementation commit.

## Mandated order

1. Replaced the ambient-existence `finally` condition in
   `extractAtomicWith` with explicit decision flags
   (`canDeletePrevious`, `canDeleteInstall`) that are set only after
   the new install is verified-and-committed or after the previous
   install is observably restored.
2. Extended the failed-delete test to assert the retained
   `.circus-previous-*` directory contains `old.txt`; the test now
   passes after the implementation fix.
3. Added `test-devhost` to the `gate` target so the devhost suite is
   continuously enforced on the canonical aggregate.
4. Re-ran the build, the Expecto suite, the launcher policy, the
   detached gate chain against the committed tree; recorded the gate
   tree-OID (`a7186fa50a9d`) in the ACT and close report.

## Outcome summary

| claim | evidence |
| --- | --- |
| `dotnet build tools/Circus.DevHost/Circus.DevHost.fsproj -c Release` | 0 warnings, 0 errors |
| `dotnet build tests/Circus.DevHost.Tests/Circus.DevHost.Tests.fsproj -c Release` | 0 warnings, 0 errors |
| Expecto suite | 31/31 tests passing |
| Archive rollback: failed delete of the candidate | `Error`; the error detail announces "rollback incomplete; previous installation retained at …"; the retained `.circus-previous-*` directory contains `old.txt`; the failed candidate remains live |
| Archive rollback: ordinary failed second-move (pre-effect) | `Error`; `old.txt` is back in the final dir; `new.txt` is gone |
| Archive rollback: ordinary failed first-move (pre-effect) | `Error`; the previous install is preserved; the previous-temp is consumed because `hadPrevious && finalPresent` |
| Archive rollback: failed second-move that mutates then throws | `Error`; the previous install is live; the failed candidate is gone |
| Archive rollback: failed extraction | `Error`; the previous install is preserved |
| Archive rollback: failed verification | `Error`; the previous install is restored; the unverified candidate is gone |
| Archive rollback: cold-start failed second move | `Error`; no `.circus-install-*` directory remains in the temp root |
| Launcher policy: Python / `jq` absence | `clean` |
| Launcher policy: pinned image derived from manifest | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` matches the manifest |
| Launcher policy: manifest mutation breaks equality | committed manifest + launcher matches; mutated digest + launcher does not match; mutated reference + launcher does not match |
| Manifest validation: non-hex digest rejected | `validate` returns `Error` for `"sha256:???..."` |
| Canonical `gate` invokes `test-devhost` | `gate: factorize format-check test-backend test-devhost test-web smoke` |
| Detached gate | `pass (3/3 pass) tree=a7186fa50a9d`; canonical vocabulary ok; tree-OID binding ok; leamas digest pass |
| `git status` post-build | working tree clean (gate summary is `.gitignore`d) |
| `git diff --check HEAD` | clean |

## Commit boundaries

* **Starting commit:** `3248840` (CORRECTION04 implementation)
* **Implementation commit:** recorded at commit time below
* **Final tested commit:** same as implementation commit

## Out of scope for this ACT

* The inherited frontend-CA, PostgreSQL-fixture, and API/Testcontainers
  work remains unchanged; the next ACT may address it after this
  installation authority is genuinely failure-safe.
* The CORRECTION02 close report's wording about `make test` running
  `test-devhost` is acknowledged: the canonical aggregate is now `gate`
  (which includes `test-devhost`). `make test` is a separate target
  that the CORRECTION02 document over-stated; this ACT does not edit
  the historical CORRECTION02 record.
