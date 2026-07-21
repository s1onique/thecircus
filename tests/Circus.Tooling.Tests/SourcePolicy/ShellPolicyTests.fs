module Circus.Tooling.Tests.SourcePolicy.ShellPolicyTests

open Expecto
open Circus.Tooling.SourcePolicy.ShellPolicy
open Circus.Tooling.SourcePolicy.Shebang
open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.Language

let private run text shebang = evaluate "test.sh" text shebang

[<Tests>]
let tests =
    testList
        "Shell policy"
        [ test "POSIX shell with 50 lines passes" {
              let text = System.String.Concat([| for _ in 1..50 -> "x\n" |])
              let findings = run text (ShebangPosixShell "")
              Expect.isFalse (List.exists (fun f -> f.Code = OversizedShell) findings) "no oversized"
          }
          test "POSIX shell with 51 lines fails oversized" {
              let text = System.String.Concat([| for _ in 1..51 -> "x\n" |])
              let findings = run text (ShebangPosixShell "")
              Expect.isTrue (List.exists (fun f -> f.Code = OversizedShell) findings) "oversized"
          }
          test "POSIX shell with bash-double-bracket fails" {
              let text = "#!/bin/sh\nif [[ -f /tmp/x ]]; then echo yes; fi\n"
              let findings = run text (ShebangPosixShell "")
              Expect.isTrue (List.exists (fun f -> f.Code = ShellContainsDomainLogic) findings) "domain logic"
          }
          test "POSIX shell with source keyword fails" {
              let text = "#!/bin/sh\n. /etc/profile\n"
              let findings = run text (ShebangPosixShell "")
              Expect.isTrue (List.exists (fun f -> f.Code = ShellContainsDomainLogic) findings) "domain logic"
          } ]
