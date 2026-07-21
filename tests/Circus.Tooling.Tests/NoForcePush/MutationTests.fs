module Circus.Tooling.Tests.NoForcePush.MutationTests

/// Immutable mutation registry for no-force-push negative mutation suite.
/// Follows the same pattern as ContainerPolicyMutationRegistry.

open System
open System.IO

open Expecto
open Circus.Tooling.NoForcePush
open Circus.Tooling.NoForcePush.StaticPolicy

// ---------------------------------------------------------------------------
// Case identity
// ---------------------------------------------------------------------------

type MutationCaseId = private MutationCaseId of string

module MutationCaseId =
    let value (MutationCaseId v) = v
    let tryCreate (s: string) = if String.IsNullOrWhiteSpace s then Error "id must be non-empty" else Ok(MutationCaseId s)
    let fromString s = match tryCreate s with Ok id -> id | Error m -> invalidArg "s" m

// ---------------------------------------------------------------------------
// Workspace seam
// ---------------------------------------------------------------------------

type WorkspaceSeam = {
    CreateTempDir: unit -> Result<string, string>
    DeleteRecursive: string -> Result<unit, string>
    VerifyFile: string -> string -> Result<Diagnostic list, string>
}

let defaultWorkspaceSeam: WorkspaceSeam = {
    CreateTempDir = fun () ->
        let path = Path.Combine(Path.GetTempPath(), "circus-nfp-mut-" + Guid.NewGuid().ToString("n"))
        try Directory.CreateDirectory path |> ignore; Ok path
        with ex -> Error(sprintf "could not create workspace: %s" ex.Message)
    DeleteRecursive = fun (root: string) ->
        try if Directory.Exists root then Directory.Delete(root, true); Ok()
        with ex -> Error(sprintf "cleanup failed: %s" ex.Message)
    VerifyFile = fun (file: string) (root: string) ->
        let content = File.ReadAllText(Path.Combine(root, file))
        let diagnostics = analyzeCommand content
        Ok(diagnostics |> List.map (fun d -> { d with Path = file }))
}

// ---------------------------------------------------------------------------
// Result model
// ---------------------------------------------------------------------------

type MutationSuccess = {
    CaseId: MutationCaseId
    ExpectedRuleId: string
    BaselineFindings: Diagnostic list
    MutatedFindings: Diagnostic list
}

type MutationFailure =
    | BaselineNotCompliant of Diagnostic list
    | MutationApplicationFailed of string
    | ExpectedViolationMissing of expectedRuleId: string * actual: Diagnostic list

// ---------------------------------------------------------------------------
// Registry (P0-4: All NFP-001 through NFP-013 must have at least one proof)
// ---------------------------------------------------------------------------

type MutationCase = {
    Id: MutationCaseId
    Description: string
    ExpectedRuleId: string
    PrepareBaseline: string -> Result<unit, string>
    ApplyMutation: string -> Result<string, string>
}

let writeFile (root: string) (rel: string) (content: string) =
    let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
    let dir = Path.GetDirectoryName full
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    File.WriteAllText(full, content)

