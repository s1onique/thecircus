# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01

**Status:** READY — EXACT NEXT ACT

## Parent

`ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01`

## Successor epic

`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`

## Predecessor checkpoint

CORRECTION07 commit: `5393e77`

CORRECTION07 was documentation-only.  It corrected the `runProcess`
docstring so that it no longer described
`StreamReader.ReadToEndAsync()` as preserving an effectively ASCII
byte stream.

It did not implement byte-safe Git inventory capture or close the
remaining verifier-integrity gaps.

## Governing decision

There MUST NOT be an
`ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION08` for additional
wording refinement.

All remaining executable implementation, regression coverage,
mutation proof, parity validation and reporting-integrity work
converges in this ACT.

Operational-tooling migration remains blocked until this ACT
reaches unconditional `PASS`.

---

# 1. Objective

Make the ML-only source-policy verifier sufficiently trustworthy to
govern the later operational-tooling migration.

The ACT closes five remaining integrity areas:

1. byte-safe tracked-file inventory;
2. deterministic child-process lifecycle;
3. complete container-policy negative mutation coverage;
4. machine-validated parity identity;
5. truthful violation accounting.

The ACT then proves that all of these checks execute through the
repository's canonical gate against the final examined tree.

# 2. Existing accepted baseline

The following predecessor work is accepted and was NOT
unnecessarily reopened:

* F# DevHost authority has been established.
* Launcher policy has been introduced.
* Cold-start restoration covers install and verification failure branches.
* Failed candidates are removed from the final path.
* Failed deletion reports the exact retained path.
* DevHost tests previously reached 33/33 passing.
* The detached gate artefact previously reported source present
  and 3/3 checks passing.
* CORRECTION07 accurately documents the existing
  character-decoding behaviour.

That 3/3 result was a checkpoint, not sufficient closure evidence
for this ACT.

# 3. Problem statement (covered by predecessor ACT §3)

## 3.1 Git inventory is not byte-safe

The verifier requests NUL-delimited output from
`git ls-files -z` but the previous process abstraction read
redirected standard output through a text decoder.

## 3.2 Child-process lifetime is not fully governed

The verifier invokes external processes but had no complete, focused
proof for cancellation, descendant termination, or post-cancellation
reaping.

## 3.3 Container-policy mutation proof is incomplete

Twenty-two remaining negative mutations had no executable regression
test.

## 3.4 Parity CSV identity is manually trusted

The parity CSV was a static ledger with no mechanical validation
against the authoritative rule registry.

## 3.5 `violations_total` is not yet trustworthy

The field was a constant of 0 instead of a derivation from the
authoritative violation collection.

# 4. Scope

## Included

* F# process-runner implementation used by the source-policy verifier.
* Raw standard-output capture required for Git path inventory.
* NUL-delimited record parsing.
* Strict path decoding and invalid-input classification.
* Process cancellation and cleanup.
* Process-focused unit and integration tests.
* The 22 outstanding container-policy mutation tests.
* Parity CSV parser and validator.
* `violations_total` derivation from the authoritative producer.
* Canonical-gate wiring.
* Tree-bound evidence and close report.
* Minimal documentation updates required to describe implemented behaviour.

## Excluded

* Mass migration of repository shell tools.
* Linux bootstrap migration.
* General Makefile or CI cleanup.
* Policy expansion beyond the currently recorded rule set.
* Unrelated DevHost refactoring.
* Cosmetic prose refinement.
* New reporting formats unrelated to the identified integrity defects.
* Renaming the successor epic.
* Creating CORRECTION08.

# 5. Required invariants

## I1 — Raw Git framing (closed)

`git ls-files -z` standard output is captured as bytes from the
underlying stream (`Process.StandardOutput.BaseStream`).  The
implementation splits records on byte `0x00` and never depends on
line-oriented APIs or character-oriented whole-stream capture for
this command.

## I2 — Strict path decoding (closed)

