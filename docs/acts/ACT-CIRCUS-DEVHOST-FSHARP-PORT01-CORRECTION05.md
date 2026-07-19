# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION05 — PARTIAL**

The previous-install recovery-copy defect and canonical-gate wiring are
closed, but the CORRECTION05 review found that cold-start installation and
verification failures could leave a failed or unverified candidate live at
the final path. Its cold-start test did not assert final-path absence, and
the uploaded digest reported the detached gate source as missing. These
remaining findings are addressed by CORRECTION06.

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
4. Reported a detached gate run, but the subsequently supplied digest
   recorded `source_status=missing`, `overall_status=unavailable`, and
   `checks_total=0`; CORRECTION06 replaces this with a bundled artifact
   and mechanically complete transcript.

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
| Archive rollback: cold-start failed second move | **insufficient in CORRECTION05** — only `.circus-install-*` absence was asserted; final-path absence was not tested |
| Launcher policy: Python / `jq` absence | `clean` |
| Launcher policy: pinned image derived from manifest | `BOOTSTRAP_IMAGE='mcr.microsoft.com/dotnet/sdk:10.0.202-noble@sha256:caa1a2d363812eb21df9c56f01fa59d5d81bbb03103cb6a48f32dd7e80855616'` matches the manifest |
| Launcher policy: manifest mutation breaks equality | committed manifest + launcher matches; mutated digest + launcher does not match; mutated reference + launcher does not match |
| Manifest validation: non-hex digest rejected | `validate` returns `Error` for `"sha256:???..."` |
| Canonical `gate` invokes `test-devhost` | `gate: factorize format-check test-backend test-devhost test-web smoke` |
| Uploaded detached gate | **unavailable** — supplied digest recorded `source_status=missing`, `overall_status=unavailable`, `checks_total=0` |
| `git status` post-build | working tree clean (gate summary is `.gitignore`d) |
| `git diff --check HEAD` | clean |

## Commit boundaries

* **Starting commit:** `3248840` (CORRECTION04 implementation)
* **Implementation commit:** `12bd4436f92059664d14a1d1224238efcda559d2`
* **Implementation tree OID:** `e2dbce6d245abfdd4d48a44b0102770cadef0dce`
* **Final CORRECTION05 commit:** `12bd4436f92059664d14a1d1224238efcda559d2`

## Out of scope for this ACT

* The inherited frontend-CA, PostgreSQL-fixture, and API/Testcontainers
  work remains unchanged; the next ACT may address it after this
  installation authority is genuinely failure-safe.
* The CORRECTION02 close report's wording about `make test` running
  `test-devhost` is acknowledged: the canonical aggregate is now `gate`
  (which includes `test-devhost`). `make test` is a separate target
  that the CORRECTION02 document over-stated; this ACT does not edit
  the historical CORRECTION02 record.