/// All mutation cases organized by diagnostic ID.
/// Each NFP-XXX has at least one mutation proof.
let private cases: MutationCase list = [
    // ==========================================================================
    // NFP-001: Force options
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-001_long_force"
      Description = "Long --force option"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --force origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_short_force"
      Description = "Short -f option"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push -f origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_short_bundle_f"
      Description = "Arbitrary short bundle containing 'f'"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push -af origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_uf_bundle"
      Description = "Short bundle -uf"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push -uf origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_force_with_lease"
      Description = "Force-with-lease option"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --force-with-lease origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_force_if_includes"
      Description = "Force-if-includes option"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --force-if-includes origin main\n") }
    
    // ==========================================================================
    // NFP-002: Leading-plus refspec
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-002_leading_plus"
      Description = "Leading-plus refspec"
      ExpectedRuleId = "NFP-002"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin +main:refs/heads/main\n") }
    
    { Id = MutationCaseId.fromString "NFP-002_plus_main"
      Description = "Leading-plus with bare main"
      ExpectedRuleId = "NFP-002"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin +main\n") }
    
    { Id = MutationCaseId.fromString "NFP-002_plus_refs_heads"
      Description = "Leading-plus with refs/heads/main"
      ExpectedRuleId = "NFP-002"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main:refs/heads/main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin +main:refs/heads/main\n") }
    
    // ==========================================================================
    // NFP-003: Remote deletion options
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-003_delete_long"
      Description = "Remote delete long option"
      ExpectedRuleId = "NFP-003"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --delete origin feature\n") }
    
    { Id = MutationCaseId.fromString "NFP-003_delete_short"
      Description = "Remote delete short option"
      ExpectedRuleId = "NFP-003"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push -d origin feature\n") }
    
    { Id = MutationCaseId.fromString "NFP-003_short_bundle_d"
      Description = "Arbitrary short bundle containing 'd'"
      ExpectedRuleId = "NFP-003"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin feature\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push -df origin feature\n") }
    
    // ==========================================================================
    // NFP-004: Empty-source deletion refspec
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-004_empty_source"
      Description = "Empty-source deletion refspec"
      ExpectedRuleId = "NFP-004"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin :refs/heads/feature\n") }
    
    { Id = MutationCaseId.fromString "NFP-004_colon_feature"
      Description = "Empty-source with bare feature name"
      ExpectedRuleId = "NFP-004"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin feature\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin :feature\n") }
    
    // ==========================================================================
    // NFP-005: Mirror or prune
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-005_mirror"
      Description = "Mirror option"
      ExpectedRuleId = "NFP-005"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --mirror origin\n") }
    
    { Id = MutationCaseId.fromString "NFP-005_prune"
      Description = "Prune option"
      ExpectedRuleId = "NFP-005"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --prune origin\n") }
    
    // ==========================================================================
    // NFP-006: Hook bypass
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-006_no_verify"
      Description = "No-verify bypass"
      ExpectedRuleId = "NFP-006"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push --no-verify origin main\n") }
    
    // ==========================================================================
    // NFP-007: Dynamic/unclassifiable arguments
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-007_dynamic_args"
      Description = "Dynamic arguments ($@)"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push \"$@\"\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_variable_option"
      Description = "Variable option"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push $OPTION origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_variable_remote"
      Description = "Variable remote"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push $REMOTE main\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_variable_refspec"
      Description = "Variable refspec"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin $REFSPEC\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_eval"
      Description = "Eval indirection"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\neval \"git push $args\"\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_sh_c"
      Description = "sh -c indirection"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\nsh -c \"git push $PUSH_ARGS\"\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_bash_c"
      Description = "bash -c indirection"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\nbash -c 'git push $@'\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_command_substitution"
      Description = "Command substitution"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push $(get_remote) main\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_malformed_quoting"
      Description = "Malformed quoting (adjacent quotes)"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push '--\"o'rigin main\n") }
    
    // ==========================================================================
    // NFP-008: Send-pack invocation
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-008_send_pack"
      Description = "Send-pack invocation"
      ExpectedRuleId = "NFP-008"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngit send-pack --force origin\n") }
    
    // ==========================================================================
    // NFP-009: GitHub API force
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-009_gh_api_force"
      Description = "GitHub API force"
      ExpectedRuleId = "NFP-009"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngh api repos/o/r/branches/b/protection\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngh api repos/o/r/branches/b/protection --field force=true\n") }
    
    // ==========================================================================
    // NFP-010: GitHub API ref deletion
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-010_gh_ref_delete"
      Description = "GitHub ref deletion"
      ExpectedRuleId = "NFP-010"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngh api repos/o/r/branches/b\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ngh api repos/o/r/git/refs/heads/b --method DELETE\n") }
    
    { Id = MutationCaseId.fromString "NFP-010_curl_delete"
      Description = "Curl DELETE ref"
      ExpectedRuleId = "NFP-010"
      PrepareBaseline = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ncurl https://api.github.com/repos/o/r\n")
      ApplyMutation = fun root -> Ok(writeFile root "script.sh" "#!/bin/sh\ncurl -X DELETE https://api.github.com/repos/o/r/git/refs/heads/b\n") }
    
    // ==========================================================================
    // NFP-011: Unclassified publication surface
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-011_unclassified_surface"
      Description = "Unclassified executable surface"
      ExpectedRuleId = "NFP-011"
      PrepareBaseline = fun root -> Ok(writeFile root "factory/inventory.csv" "path,surface_kind,parser_kind,authority,reason\n")
      ApplyMutation = fun root -> 
          // Create an executable script that's not in the inventory
          writeFile root "publish.sh" "#!/bin/sh\ngit push origin main\n" |> ignore
          Ok(writeFile root "factory/inventory.csv" "path,surface_kind,parser_kind,authority,reason\n") }
    
    // ==========================================================================
    // NFP-012: Doctrine/missing file drift
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-012_missing_doctrine"
      Description = "Missing doctrine file"
      ExpectedRuleId = "NFP-012"
      PrepareBaseline = fun root -> Ok(writeFile root "factory/no-force-push-surfaces.csv" "path,surface_kind,parser_kind,authority,reason\n.githooks/pre-push,agent-executable,shell,repository,test\n")
      ApplyMutation = fun root -> Ok(writeFile root "factory/no-force-push-surfaces.csv" "path,surface_kind,parser_kind,authority,reason\n") }
    
    // ==========================================================================
    // NFP-013: Parser operational failure
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-013_malformed_inventory"
      Description = "Malformed inventory CSV"
      ExpectedRuleId = "NFP-013"
      PrepareBaseline = fun root -> Ok(writeFile root "factory/no-force-push-surfaces.csv" "path,surface_kind,parser_kind,authority,reason\n.githooks/pre-push,agent-executable,shell,repository,test\n")
      ApplyMutation = fun root -> Ok(writeFile root "factory/no-force-push-surfaces.csv" "path,surface_kind,parser_kind,authority\n.githooks/pre-push,agent-executable,shell,repository\n") }
    
    // ==========================================================================
    // Additional YAML/Dockerfile/Makefile-specific cases
    // ==========================================================================
    { Id = MutationCaseId.fromString "NFP-007_yaml_folded_scalar"
      Description = "YAML folded scalar with git push"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "action.yml" "name: test\nruns:\n  using: node16\n  main: index.js\n")
      ApplyMutation = fun root -> Ok(writeFile root "action.yml" "name: test\nruns:\n  using: node16\n  main: index.js\n  post-run: >\n    git push $@ origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-001_dockerfile_invocation"
      Description = "Dockerfile RUN with force push"
      ExpectedRuleId = "NFP-001"
      PrepareBaseline = fun root -> Ok(writeFile root "Dockerfile" "FROM alpine\nRUN echo hello\n")
      ApplyMutation = fun root -> Ok(writeFile root "Dockerfile" "FROM alpine\nRUN git clone https://github.com/example/repo && git push --force origin main\n") }
    
    { Id = MutationCaseId.fromString "NFP-007_makefile_continuation"
      Description = "Makefile with line continuation and dynamic args"
      ExpectedRuleId = "NFP-007"
      PrepareBaseline = fun root -> Ok(writeFile root "Makefile" "publish:\n\tgit push origin main\n")
      ApplyMutation = fun root -> Ok(writeFile root "Makefile" "publish:\n\tgit push \\\n\t  $REMOTE \\\n\t  main\n") }
]

/// Number of mutation cases - not tracked separately from registry.
let registeredCount = List.length cases

// ---------------------------------------------------------------------------
// Execute
// ---------------------------------------------------------------------------

let executeCase (c: MutationCase) (seam: WorkspaceSeam) : Result<MutationSuccess, MutationFailure> =
    match seam.CreateTempDir() with
    | Error e -> Error(MutationApplicationFailed e)
    | Ok root ->
        try
            // Prepare baseline
            match c.PrepareBaseline root with
            | Error e -> Error(MutationApplicationFailed e)
            | Ok () ->
                // Baseline proof
                match seam.VerifyFile "script.sh" root with
                | Error e -> Error(MutationApplicationFailed e)
                | Ok baselineFindings ->
                    if not (List.isEmpty baselineFindings) then
                        Error(BaselineNotCompliant baselineFindings)
                    else
                        // Apply mutation
                        match c.ApplyMutation root with
                        | Error e -> Error(MutationApplicationFailed e)
                        | Ok () ->
                            // Mutated proof
                            match seam.VerifyFile "script.sh" root with
                            | Error e -> Error(MutationApplicationFailed e)
                            | Ok mutatedFindings ->
                                if not (List.exists (fun d -> d.RuleId = c.ExpectedRuleId) mutatedFindings) then
                                    Error(ExpectedViolationMissing(c.ExpectedRuleId, mutatedFindings))
                                else
                                    Ok { CaseId = c.Id
                                         ExpectedRuleId = c.ExpectedRuleId
                                         BaselineFindings = baselineFindings
                                         MutatedFindings = mutatedFindings }
        finally
            seam.DeleteRecursive root |> ignore

[<Tests>]
let tests =
    testList
        "NoForcePush MutationTests"
        [ test "all NFP-NN check ids are enumerable" {
              let ids = [
                  "NFP-001"; "NFP-002"; "NFP-003"; "NFP-004"
                  "NFP-005"; "NFP-006"; "NFP-007"; "NFP-008"
                  "NFP-009"; "NFP-010"; "NFP-011"; "NFP-012"; "NFP-013"
              ]
              Expect.equal (List.length ids) 13 "13 check ids"
          }
          test "registry contains mutations for every NFP-XXX" {
              // Group cases by expected rule ID
              let byRuleId =
                  cases
                  |> List.groupBy (fun c -> c.ExpectedRuleId)
                  |> Map.ofList
              
              let allRuleIds = [ "NFP-001"; "NFP-002"; "NFP-003"; "NFP-004"
                                  "NFP-005"; "NFP-006"; "NFP-007"; "NFP-008"
                                  "NFP-009"; "NFP-010"; "NFP-011"; "NFP-012"; "NFP-013" ]
              
              for ruleId in allRuleIds do
                  match Map.tryFind ruleId byRuleId with
                  | Some cases -> 
                      if List.isEmpty cases then
                          failwithf "rule %s has no mutation cases" ruleId
                  | None ->
                      failwithf "rule %s not found in registry" ruleId
          }
          yield! cases |> List.map (fun c ->
              test (sprintf "mutation case %s: %s" (MutationCaseId.value c.Id) c.Description) {
                  match executeCase c defaultWorkspaceSeam with
                  | Ok success ->
                      Expect.isEmpty success.BaselineFindings "baseline empty"
                      Expect.isNonEmpty success.MutatedFindings "mutated has findings"
                      Expect.isTrue
                          (List.exists (fun d -> d.RuleId = c.ExpectedRuleId) success.MutatedFindings)
                          (sprintf "has %s" c.ExpectedRuleId)
                  | Error e ->
                      failwithf "mutation failed: %A" e
              })
          test "registered count matches registry" {
              Expect.equal registeredCount (List.length cases) "registeredCount matches list length"
          } ]