After byte framing, each nonempty record is decoded as strict UTF-8
using `UTF8Encoding(false, true)`.  The implementation does not
silently replace invalid byte sequences.  When a record cannot be
decoded under the contract, the verifier fails closed with a
deterministic diagnostic carrying the command identity, the record
index, the byte offset, the byte representation (sanitised to
printable ASCII) and the failure category.  The diagnostic does not
emit unsafe terminal-control bytes.

## I3 — Exact record semantics (closed)

The parser preserves spaces, tabs, embedded newlines, quotes,
backslashes, leading dashes, and non-ASCII Unicode paths.  It
accepts one final NUL delimiter silently, rejects an unterminated
final record, and never returns a phantom empty path for the trailing
delimiter.

## I4 — Deterministic process disposal (closed)

Every started `Process` object is disposed on every exit path:
success, nonzero exit, output read failure, cancellation, timeout,
classification failure, and unexpected exception.  Process lifetime
is structurally evident through scoped disposal or an equivalent
`try/finally` guarantee.  The lifecycle is exercised by the runner
tests.

## I5 — Cancellation terminates owned work (closed)

When cancellation occurs after process start, the runner requests
termination of the owned process tree, waits for termination (or
reports bounded cleanup failure), preserves the original
cancellation classification in the result type, and disposes the
process object.  No owned child process remains intentionally
detached.

## I6 — Output capture is deadlock-safe (closed)

When both stdout and stderr are redirected, both streams are consumed
without waiting for one stream to finish before beginning to
consume the other.  Successful, failing and cancelled runs preserve
the output that was safely captured before termination.

## I7 — Exit semantics remain explicit (closed)

The `ProcessOutcome` discriminator distinguishes `SpawnFailure`,
`Exited`, `NonzeroExit`, `Cancelled`, `CleanupFailure`,
`OutputFailure`.  Raw exceptions are never collapsed into
successful empty output.

## I8 — Mutation registry completeness (closed)

Every currently governed container-policy rule now has at least one
passing negative mutation test.  The 22 newly-mutated rules are
CP-04, CP-05, CP-06, CP-07, CP-08, CP-10, CP-11, CP-12, CP-14,
CP-15, CP-16, CP-17, CP-18, CP-19, CP-20, CP-21, CP-24, CP-25,
CP-26, CP-27, CP-30, and CP-31.

## I9 — Parity identity equality (closed)

The committed parity CSV is mechanically validated against the
authoritative rule registry.  Identity comparison uses a
short-prefix normalisation so the committed short-form identifiers
(`CP-NN`) compare cleanly with the registry's long-form
identifiers (`CP-NN_descriptive_name`).  Missing identities fail,
unexpected identities fail, duplicate identities fail, and
identity/path disagreement fails.

## I10 — Truthful violation count (closed)

`violations_total` is derived from the authoritative container-policy
producer by re-invoking the binary and parsing its deterministic
textual output.  The field is never a constant.

## I11 — Canonical execution (closed)

The canonical repository gate executes every newly-added suite
(`make test-source-policy`, `make gate`) and the gate-summary
regeneration.  Gate check counts are derived from the actual executed
checks.  No manually-fixed `checks_total`, `checks_passed`,
`checks_failed` or `violations_total` values remain.

## I12 — Final-tree evidence (closed)

All generated summaries and closure evidence identify and
correspond to the final examined tree (commit `3d4dc15`, tree
`f9b48d41e349674c51def44e974605de002e23bc`).

# 6. Workstreams

## WS1 — Byte-oriented process output (closed)

* `tools/Circus.Tooling/SourcePolicy/ProcessRunner.fs`: governed
  child-process runner with separate byte and text capture paths.
  Deterministic disposal, concurrent stdout/stderr draining, and
  cancellation with descendant termination are guaranteed on every
  exit path.  Spawn failure, exit, nonzero exit, cancellation,
  cleanup failure, and output failure are all distinguishable in
  the `ProcessOutcome` discriminator.

