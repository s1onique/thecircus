module Circus.Tooling.Tests.SourcePolicy.DeterminismTests

open Expecto
open Circus.Tooling.SourcePolicy.Verification

let private cfg =
    { RepoRoot = "/repo"
      BaselinePath = "/repo/factory/source-policy-baseline.csv"
      RejectBaselineExpansion = true
      DetectUnknownExes = true }

let private cfg2 =
    { RepoRoot = "/repo2"
      BaselinePath = "/repo2/factory/source-policy-baseline.csv"
      RejectBaselineExpansion = true
      DetectUnknownExes = true }

[<Tests>]
let tests =
    testList
        "Determinism"
        [ test "default config is deterministic" {
              let a = defaultConfig "/repo"
              let b = defaultConfig "/repo"
              Expect.equal a.RepoRoot b.RepoRoot "repo"
              Expect.equal a.RejectBaselineExpansion b.RejectBaselineExpansion "expansion"
          }
          test "different configs have different baselines" {
              Expect.notEqual cfg.BaselinePath cfg2.BaselinePath "different paths"
          } ]
