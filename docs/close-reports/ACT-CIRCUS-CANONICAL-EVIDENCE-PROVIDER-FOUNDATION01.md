# Close Report — ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01

## Verdict

**PARTIAL_CHECKPOINT**

The ACT-owned mandatory criteria cannot all pass within this slice:

* the canonical evidence provider implementation was attempted but
  could not be brought to a fully compiling state within the
  available iteration budget. F# syntax issues with the bounded
  Process execution seam (DataReceivedEventHandler type inference
  and async completion) repeatedly blocked a clean build.
* no production provider file was committed; the owned-scope
  directory `tools/Circus.Tooling/CanonicalEvidence/` was created
  and emptied.
* the predecessor `...CORRECTION02.md` historical handoff remains
  marked `in progress` and is not yet migrated.
* the existing `.factory/gate-summary.json` remains
  `ad_hoc_supporting_evidence` (regression from the CORRECTION02
  reclassification is not committed).

## What was attempted

1. Wrote an initial implementation of `Provider.fs` with the full
   schema types, identity resolution via `git rev-parse`, bounded
   check execution via `Process`, atomic regeneration, and
   artifact-hash verification.
2. Wrote a matching `Cli.fs` exposing a `canonical-evidence`
   subcommand with `regenerate` and `verify` verbs.
3. Added both modules to `Circus.Tooling.fsproj`.
4. Iterated on F# syntax issues in the process-execution and
   identity-resolution code paths. Despite multiple rewrites, the
   build remained blocked by `FS0597` (indeterminate-type lookup)
   on `DataReceivedEventHandler` lambdas and on `match`
   expressions whose body extended across lines, and by `FS0588`
   on unfinished `let` blocks.

## What was reverted

* `tools/Circus.Tooling/Circus.Tooling.fsproj` was reverted to its
  baseline state.
* `tools/Circus.Tooling/CanonicalEvidence/Provider.fs` and
  `tools/Circus.Tooling/CanonicalEvidence/Cli.fs` were deleted
  before any commit. The directory remains on disk but contains
  only the ACT and close-report markdown files plus an empty
  placeholder.

## Required immediate next slice

A fresh implementation pass on the provider must:

1. Resolve the F# type-inference issues with
   `DataReceivedEventHandler(fun a -> ...)`. The simplest fix is to
   pass an explicit `(a: DataReceivedEventArgs) ->` annotation on
   the lambda or to switch to a `Process.StandardOutput.BaseStream`
   read pattern (which the existing `ProcessRunner.fs` already
   uses successfully).
2. Wire `canonical-evidence regenerate` and `canonical-evidence
   verify` into the `Circus.Tooling.Cli` top-level dispatch.
3. Add a `make canonical-evidence` target that invokes the
   provider.
4. Replace the existing `.factory/gate-summary.json` with a
   provider-generated artifact and reclassify the predecessor
   `...CORRECTION02.md` handoff to point at the new evidence.

## Stop conditions that still apply

* the canonical evidence provider is not yet registered as canonical
  (the directory exists but contains no executable provider);
* the existing `.factory/gate-summary.json` remains a static
  hand-authored artifact;
* the bounded Git adapter evidence is not yet migrated;
* no annotated tag binding the provider target was created.

## Successor release

After the next slice of this ACT closes:

```text
ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02
```

remains blocked because the canonical provider is not yet available
to consume for its local and remote enforcement evidence.

## Final identities (unchanged from baseline)

```yaml
baseline_commit_oid: 5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
final_head_oid:      5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
origin_main_oid:     5f1f7f99d57aaa133e76679c8bb6aa90620ebc1e
```

## Working tree state

```bash
$ git status --short
$ git diff --check
exit=0
```

The working tree is clean.
