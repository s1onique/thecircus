module Circus.Tooling.Tests.SourcePolicy.ShellPolicyTests

open Expecto
open Circus.Tooling.SourcePolicy.ShellPolicy
open Circus.Tooling.SourcePolicy.Shebang
open Circus.Tooling.SourcePolicy.Domain

let private run label text shebang =
    let cls : Classifier.Classification =
        { Path = "test.sh"
          Category = FileCategory.ShellScript
          Language = SourceLanguage.PosixShell
          Shebang = Shebang.tag shebang
          PhysicalLines = 0
          Sha256 = ""
          Executable = false }
    let findings = evaluate "test.sh" text shebang
    label, findings

let tests =
    testList "Shell policy" [
        test "POSIX shell with 50 lines passes" {
            let text = System.String.Concat([| for _ in 1 .. 50 -> "#\n" |]) |> (fun s -> s.Replace('#', 'x'))
            let _, findings = run "50-line" text ShebangPosixShell
            Expect.isFalse (List.exists (fun f -> f.Code = OversizedShell) findings) "no oversized"
        }
        test "POSIX shell with 51 lines fails oversized" {
            let text = System.String.Concat([| for _ in 1 .. 51 -> "x\n" |])
            let _, findings = run "51-line" text ShebangPosixShell
            Expect.isTrue (List.exists (fun f -> f.Code = OversizedShell) findings) "oversized"
        }
        test "POSIX shell with bash-double-bracket fails" {
            let text = "#!/bin/sh\nif [[ -f /tmp/x ]]; then echo yes; fi\n"
            let _, findings = run "bash-test" text ShebangPosixShell
            Expect.isTrue (List.exists (fun f -> f.Code = ShellContainsDomainLogic) findings) "domain logic"
        }
        test "POSIX shell with source keyword fails" {
            let text = "#!/bin/sh\n. /etc/profile\n"
            let _, findings = run "source-test" text ShebangPosixShell
            Expect.isTrue (List.exists (fun f -> f.Code = ShellContainsDomainLogic) findings) "domain logic"
        }
    ]
