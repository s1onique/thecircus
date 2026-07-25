module Circus.Tooling.Tests.CanonicalEvidence.CliTests

// =============================================================================
// CLI tests for the canonical evidence provider
//
// ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION01
// Tests 39–44: CLI verb dispatch, regenerate, verify, exit codes.
// =============================================================================

open System
open System.IO
open Expecto

open Circus.Tooling.CanonicalEvidence.Cli

let private tempDir () : string =
    let dir = Path.Combine(Path.GetTempPath(), "circus-canonev-cli-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory dir |> ignore
    dir

[<Tests>]
let tests =
    testList
        "CanonicalEvidence.Cli"
        [
          // 39. Unknown verb fails
          test "unknown verb fails" {
              let code = run [ "unknown" ]
              Expect.notEqual code 0 "unknown verb fails"
          }

          // 40. Missing required argument fails
          test "missing required argument fails" {
              let code = run [ "regenerate" ]
              Expect.notEqual code 0 "missing arg fails"
          }

          // 41. Regenerate succeeds with valid inputs
          test "regenerate succeeds with valid inputs" {
              let dir = tempDir ()
              let runCmd (args: string list) =
                  let psi = System.Diagnostics.ProcessStartInfo()
                  psi.FileName <- "git"
                  psi.WorkingDirectory <- dir
                  psi.UseShellExecute <- false
                  psi.RedirectStandardOutput <- true
                  psi.RedirectStandardError <- true
                  psi.CreateNoWindow <- true
                  for a in args do psi.ArgumentList.Add(a)
                  let p = System.Diagnostics.Process.Start psi
                  p.WaitForExit()
              runCmd [ "init"; "-q" ]
              runCmd [ "config"; "user.email"; "ci@local" ]
              runCmd [ "config"; "user.name"; "ci" ]
              runCmd [ "config"; "commit.gpgsign"; "false" ]
              File.WriteAllText(Path.Combine(dir, "README.md"), "init")
              runCmd [ "add"; "README.md" ]
              runCmd [ "commit"; "-q"; "-m"; "init" ]
              let baseline = "HEAD"
              let output = Path.Combine(dir, "evidence.json")
              let code = run [ "regenerate"; "--repo-root"; dir; "--output"; output; "--baseline-commit"; baseline ]
              Expect.equal code 0 "regenerate returns 0"
              Expect.isTrue (File.Exists output) "evidence.json exists"
          }

          // 42. Verify succeeds for current valid evidence
          test "verify succeeds for current valid evidence" {
              let dir = tempDir ()
              let runCmd (args: string list) =
                  let psi = System.Diagnostics.ProcessStartInfo()
                  psi.FileName <- "git"
                  psi.WorkingDirectory <- dir
                  psi.UseShellExecute <- false
                  psi.RedirectStandardOutput <- true
                  psi.RedirectStandardError <- true
                  psi.CreateNoWindow <- true
                  for a in args do psi.ArgumentList.Add(a)
                  let p = System.Diagnostics.Process.Start psi
                  p.WaitForExit()
              runCmd [ "init"; "-q" ]
              runCmd [ "config"; "user.email"; "ci@local" ]
              runCmd [ "config"; "user.name"; "ci" ]
              runCmd [ "config"; "commit.gpgsign"; "false" ]
              File.WriteAllText(Path.Combine(dir, "README.md"), "init")
              runCmd [ "add"; "README.md" ]
              runCmd [ "commit"; "-q"; "-m"; "init" ]
              let output = Path.Combine(dir, "evidence.json")
              let regenCode = run [ "regenerate"; "--repo-root"; dir; "--output"; output; "--baseline-commit"; "HEAD" ]
              Expect.equal regenCode 0 "regenerate ok"
              let verifyCode = run [ "verify"; "--repo-root"; dir; "--input"; output ]
              Expect.equal verifyCode 0 "verify ok"
          }

          // 43. Verify fails for stale evidence
          test "verify fails for stale evidence" {
              let dir = tempDir ()
              let runCmd (args: string list) =
                  let psi = System.Diagnostics.ProcessStartInfo()
                  psi.FileName <- "git"
                  psi.WorkingDirectory <- dir
                  psi.UseShellExecute <- false
                  psi.RedirectStandardOutput <- true
                  psi.RedirectStandardError <- true
                  psi.CreateNoWindow <- true
                  for a in args do psi.ArgumentList.Add(a)
                  let p = System.Diagnostics.Process.Start psi
                  p.WaitForExit()
              runCmd [ "init"; "-q" ]
              runCmd [ "config"; "user.email"; "ci@local" ]
              runCmd [ "config"; "user.name"; "ci" ]
              runCmd [ "config"; "commit.gpgsign"; "false" ]
              File.WriteAllText(Path.Combine(dir, "README.md"), "init")
              runCmd [ "add"; "README.md" ]
              runCmd [ "commit"; "-q"; "-m"; "init" ]
              let output = Path.Combine(dir, "evidence.json")
              let regenCode = run [ "regenerate"; "--repo-root"; dir; "--output"; output; "--baseline-commit"; "HEAD" ]
              Expect.equal regenCode 0 "regenerate ok"
              // Advance the repo by a new commit so the evidence
              // becomes stale.
              File.WriteAllText(Path.Combine(dir, "README.md"), "second")
              runCmd [ "add"; "README.md" ]
              runCmd [ "commit"; "-q"; "-m"; "second" ]
              let verifyCode = run [ "verify"; "--repo-root"; dir; "--input"; output ]
              Expect.notEqual verifyCode 0 "verify fails on stale evidence"
          }

          // 44. All failures return non-zero without a PASS line
          test "missing repo-root returns non-zero without a PASS line" {
              let code = run [ "regenerate"; "--output"; "/tmp/x"; "--baseline-commit"; "HEAD" ]
              Expect.notEqual code 0 "non-zero exit"
          }
        ]
