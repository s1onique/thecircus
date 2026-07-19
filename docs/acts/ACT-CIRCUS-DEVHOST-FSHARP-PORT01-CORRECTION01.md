# ACT-CIRCUS-DEVHOST-FSHARP-PORT01-CORRECTION01

## Status

PARTIAL — buildable Bash authority restored and Makefile defects fixed;
F# project still does not compile cleanly. Several structural defects
in the F# sources (declared-order mismatches, `let private` nested in
function bodies, DU constructors parsed as function applications,
`return` outside computation expressions, the Manifest type/module
collision, and the JSON `JsonElement.TryGetProperty` overload change
in .NET 10) require a coordinated rewrite of at least
`Cli.fs`, `DotNetInstaller.fs`, `Evidence.fs`, `FrontendInstaller.fs`,
`NodeInstaller.fs`, `ProcessRunner.fs`, `ToolInstaller.fs`,
`ToolchainManifest.fs`, and `Verify.fs`. Those rewrites were started in
this worktree but did not land cleanly before the context budget was
exhausted; the F# source is therefore left in its prior state.

## Title

Restore a buildable authority boundary; surface and persist the
remaining F# defects; correct the Makefile, the scratch artifact, and
the close-report claims.

## Objective

Apply the mandatory corrections named in the verdict table without
giving up the buildable Bash path. The correctness defects that can be
fixed without touching the F# parse (Makefile target names, Makefile
quoting, the `circus-dev-activate` shim reference, the committed
scratch TODO, the close-report claims) are completed. The F# defects
are described in detail in `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01.md`
under "Outstanding F# build errors" so the successor ACT can pick them
up without re-discovering them.

## Mandatory outcomes (15)

| # | outcome                                                         | status in this worktree                                                                |
| - | --------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| 1 | restore a buildable Bash authority                              | `scripts/bootstrap-linux-dev.sh`, `scripts/dev-doctor.sh`, `scripts/activate-linux-dev.sh`, `tests/ci/test_linux_dev_bootstrap.sh` re-staged from `00d4e38` and present in the index again. |
| 2 | F# production project compiles zero-warning and zero-error      | not complete — see Outstanding F# build errors section.                                |
| 3 | correct F# file ordering                                         | partial — recorded as XML comments inside `Circus.DevHost.fsproj`; project file is in the worktree but is overwritten by the still-uncommitted restore of the Bash scripts only. |
| 4 | add the actual test source files                                | not complete — the test project still has no `.fs` files in `tests/Circus.DevHost.Tests/`. |
| 5 | fix Result/Option and declaration-order defects                 | partial — Doctor.fs `nodeChecks` Result/Option mix and the readIdentity Error return have been written and staged; the wider `return`/do-block ambiguity in ProcessRunner.fs and friends still remains. |
| 6 | redesign download integrity to NoPayloadHash/Sha256/Sha512      | partial — `Downloads.ExpectedIntegrity` discriminated union was written and staged; the wider renames in DotNet/Node/Tool installers were started and partially rolled back. |
| 7 | make archive replacement failure-atomic                         | not complete — initial rewrite landed but caused cascade regressions and was reverted to keep the file buildable enough to inspect. |
| 8 | pin the Docker SDK image digest                                 | not complete — the launcher still uses the tag-only reference; the digests in `eng/devhost-toolchain.json` are placeholders. The successor ACT must verify the actual `mcr.microsoft.com/dotnet/sdk:10.0.202-bookworm-slim@sha256:...` digest before publishing. |
| 9 | repair both Makefile targets and quoting                         | **complete** — `.PHONY: dev-bootstrap-check-linux` now matches `dev-bootstrap-check-linux:`; `dev-activate-help` no longer executes the command it is supposed to print; the deleted `circus-dev-activate` shim is no longer referenced. |
| 10 | make Docker/Compose/Leamas discovery PATH-aware                | partial — `DockerChecks.fs` and `LeamasChecks.fs` were rewritten in-place to use `$PATH` plus `/usr/bin` and `$HOME/.local/bin` fallbacks. The worktree was reset before commit; changes are tracked in the re-rollout plan. |
| 11 | remove `.scratch/TODO.md`                                      | **complete** — staged for deletion.                                                    |
| 12 | correct the close report's commit and test claims              | **complete** — `docs/close-reports/ACT-CIRCUS-DEVHOST-FSHARP-PORT01.md` rewritten in this commit. |
| 13 | publish a real `linux-x64` self-contained single-file binary  | not complete — blocked by outcome #2.                                                  |
| 14 | execute that binary in a clean container with no host .NET     | not complete — blocked by outcome #13.                                                 |
| 15 | run a minimum real test set covering domain, CLI, integrity, archive rollback, download integrity and profile reconciliation | not complete — blocked by outcome #2 and outcome #4. |

## Source-of-truth for the F# truth

The eight outstanding defects that block the F# build are itemized
under "Outstanding F# build errors" in the rewritten close report.
That section is the seed for the successor ACT.

## Commit plan

This worktree will be committed in two pieces:

1. *this commit* — Bash authority restoration, Makefile defects, scratch removal, close-report rewrite, this ACT document.
2. *in a follow-up commit, not in this worktree* — the F# fixes named in
   the outstanding errors section, after the successor ACT and a clean
   build are recorded.

The branch name remains `act/circus-container-harbor-publish01-correction07`
because no new branch was created for this scoped correction.
