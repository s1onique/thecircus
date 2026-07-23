module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.DeclarationTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.Declarations"
        [ test "parses valid declaration" {
              let json = "{\"schema_version\":\"repair-episode-declaration-v1\",\"episode_key\":\"ep-a\",\"before_capture_id\":\"before\",\"after_capture_id\":\"after\",\"before_commit_oid\":\"0000000000000000000000000000000000000000\",\"after_commit_oid\":\"1111111111111111111111111111111111111111\",\"verification_evidence_ids\":[\"ev-1\"],\"declared_relevant_paths\":[\"src/Foo.fs\"],\"notes\":null}"
              let v = parseDeclaration json (Some "test.json")
              match v.Declaration with
              | Some d ->
                  Expect.equal d.EpisodeKey "ep-a" "key"
                  Expect.equal d.BeforeCaptureId "before" "before cap"
                  Expect.equal d.AfterCaptureId "after" "after cap"
                  Expect.isEmpty v.Issues "no issues"
              | None -> failwithf "expected Some declaration, got None; issues=%A" v.Issues
          }
          test "rejects unknown schema_version" {
              let json = "{\"schema_version\":\"wrong\",\"episode_key\":\"ep\",\"before_capture_id\":\"a\",\"after_capture_id\":\"b\",\"before_commit_oid\":\"0000000000000000000000000000000000000000\",\"after_commit_oid\":\"1111111111111111111111111111111111111111\",\"verification_evidence_ids\":[\"ev\"],\"declared_relevant_paths\":[\"x\"]}"
              let v = parseDeclaration json None
              Expect.isNone v.Declaration "should fail"
              Expect.isTrue (List.exists (fun i -> match i with InvalidSchemaVersion -> true | _ -> false) v.Issues) "schema issue"
          }
          test "rejects unknown fields" {
              let json = "{\"schema_version\":\"repair-episode-declaration-v1\",\"episode_key\":\"ep\",\"before_capture_id\":\"a\",\"after_capture_id\":\"b\",\"before_commit_oid\":\"0000000000000000000000000000000000000000\",\"after_commit_oid\":\"1111111111111111111111111111111111111111\",\"verification_evidence_ids\":[\"ev\"],\"declared_relevant_paths\":[\"x\"],\"unknown_extra\":1}"
              let v = parseDeclaration json None
              Expect.isNone v.Declaration "should fail on unknown field"
              Expect.isTrue (List.exists (fun i -> match i with UnknownField _ -> true | _ -> false) v.Issues) "unknown field issue"
          }
          test "rejects absolute declared paths" {
              let json = "{\"schema_version\":\"repair-episode-declaration-v1\",\"episode_key\":\"ep\",\"before_capture_id\":\"a\",\"after_capture_id\":\"b\",\"before_commit_oid\":\"0000000000000000000000000000000000000000\",\"after_commit_oid\":\"1111111111111111111111111111111111111111\",\"verification_evidence_ids\":[\"ev\"],\"declared_relevant_paths\":[\"/abs/path\"]}"
              let v = parseDeclaration json None
              Expect.isNone v.Declaration "absolute path"
              Expect.isTrue (List.exists (fun i -> match i with AbsoluteDeclaredPath _ -> true | _ -> false) v.Issues) "abs path"
          }
          test "rejects malformed OIDs" {
              let json = "{\"schema_version\":\"repair-episode-declaration-v1\",\"episode_key\":\"ep\",\"before_capture_id\":\"a\",\"after_capture_id\":\"b\",\"before_commit_oid\":\"short\",\"after_commit_oid\":\"alsoshort\",\"verification_evidence_ids\":[\"ev\"],\"declared_relevant_paths\":[\"x\"]}"
              let v = parseDeclaration json None
              Expect.isNone v.Declaration "malformed OIDs"
              Expect.isTrue (List.exists (fun i -> match i with InvalidOidFormat _ -> true | _ -> false) v.Issues) "oid issue"
          }
          test "rejects missing required fields" {
              let json = "{\"schema_version\":\"repair-episode-declaration-v1\",\"episode_key\":\"ep\",\"before_capture_id\":\"a\",\"after_capture_id\":\"b\"}"
              let v = parseDeclaration json None
              Expect.isNone v.Declaration "missing fields"
              Expect.isTrue (List.exists (fun i -> match i with MissingField _ -> true | _ -> false) v.Issues) "missing field"
          } ]
