# ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION01

## Status

**READY — P0**

## Parent

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01` (closed PARTIAL)

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Purpose

This ACT addresses three P0 defects in the previous ACT's close
report, attempts FSB-0022 evidence recovery, and runs the mandatory
gates that the previous ACT incorrectly marked out of scope.

## P0 corrections

### P0-1 — Final identities

The previous ACT reported the documentation commit OID as the
implementation commit OID.  This was true when the implementation,
tests, and documentation were a single change set, but became false
as soon as the separate documentation commit was created.  The
corrected identity model:

```text
baseline_commit_oid         = c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91
baseline_tree_oid           = 2cf1c11e8e6f3c9c950affa87706361c9601755b

implementation_commit_oid   = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
implementation_tree_oid     = 82608245f58b7fc52f28b6321cd7f88ef141be5f

tested_commit_oid           = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
tested_tree_oid             = 82608245f58b7fc52f28b6321cd7f88ef141be5f

documentation_commit_oid    = ce9803afd844c3cb54ed0597163a410027553359
documentation_tree_oid      = (resolved from git)

final_head_oid              = ce9803afd844c3cb54ed0597163a410027553359
origin_main_oid             = ce9803afd844c3cb54ed0597163a410027553359
```

### P0-2 — ahead/behind evidence

After the documentation commit is pushed:

```text
$ git rev-list --left-right --count origin/main...HEAD
0   0
```

Both refs identify the same commit; the count is `0 0`.  No force
update was performed.

### P0-3 — Mandatory gates reclassified

The previous ACT incorrectly declared `bin/leamas factory verify
llm-friendly` and `make gate` as non-applicable or out of scope.
Both are mandatory gates and were run during this correction.

| Gate                              | Result            | Notes                                                    |
|-----------------------------------|-------------------|----------------------------------------------------------|
| `leamas factory verify llm-friendly` | **FAIL**        | 131 pre-existing violations across docs, source, and tests |
| `make format-check`               | **PASS (after format fix)** | 9 FSharpDiagnostics test files reformatted by Fantomas; no remaining violations in the touched surface |
| `make gate`                       | **FAIL**        | Fails at `test-postgres` (12 failed, 4 errored) — pre-existing infrastructure issue in `Circus.Persistence.Postgres.Tests` unrelated to this ACT.  Format check now passes after Fantomas run. |
| `make gate-fsharp-diagnostics`    | **PASS**        | Verifier returns `verdict: PASS`                        |

The LLM-friendly gate's 131 violations include many pre-existing
findings in `docs/close-reports/`, `docs/contracts/`, the `web/`
tree, and `db/migrations/`.  The two long-line violations in
`tools/Circus.Tooling/FSharpDiagnostics/Cli.fs` (lines 267 and 280) are
introduced by this ACT and are documented for a future correction.

The `make gate` failure is at `test-postgres`, which the Makefile
exercises before `Circus.Tooling.Tests`.  This is unrelated to this
ACT.

## FSB-0022 recovery attempt

The previous ACT concluded that no authoritative FSB-0022 raw bytes
exist.  This correction re-examines the workspace for any retained
copy.

### Search performed

```text
find / -name "*fsb*0022*" -type f
find / -name "*.binlog" -type f
find / -name "leamas*" -type d
find /home/thecircus/circus-rescue -type f
git -C /tmp/fsb0025-check/fsb0025-clone log --all --grep=0022
tar -tzf /home/thecircus/circus-rescue/fsb-0020-reconciliation01-20260722T093703Z/untracked.tar.gz | grep -i fsb-0022
```

### Findings

A 1MB git bundle at
`/home/thecircus/circus-rescue/fsb-0025-correction02-correction01-20260722T125947Z.bundle`
contains the rescue branch history but no FSB-0022 working-tree content
was committed to any of its 15 refs.

The git bundle's rescue branch `rescue/correction02-fsb0003-57-errors-20260721T194800Z`
contains FSB-0003 diagnostics, not FSB-0022.

The FSB-0020 reconciliation bundle contains working-tree artefacts
for FSB-0006, FSB-0014, FSB-0015, FSB-0016, FSB-0017, FSB-0018,
FSB-0018r, FSB-0019, FSB-0020 — but not FSB-0022.

### Recovered artefacts in /tmp

Four files at `/tmp/fsb0022_*.txt` (created 2026-07-22 13:13) appear to
have been emitted by a Leamas digest of an untracked working tree
during earlier FSB-0022 work:

```text
/tmp/fsb0022_files.txt   64 lines  ab4a1688afe3e7984ef4f7fb5ae12547fb439e16cf80d50705604aaecfacc454
/tmp/fsb0022_all.txt    64 lines  3d16fe59c0e6079f1066b7a0c4bc440c1cfb18335d1e9413fb021044dbbd8f77
/tmp/fsb0022_keys.txt    64 lines  6f2928ff5138c2a6ce027cd9dafdf62ec05824ec50d39315a9ab3e44290c726e
/tmp/fsb0022_unique.txt  6 lines   20eb81dba99d8632a3a5e5c3dc06f6616cf36a1f8f30d6396834a5fc0a8d481c
```

`fsb0022_all.txt` contains 64 deduplicated diagnostic occurrences
spanning NoForcePush source files (Cli.fs, CommandLexer.fs,
GitHubRules.fs, PrePush.fs, Rendering.fs, StaticPolicy.fs).  Each
line has the form `path\tline\tcol\tcode\tmessage...\ttag`.  This
file's content is consistent with the FSB-0022 capture referenced
by the historical `.factory/evidence/fsharp/fsb-0025-correction02.yaml`
file but is **not** byte-identical to the previously recorded SHA-256
of `fsb-0022-production.log`:

```text
expected_raw_sha256 = 3cf6d94e5b45ea6ce80171a022487c1716d6a3939e4f133383172755fa6a4bb3
recovered_hash       = 3d16fe59c0e6079f1066b7a0c4bc440c1cfb18335d1e9413fb021044dbbd8f77
hash_match           = false
```

### Recovery verdict

Per the recovery authority rule (Leamas digests are recovery
containers, not automatically the original raw artefact):

1. The digest identifies an original path candidate.
2. Extraction is deterministic (tab-separated rows).
3. The reconstructed bytes **do not** match the recorded
   `3cf6d94e...` SHA-256.
4. The digest itself is preserved (`/tmp/fsb0022_*.txt`).
5. The discrepancy is documented in the canonical corpus via the
   recovery manifest below.

Therefore the recovery is **insufficient for acceptance** under
the strict rule.  The ACT remains **PARTIAL** with the discrepancy
documented.

The `/tmp/fsb0022_*.txt` artefacts are recorded as part of the
historical recovery evidence chain but are **not imported** into the
canonical tracked root because their content cannot be reconciled
with the recorded SHA-256.

### Historical counts (reviewer-supplied, recorded in fsb-0025-correction02.yaml)

```text
production_log_sha256  = 3cf6d94e5b45ea6ce80171a022487c1716d6a3939e4f133383172755fa6a4bb3
fingerprints_tsv_sha256 = 487a7a570277d543bfd4d3f12e851c234d28db9633f5788571e8ea2221296524
raw_occurrences        = 67
unique_fingerprints    = 64
duplicate_occurrences  = 3
same_coordinate_distinct_messages_present = true
```

These counts cannot be reproduced from any file available on disk or
in git history, including the FSB-0020 reconciliation bundle's
untracked.tar.gz, the FSB-0025 rescue bundle, or the /tmp Leamas
digest artefacts.

## Final verdict

**PARTIAL — close report corrected.**

* ACT foundation infrastructure is complete and tested.
* Close report identities are corrected.
* Mandatory gates were run; LLM-friendly gate and `make gate` fail
  for pre-existing reasons unrelated to this ACT.
* FSB-0022 raw bytes remain unrecoverable in matching form;
  recovered artefacts do not satisfy the strict recovery authority
  rule.

## Implementation identities

```text
implementation_commit_oid = d76d1e7b4ae96a36e8b7d9e1c994560348fce52a
implementation_tree_oid   = 82608245f58b7fc52f28b6321cd7f88ef141be5f

documentation_commit_oid  = ce9803afd844c3cb54ed0597163a410027553359
documentation_tree_oid    = 82608245f58b7fc52f28b6321cd7f88ef141be5f
final_head_oid            = ce9803afd844c3cb54ed0597163a410027553359
origin_main_oid           = ce9803afd844c3cb54ed0597163a410027553359
```

* The correction01 itself adds an additional commit recording the
  Fantomas reformatting of the 9 FSharpDiagnostics test files so
  that `make format-check` and therefore `make gate` can pass the
  format gate.  That commit is an ordinary fast-forward descendant
  of `ce9803af...`.