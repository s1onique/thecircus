# ML-Only Source Policy

**Status:** ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01 — installed (this ACT).

## Purpose

The Circus repository enforces a strict ML-family implementation-language
policy for first-party executable code:

* **F#** is the canonical implementation language for backend, operational
  tooling, verification, and automation.
* **Elm** is the canonical implementation language for the browser
  application.
* **POSIX `sh`** is permitted **only** as a tightly constrained
  *stage-zero launcher* that locates an installed F# binary and `exec`s it.

Every other executable language is rejected at the verifier level and
may not be introduced into the repository without an explicit ACT.

> The precise claim is:
> *All first-party executable implementation logic must be written in an
> approved project language, currently F# or Elm, except for tightly
> constrained POSIX stage-zero launchers.*

The claim is intentionally narrow: we are not claiming that *every*
file in the repository is F# or Elm. Declarative artefacts (Markdown,
JSON, YAML, TOML, SQL, Dockerfile, Makefile, .fsproj, etc.) are not
implementation code.

## Approved languages

| Language | Recognised files                              |
| -------- | --------------------------------------------- |
| F#       | `*.fs`, `*.fsi`, `*.fsproj`                   |
| Elm      | `*.elm`                                       |

`*.fsx` is recognised only when placed under an explicitly designated
examples or experiments directory. A canonical gate, build, bootstrap,
publication, migration, or verification command must never depend on
`*.fsx`.

## Restricted shell

New or modified first-party shell files must:

* use the `.sh` extension;
* begin with `#!/bin/sh`;
* contain no more than **50 physical lines**;
* use LF line endings;
* end with a newline;
* pass `sh -n`;
* contain no Bash-only shebang or declared Bash dependency;
* contain no embedded source in another language.

The following are prohibited in non-grandfathered shell:

* `#!/bin/bash`, `#!/usr/bin/env bash`;
* Bash arrays, `[[ ... ]]`, `source`, `eval`;
* Heredoc-with-execution, embedded Python/JS/Ruby/Perl/Haskell/OCaml/Go;
* JSON, YAML, TOML, XML, SQL parsing;
* retry loops, package-version resolution;
* domain-operation shell functions.

## Verifier

The verifier is a compiled F# program at:

```text
tools/Circus.Tooling/
  Circus.Tooling.fsproj
  Program.fs
  SourcePolicy/
    Language.fs
    Domain.fs
    Paths.fs
    Inventory.fs
    LineCounting.fs
    Shebang.fs
    Classifier.fs
    ShellPolicy.fs
    InvocationPolicy.fs
    Baseline.fs
    Verification.fs
    JsonReport.fs
    HumanReport.fs
    Cli.fs
```

It walks the Git inventory (tracked and non-ignored untracked files),
classifies each policy-relevant file, evaluates shell semantics,
detects interpreter invocations, and applies the baseline ratchet.

Build and run:

```bash
make source-policy
```

Exit codes:

| Code | Meaning                                                      |
| ---: | ------------------------------------------------------------ |
|    0 | Policy passes (only baseline violations are tolerated).     |
|    1 | Policy violations exist.                                     |
|    2 | Operational, configuration, parsing, or repository failure. |

## Forbidden interpreter invocations

The verifier inspects `Makefile`, `*.mk`, `Dockerfile*`, `*.sh`,
`*.yml`, `*.yaml`, `go.mod`, `go.sum`, and `flake.nix` for invocations
of the following runtimes, which constitute a policy violation:

`python`, `python2`, `python3`, `pypy`, `pypy3`,
`go`, `node`, `deno`, `bun`, `ts-node`,
`ruby`, `perl`, `php`, `lua`,
`pwsh`, `powershell`,
`runhaskell`, `ghc`, `ocaml`.

Plain textual mentions in documentation are allowed.

## Baseline and ratchet

Existing violations of the `oversized_shell` kind are recorded in
`factory/source-policy-baseline.csv` with the exact SHA-256 digest and
physical-line count at install time. New violations are not permitted.
Python, Go, and unknown executable shebangs may not be grandfathered.

The CSV format is:

```csv
path,violation_kind,physical_lines,sha256,owner,successor_act,reason
```

A baseline row is **stale** when its path is no longer a violation, and
is an **expansion** when a tracked file is added without a corresponding
baseline entry. Both are failures under `source-policy verify`.

Successor ownership for all current rows is recorded as
`ACT-CIRCUS-BOOTSTRAP-LINUX-DEV-FSHARP-MIGRATION01`.

## How to run

```bash
# Build and run the verifier (human-readable output)
make source-policy

# JSON output for tooling
make source-policy-json

# Run the test project (added by this ACT)
make test-source-policy

# Inspect a single file
tools/Circus.Tooling/bin/Release/net10.0/linux-x64/circus-tooling source-policy explain scripts/circus-dev
```

## Exit-code meanings for CI integration

| Code | Meaning                                                       |
| ---: | ------------------------------------------------------------- |
|    0 | No findings. Only baseline violations are tolerated.            |
|    1 | Policy violation. CI must fail.                                |
|    2 | Operational error (Git unavailable, baseline malformed, etc.). |

## How to request an exception

You cannot. The policy is an allowlist. The only sanctioned path for
introducing a forbidden language is to file a new ACT that explicitly
removes or supersedes `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01`, and that
ACT must carry its own close-report.

## Successor ownership

The remaining `oversized_shell` violations recorded in
`factory/source-policy-baseline.csv` are owned by:

* `ACT-CIRCUS-BOOTSTRAP-LINUX-DEV-FSHARP-MIGRATION01`

That successor ACT extracts the bootstrap domain logic into compiled
F#, reduces each shell entry point to a ≤50-line POSIX launcher, and
removes the corresponding baseline row. This ACT does not claim that
migration is complete.

## Pointer for contributors

Before adding repository tooling, run:

```bash
make source-policy
```

If your change adds an `*.fs`, `*.fsi`, `*.fsproj`, or `*.elm` file, you
are conforming. If it adds a `.sh` file, keep it under 50 physical lines,
use `#!/bin/sh`, and avoid every pattern listed under **Restricted shell**
above. If it adds any other executable extension, the verifier will
reject your change before CI runs.

## Anti-evasion

The verifier is closed under the following evasion attempts:

* executable files with an unknown shebang — rejected as
  `unknown_executable_shebang`;
* extensionless files with a forbidden-interpreter shebang — rejected
  as `unknown_executable_shebang`;
* heredocs that pipe forbidden code into a shell — rejected as
  `shell_contains_domain_logic`;
* base64-decoded source executed at runtime — flagged by the
  interpreter-invocation check;
* generated shell that exceeds the 50-line limit — rejected as
  `oversized_shell`;
* symlinks that escape the repository root — rejected as
  `repository_boundary_escape`.

## Notes on vendored Elm tooling

`web/node_modules/` and `web/elm-stuff/` are excluded from the
inventory by `.gitignore`. The verifier never reads them.

## Pointer for operators

`factory/source-policy-baseline.csv` is the canonical, digest-bound
record of existing `oversized_shell` violations. The verifier rejects
any change to a baseline file that does not correspond to an observable
violation, and rejects any new tracked file that introduces a
violation without a baseline row. Operators should not edit the file
directly; updates must flow through the successor ACT.
