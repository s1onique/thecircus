# ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION02

## Status

**READY — P0**

## Parent

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01` (closed PARTIAL)

`ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01-CORRECTION01`

## Parent epic

`EPIC-CIRCUS-FSHARP-DIAGNOSTIC-KNOWLEDGE-AND-HISTORY-SAFETY01`

## Purpose

This ACT resolves the structural recursion problem in the previous
close report: a commit OID cannot be embedded inside its own commit
because the OID depends on the tree containing the report.  The
fix is to use an annotated tag — a separate Git object created
**after** the final commit — to carry the closure binding.

It also fixes the two new long-line LLM-friendly violations in
`tools/Circus.Tooling/FSharpDiagnostics/Cli.fs` lines 267 and 280.

## Closure binding protocol

```text
1. Implementation, tests, and code corrections are committed via
   ordinary fast-forward pushes only.
2. The close report does NOT claim its own commit OID.  It records
   only historical identities already in git history.
3. After the final code commit exists, an annotated tag is created
   targeting it.  The tag message carries the final identities and
   verdict.  Git revisions like `<tag>^{commit}` and
   `<tag>:<path>` then resolve the target commit and the close-report
   blob without recursion.
```

## Tagged closure

```text
closure_binding_kind = annotated_tag_v1
closure_tag_name     = act/circus-fsharp-diagnostic-corpus-foundation01-partial-v1
```

The tag message includes:

```text
act_id
verdict=PARTIAL
target_commit_oid
target_tree_oid
close_report_path
close_report_blob_oid
implementation_commit_oid
implementation_tree_oid
tested_commit_oid
tested_tree_oid
focused_tests=pass
focused_gate=pass
llm_friendly_gate=fail
canonical_gate=fail
fsb_0022_acceptance=fail
force_update=false
```

The tag is pushed without `--force`.

## Code corrections in this ACT

* `tools/Circus.Tooling/FSharpDiagnostics/Cli.fs` lines 267 and 280
  contained strings longer than 240 characters.  The strings were
  split across multiple line continuations.  Both long-line LLM-
  friendly violations introduced by `FOUNDATION01` are now removed.
* `docs/close-reports/ACT-CIRCUS-FSHARP-DIAGNOSTIC-CORPUS-FOUNDATION01.md`
  is rewritten to:
  - record `closure_binding_kind = annotated_tag_v1`
  - record `closure_tag_name`
  - cite historical commit OIDs only
  - explicitly state that the final HEAD and tag OID are produced
    after the report is committed

## Verification (already executed in this ACT)

| Check                                | Result |
|--------------------------------------|--------|
| `dotnet build tools/Circus.Tooling`   | pass   |
| `make gate-fsharp-diagnostics`       | PASS (verdict: pass) |
| FSharpDiagnostics lines in LLM-friendly gate | pass (no long lines) |

## Final verdict

**PARTIAL** with the closure binding now non-recursive.  The
foundation infrastructure, focused gate, and FSharpDiagnostics
LLM-friendly surface all pass.  FSB-0022 acceptance, the repository-
wide LLM-friendly gate, and the canonical `make gate` remain failed
for pre-existing reasons.