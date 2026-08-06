module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateCliFailureTests

// =============================================================================
// Rule Candidate CLI Failure Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Eight tests covering CLI exit codes and error reporting for the
// rule-candidate commands.
// =============================================================================

open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Cli
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private jsonlRel = ruleCandidatesJsonlRelativePath
let private summaryRel = ruleCandidatesSummaryRelativePath

let private withTempRepo (f: string -> 'a) : 'a =
    use repo = new TempRepository()
    f repo.Root

[<Tests>]
let cliFailureTests =
    testList
        "FSharpDiagnostics.RuleCandidates.CliFailure"
        [ test "inventory with missing corpus returns nonzero exit code" {
              use repo = new TempRepository()
              let code = runInventory repo.Root
              Expect.notEqual code ExitCode.pass "inventory with missing corpus must fail"
          }

          test "regenerate with unresolved reference returns nonzero exit code" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cli-unresolved"
              // Remove evidence so reference is unresolved
              repo.Delete(ruleCandidatesJsonlRelativePath)
              repo.Delete(summaryRel)
              repo.WriteUtf8(jsonlRel, "")
              // Remove evidence corpus to force unresolved reference
              let evidenceRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/verification-evidence-v1.jsonl"
              repo.Delete evidenceRel
              let code = runRegenerate repo.Root
              Expect.notEqual code ExitCode.pass "regenerate with unresolved reference must fail"
          }

          test "regenerate with zero candidates returns nonzero exit code" {
              use repo = new TempRepository()
              writeValidMinimalCorpus repo "cli-zero"
              // Wipe the transitions to produce zero candidates
              let transitionsRel = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/diagnostic-transitions-v1.jsonl"
              System.IO.File.WriteAllText(repo.Absolute transitionsRel, "")
              let code = runRegenerate repo.Root
              Expect.notEqual code ExitCode.pass "regenerate with zero candidates must fail"
          }

          test "verify with canonical mismatch returns nonzero exit code" {
              use repo = new TempRepository()
              // Create canonical outputs that won't match expected
              repo.WriteUtf8(jsonlRel, "{not json")
              repo.WriteUtf8(summaryRel, "{}")
              let code = runVerify repo.Root
              Expect.notEqual code ExitCode.pass "verify with malformed canonical must fail"
          }

          test "show with unknown candidate id returns nonzero exit code" {
              use repo = new TempRepository()
              let code = runShow repo.Root "nonexistent-id"
              Expect.notEqual code ExitCode.pass "show with unknown id must fail"
          }

          test "parse: unknown command yields HelpCmd (exit pass, no banner)" {
              let cmd = parse [ "unknown-command" ]
              match cmd with
              | HelpCmd -> ()
              | _ -> failwithf "expected HelpCmd, got %A" cmd
          }

          test "parse: missing show argument yields HelpCmd" {
              let cmd = parse [ "show" ]
              match cmd with
              | HelpCmd -> ()
              | _ -> failwithf "expected HelpCmd, got %A" cmd
          }

          test "ExitCode values are stable: pass=0, policyFailure=1, operationalError=2" {
              Expect.equal ExitCode.pass 0 "ExitCode.pass must be 0"
              Expect.equal ExitCode.policyFailure 1 "ExitCode.policyFailure must be 1"
              Expect.equal ExitCode.operationalError 2 "ExitCode.operationalError must be 2"
          } ]