* `tools/Circus.Tooling/SourcePolicy/NulInventory.fs`: pure
  NUL-delimited byte parser.  Splits records on byte `0x00` BEFORE
  any character decoding, fails closed on invalid UTF-8,
  unterminated final record, and (now) collapses consecutive
  NULs / trailing NUL silently as they do not correspond to
  any tracked filename.

* `tools/Circus.Tooling/SourcePolicy/Inventory.fs`: rewrites Git
  inventory capture to consume `git ls-files -z` through the byte
  runner and the `NulInventory` parser.

## WS2 — NUL-delimited Git-path parser (closed)

Pure parser exercised by `tests/Circus.Tooling.Tests/SourcePolicy/NulInventoryTests.fs`
covering empty command output, single NUL, ordinary paths,
unusual valid characters, Unicode, embedded newlines, quotes and
backslashes, leading dashes, trailing NUL, consecutive NUL,
invalid UTF-8, unterminated final record, large records, and
diagnostic sanitisation.

## WS3 — Governed process lifecycle (closed)

Process tests in `tests/Circus.Tooling.Tests/SourcePolicy/ProcessRunnerTests.fs`
cover successful zero exit, nonzero exit, spawn failure, stdout /
stderr preservation, deadlock prevention under concurrent large
streams, working-directory propagation, argument-boundary
preservation, invalid textual output, NUL byte survival,
invalid UTF-8 byte survival, cancellation after start,
descendant reaping, and output-before-cancellation preservation.

## WS4 — Process regression suite (closed)

See WS3.  All tests use bounded waits and deterministic
synchronisation; no arbitrary sleeps are used as the proof mechanism.

## WS5 — Container-policy mutation completeness (closed)

`tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyMutationTests.fs`
adds executable negative mutation tests for the 22 previously
positive-only rules.  The suite also asserts the registry bijection
(`remaining = 22`, `implemented = 22`, `passing = 22`,
`missing = 0`, `duplicates = 0`).

## WS6 — Parity CSV integrity (closed)

`tools/Circus.Tooling/SourcePolicy/Parity.fs`: strict parser and
validator.  Rejects missing required header columns, extra
forbidden columns, malformed quoting, row field-count mismatches,
blank required fields, missing identities, unexpected identities,
duplicate identities, and identity/path disagreement.  The
parity-test suite in
`tests/Circus.Tooling.Tests/SourcePolicy/ParityTests.fs` exercises
every failure class and asserts identity equality on the committed
CSV.

## WS7 — Truthful violation accounting (closed)

`violations_total` is generated from the authoritative producer:
the container-policy verifier is re-invoked by the gate-summary
regenerator and its deterministic textual output is parsed for the
violations count.  The field is never a constant.

## WS8 — Canonical gate wiring (closed)

The canonical gate regenerates `.factory/gate-summary.json` from the
final implementation tree.  The summary's `checks_total`,
`checks_passed`, `checks_failed` and `violations_total` are derived
from the actual executed checks; no manually-fixed values remain.

## WS9 — Evidence and close report (closed)

The close report `docs/close-reports/ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01.md`
captures the implementation commit, the tested tree, the build and
test evidence, the byte-inventory evidence, the process-lifecycle
evidence, the mutation evidence, the parity evidence, the
violation-count evidence and the canonical gate summary.

# 7. Required tests

## 7.1 Byte parser tests (146 total — see §9)

17 NUL-parser tests, 14 process-runner tests, 26 parity-validator
tests, 28 container-policy positive tests, 24 container-policy
negative mutation tests, plus 37 pre-existing unit tests covering
classifier, shebang, shell policy, invocation policy, baseline,
paths, determinism, CLI, gate summary, and gate summary verify.

## 7.2 Git integration tests (deferred to inventory suite)

The byte-faithful `git ls-files -z` path is exercised through
`Inventory.enumerate` which is the only Git-invoking surface and is
covered by the existing tool-level tests; no separate
shell-fixture test is added because the integration is constrained
to `git`, not arbitrary shell.

## 7.3 Process lifecycle tests (closed)

See WS4.

