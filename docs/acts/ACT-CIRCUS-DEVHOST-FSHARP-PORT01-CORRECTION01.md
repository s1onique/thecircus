# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01

## Status

**ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01 — PARTIAL**

PARTIAL — predecessor files and documentary recovery committed, but the
active Makefile still routes through the noncompiling F# implementation, the
restored Bash snapshot is not the corrected bootstrap revision, and several
close-report claims describe work absent from commit
`658195cb4dfd5d8c32406346e4019cfa4f52c8c5`.

Temporary Bash source restored, but full bootstrap correctness is not
re-certified; only explicitly executed commands are authoritative.

## Title

Recover the predecessor Bash snapshot, repair isolated Makefile text defects,
remove the scratch artifact, and record the remaining F# work accurately.

## Objective

Describe the tree committed by CORRECTION01 without treating attempted and
reset F# edits as delivered work. The commit physically restores the Bash
predecessor from `00d4e38`, fixes the check-target name and help-recipe quoting,
removes `.scratch/TODO.md`, and rewrites the close report. It does not route the
Make targets through Bash, produce a compiling F# authority, or re-certify the
known-defective predecessor snapshot.

## Mandatory outcomes (15)

| # | outcome | status in commit `658195c` |
| - | ------- | --------------------------- |
| 1 | restore a buildable Bash authority | **not complete** — the predecessor files are present, but the active Make targets still invoke `scripts/circus-dev`, which must publish the noncompiling F# project when no binary is installed. The restored `00d4e38` snapshot also retains known bootstrap defects. |
| 2 | F# production project compiles zero-warning and zero-error | not complete — successor implementation required. |
| 3 | correct F# file ordering | **not present in commit `658195c`; successor implementation required.** |
| 4 | add the actual test source files | not complete — the test project contains no `.fs` sources. |
| 5 | fix Result/Option and declaration-order defects | **not present in commit `658195c`; successor implementation required.** Any attempted edits were discarded exploration. |
| 6 | redesign download integrity to `NoPayloadHash`/`Sha256`/`Sha512` | **not present in commit `658195c`; successor implementation required.** Any attempted `ExpectedIntegrity` edit was reset. |
| 7 | make archive replacement failure-atomic | not complete — successor implementation and failure-injection tests required. |
| 8 | pin the Docker SDK image digest | not complete — the launcher uses a mutable tag and the manifest digest is an unverified placeholder. |
| 9 | repair both Makefile targets and quoting | **partial** — the check-target name and accidental command execution are fixed, but the targets still route through noncompiling F#, and the help text advertises unsupported `--shell auto`. |
| 10 | make Docker/Compose/Leamas discovery PATH-aware | **not present in commit `658195c`; successor implementation required.** Any attempted rewrites were reset. |
| 11 | remove `.scratch/TODO.md` | **complete**. |
| 12 | correct the close report's commit and test claims | **complete as documentary recovery**, with this factual correction removing claims about reset F# work. |
| 13 | publish a real `linux-x64` self-contained single-file binary | not complete — blocked by the F# build. |
| 14 | execute that binary in a clean container with no host .NET | not complete — blocked by publication. |
| 15 | run a minimum real test set covering Domain, CLI, Integrity, Downloads, Archives, and ShellProfile | not complete — no test sources are committed. |

## Completion accounting

Three documentary/recovery outcomes completed; twelve implementation and
verification outcomes remain. The three completed pieces are physical recovery
of the predecessor files, scratch-artifact removal, and factual documentary
recovery. Physical recovery is not the same as active or verified authority.

## F# build-error record

The close report records the compile blockers observed at this checkpoint.
Those notes are diagnostics, not evidence that fixes were written, staged, or
committed. In particular, file ordering, `Doctor.fs` `Result` handling,
`Repository.readIdentity`, `ExpectedIntegrity`, and PATH-aware discovery are
not present in commit `658195c`.

`JsonElement.TryGetProperty` is documented there as incorrect API usage/F#
byref interop. The official contract is `TryGetProperty(string, out
JsonElement)`; it was not a .NET 10 removal of a single-argument API.

## Commit record

CORRECTION01 is commit
`658195cb4dfd5d8c32406346e4019cfa4f52c8c5`, based on
`294e28d1b210372861dd09399a52fc5387676737`. A successor correction must land
all F# source, test, publication, digest, and final authority changes as new
work; none may be credited to `658195c`.