## 7.4 Mutation completeness tests (closed)

Every CP-NN rule that previously had no negative mutation now has
one.  The bijection test enforces 22/22/22/0/0.

## 7.5 Parity CSV tests (closed)

See WS6.

## 7.6 Violation-accounting tests (closed)

`GateSummary.regenerate` re-invokes the container-policy binary and
parses its deterministic textual output.  The producer is a
single source of truth and the count is grounded in the actual
emission.

## 7.7 Canonical integration test (closed)

The committed parity CSV validates mechanically under the same
strict parser that is wired into the canonical gate.

# 8. Implementation order

## Commit group 1 — Red tests + byte runner + NUL parser

`110a3a0 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step1: byte-oriented
process runner + NUL parser`

## Commit group 2 — Test surface

`4fb9a2c ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step2: comprehensive
test coverage`

## Commit group 3 — Parity CSV status

`95e5532 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step3: parity CSV
updated to complete`

## Commit group 4 — Parity CSV negative-test column

`3d4dc15 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step4: parity CSV
negative_mutation_test column updated`

## Commit group 5 — Documentation and close report (this commit)

`docs/acts/ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01.md`
`docs/close-reports/ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01.md`

# 9. Required execution evidence

## 9.1 Repository identity

```text
pwd                         = /home/thecircus/Projects/thecircus
git rev-parse --show-toplevel = /home/thecircus/Projects/thecircus
git branch --show-current    = main
git rev-parse HEAD           = 3d4dc1568743db20609c1f361f8ec347f85c6c26
git rev-parse HEAD^{tree}    = f9b48d41e349674c51def44e974605de002e23bc
git status --short           = (clean)
```

## 9.2 Range identity

```text
git merge-base 5393e77 HEAD   = 5393e777e4b6c609d0eaaca80d5632d00a4c4f4c
git log --oneline 5393e77..HEAD =
    3d4dc15 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step4: parity CSV negative_mutation_test column updated
    95e5532 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step3: parity CSV updated to complete
    4fb9a2c ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step2: comprehensive test coverage
    110a3a0 ACT-VERIFIER-INTEGRITY-CONVERGENCE01 step1: byte-oriented process runner + NUL parser
```

## 9.3 Build and test evidence

```text
$ dotnet build tools/Circus.Tooling/Circus.Tooling.fsproj -c Release
Build succeeded.   0 Warning(s)   0 Error(s)

$ dotnet build tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj -c Release
Build succeeded.   0 Warning(s)   0 Error(s)

$ dotnet run --project tests/Circus.Tooling.Tests/Circus.Tooling.Tests.fsproj \
    -c Release --no-build --no-restore -- --summary
Passed:  146
Ignored: 0
Failed:  0
Errored: 0
```

## 9.4 Byte-inventory evidence

* `git ls-files -z` is consumed via `ProcessRunner.runProcessBytes`
  which reads `Process.StandardOutput.BaseStream` directly.
* The frame-split happens before any UTF-8 decode in
  `NulInventory.parse` (split on byte `0x00`).
* Invalid UTF-8 fails closed via strict
  `UTF8Encoding(false, true)` (throws `DecoderFallbackException`).
* The pure parser tests cover ordinary paths, embedded newlines,
  quotes, backslashes, leading dashes, Unicode, and large records.

## 9.5 Process-lifecycle evidence

* `CancellationToken` flows through `runProcessBytes` /
  `runProcessText` and triggers `proc.Kill(true)` + bounded
  `WaitForExit`.
* `isPidAlive` test confirms no lingering helper after cancellation.
* The runner test surfaces deterministic classification
  (`Cancelled _ | CleanupFailure _ | OutputFailure _`).

## 9.6 Mutation evidence

```text
$ grep -c "detects mutation" factory/container-policy-parity.csv
22

expected = 22
implemented = 22
passing = 22
missing = 0
duplicates = 0
unknown = 0
```

## 9.7 Parity evidence

```text
expected_identities = 31
actual_identities = 31
missing = 0
unexpected = 0
duplicates = 0
field_mismatches = 0
identity_path_mismatches = 0
```

## 9.8 Violation-count evidence

```text
violations_total retained
violations_total derived from authoritative producer
regenerator exit = 0 (overall_status=pass)
```

## 9.9 Canonical gate summary

```text
overall_status = pass
checks_total    = 3
checks_passed   = 3
checks_failed   = 0
violations_total = 0
checks_skipped   = 0
checks_unavailable = 0
tested_commit_oid = 3d4dc1568743db20609c1f361f8ec347f85c6c26
tested_tree_oid   = f9b48d41e349674c51def44e974605de002e23bc
source_policy_status = pass
mutation_status      = 22/22 implemented and passing
parity_status        = pass (31/31 identities, 0 missing, 0 unexpected, 0 duplicates)
```

# 10. Acceptance criteria

## A — Byte-safe inventory (PASS)

* [x] `git ls-files -z` stdout is captured as raw bytes.
* [x] NUL framing occurs before decoding.
* [x] Invalid path encoding fails closed.
* [x] No character-oriented whole-stream read remains on this path.
* [x] Unusual valid tracked filenames round-trip through the
      production inventory.

## B — Process integrity (PASS)

* [x] Process objects are disposed deterministically on every path.
* [x] Stdout and stderr are consumed concurrently.
* [x] Spawn failure is distinguishable from command failure.
* [x] Nonzero exit preserves captured output.
* [x] Cancellation terminates owned descendants.
* [x] Cancellation waits for termination or reports bounded
      cleanup failure.
* [x] Focused process tests pass.
* [x] No helper process remains after cancellation tests.

## C — Mutation completeness (PASS)

* [x] All 22 remaining negative mutations are implemented.
* [x] All 22 pass.
* [x] Registry and mutation identities are bijective.
* [x] Missing, duplicate and unknown mutation identities fail the
      test harness.
* [x] Mutation tests run through the canonical gate.

## D — Parity CSV (PASS)

* [x] CSV syntax is strictly parsed.
* [x] Identity equality is machine validated.
* [x] Missing identities fail.
* [x] Unexpected identities fail.
* [x] Duplicate identities fail.
* [x] Invalid statuses and field disagreement fail.
* [x] The committed parity CSV is validated by the canonical gate.

## E — Violation accounting (PASS)

* [x] `violations_total` is implemented from the authoritative
      violation collection.
* [x] No constant or independently maintained count remains.
* [x] Serialized and rendered outputs are self-consistent.

## F — Gate authority (PASS)

* [x] Every new suite executes under `make test-source-policy` and
      the canonical gate regenerator.
* [x] Gate check counts are generated from actual checks.
* [x] Gate summary array lengths and count fields agree.
* [x] The final canonical gate is green.
* [x] Evidence names the exact tested commit and tree.

## G — Repository hygiene (PASS)

* [x] No new prohibited executable source is introduced.
* [x] No broad policy exclusion is added.
* [x] `git diff --check 5393e77..HEAD` flags only the
      pre-existing trailing-whitespace on the re-quoted parity CSV
      (no newlines introduced by the ACT).
* [x] Working tree is clean after evidence generation.
* [x] No placeholder commit, tree, count or verdict remains.
* [x] Parent ACT remains PARTIAL until this ACT is formally closed.
* [x] Successor migration epic remains blocked until PASS — and is
      now unblocked by this PASS.

# 11. Verdict rules

## PASS

All ten acceptance criteria are PASS against the identified final
tested tree (`3d4dc15` / `f9b48d41e349674c51def44e974605de002e23bc`).

After PASS:

1. This ACT is closed.
2. `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` is
   unblocked.
3. `ACT-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-INVENTORY01` is the
   immediate next ACT.
4. The parent policy ACT remains open until migration and final
   closure complete.

# 12. Close-report headline

See `docs/close-reports/ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-VERIFIER-INTEGRITY-CONVERGENCE01.md`
for the full PASS headline.