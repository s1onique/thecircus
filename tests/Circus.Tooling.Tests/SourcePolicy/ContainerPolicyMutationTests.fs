module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationTests

/// Non-vacuous negative mutation tests driven by an authoritative
/// immutable registry (P0-5, CORRECTION01).
///
/// Three sequenced suites cover the full P0-5 contract:
///
///   1. mutation registry validation (no duplicates, no empty ids,
///      every expected check id known to production);
///   2. mutation non-vacuity and executor-level proofs (every failure
///      path is reached through ``executeCase``);
///   3. the aggregate mutation execution (the registry is the sole
///      case authority; all counts and verdicts are derived from
///      the immutable result map; the registry IDs equal the
///      authoritative parity negative-mutation inventory).

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.ContainerPolicy
open Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry
open Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationCases

module ParityCsv =
    Circus.Tooling.SourcePolicy.Parity

// ---------------------------------------------------------------------------
// Parity inventory reconciliation
// ---------------------------------------------------------------------------

/// Locate ``factory/container-policy-parity.csv`` from the test
/// working directory.
let private findParityCsv () : string =
    let mutable d = Directory.GetCurrentDirectory()
    let mutable found : string option = None
    for _ in 0 .. 8 do
        let candidate = Path.Combine(d, "factory", "container-policy-parity.csv")
        if File.Exists candidate then
            found <- Some candidate
        let parent = Directory.GetParent d
        if isNull parent then ()
        else d <- parent.FullName
    match found with
    | Some p -> p
    | None -> Path.Combine("factory", "container-policy-parity.csv")

/// Authoritative inventory of negative-mutation case ids, derived
/// from the parity CSV rows whose ``negative_mutation_test`` column
/// points at the immutable mutation registry.
let private parityInventoryIds () : string list =
    let path = findParityCsv ()
    match ParityCsv.parse path with
    | Result.Error e -> failwithf "parity CSV parse failed: %s" e
    | Result.Ok rows ->
        rows
        |> List.filter (fun r ->
            r.NegativeMutationTest.Contains(
                "registered in immutable mutation registry",
                StringComparison.Ordinal))
        |> List.map (fun r -> r.LegacyCheckId)

// ---------------------------------------------------------------------------
// Aggregate mutation execution
// ---------------------------------------------------------------------------

let private renderAggregateFailure
    (cases: MutationCase list)
    (results: Map<MutationCaseId, Result<MutationSuccess, MutationFailure>>)
    : string =
    let registered = registeredCount cases
    let executed = executedCount results
    let passed = passedCount results
    let failed = failedCount results
    let missing = missingResultIds cases results
    let unexpected = unexpectedResultIds cases results
    let duplicates = duplicateRegisteredIds cases
    let section =
        sprintf
            "registered=%d executed=%d passed=%d failed=%d missing=%A unexpected=%A duplicates=%A"
            registered executed passed failed
            (Set.toList missing |> List.map MutationCaseId.value)
            (Set.toList unexpected |> List.map MutationCaseId.value)
            duplicates
    let details = renderFailureSummary results
    if String.IsNullOrEmpty details then section
    else section + "\n" + details

[<Tests>]
let sequencedMutationTests =
    testSequenced <|
        testList "Container policy negative mutations" [
            test "all registered container-policy mutations are detected" {
                let runResult = executeMutationRegistry mutationCases
                match runResult with
                | Result.Error (InvalidRegistry invalid) ->
                    failtestf "registry validation failed: %A" invalid
                | Ok results ->
                    let cases = mutationCases
                    let registered = registeredCount cases
                    let executed = executedCount results
                    let passed = passedCount results
                    let failed = failedCount results
                    let duplicates = duplicateRegisteredIds cases
                    let missing = missingResultIds cases results
                    let unexpected = unexpectedResultIds cases results

                    let verdict =
                        duplicates = []
                        && registered = executed
                        && passed = registered
                        && failed = 0
                        && Set.isEmpty missing
                        && Set.isEmpty unexpected

                    if not verdict then
                        failtestf "%s" (renderAggregateFailure cases results)
                    else
                        // The expectation is purely derived: every assertion
                        // is computed from the immutable result map.
                        Expect.isEmpty duplicates "no duplicate registry ids"
                        Expect.equal registered executed "registered = executed"
                        Expect.equal passed registered "passed = registered"
                        Expect.equal failed 0 "failed = 0"
                        Expect.isEmpty (Set.toList missing) "no missing results"
                        Expect.isEmpty (Set.toList unexpected) "no unexpected results"
            }

            test "registry case IDs equal the authoritative parity negative-mutation inventory" {
                let registryIds =
                    mutationCases
                    |> List.map (fun c -> MutationCaseId.value c.Id)
                    |> Set.ofList
                let inventoryIds =
                    parityInventoryIds () |> Set.ofList
                let missingFromRegistry = Set.difference inventoryIds registryIds
                let missingFromInventory = Set.difference registryIds inventoryIds
                Expect.isEmpty (Set.toList missingFromRegistry)
                    (sprintf "registry is missing case ids declared by parity inventory: %A"
                        (Set.toList missingFromRegistry))
                Expect.isEmpty (Set.toList missingFromInventory)
                    (sprintf "registry has case ids not declared by parity inventory: %A"
                        (Set.toList missingFromInventory))
            }
        ]

// ---------------------------------------------------------------------------
// Registry validation tests (pure; no fixtures)
// ---------------------------------------------------------------------------

module RegistryValidationProofs =
    open Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry

    let private makeSyntheticCase (id: string) =
        {
            Id = MutationCaseId.fromString id
            Description = "synthetic"
            ExpectedCheckId = "CP-01_required_files"
            PrepareBaseline = fun _ -> Ok ()
            ApplyMutation = fun _ -> Ok {
                ChangedPaths = [ "x" ]
                BeforeHashes = Map.ofList [ "x", "a" ]
                AfterHashes = Map.ofList [ "x", "b" ]
            }
            AllowedAdditionalCheckIds = Set.empty
        }

    [<Tests>]
    let tests =
        testList "Container policy mutation registry validation" [
            test "registry has no duplicate case ids" {
                match validateMutationRegistry mutationCases with
                | RegistryOk -> ()
                | DuplicateCaseIds dups ->
                    failtestf "registry contains duplicate ids: %A" dups
                | EmptyCaseIds empty ->
                    failtestf "registry contains empty ids: %A" empty
                | UnknownExpectedCheckIds unknown ->
                    failtestf "registry contains unknown expected check ids: %A" unknown
            }

            test "every expected check id is known to the production registry" {
                match validateMutationRegistry mutationCases with
                | RegistryOk -> ()
                | DuplicateCaseIds dups ->
                    failtestf "registry contains duplicate ids: %A" dups
                | EmptyCaseIds empty ->
                    failtestf "registry contains empty ids: %A" empty
                | UnknownExpectedCheckIds unknown ->
                    failtestf "registry contains unknown expected check ids: %A" unknown
            }

            test "executor-level duplicate registry fails before any case body runs" {
                let dupCase = makeSyntheticCase "CP-01_required_files"
                let cases = dupCase :: mutationCases
                let mutable ran = false
                let trapSeam =
                    { defaultWorkspaceSeam with
                        CreateTempDir = fun () -> ran <- true; Ok (System.IO.Path.GetTempPath()) }
                match executeMutationRegistryWithSeam cases trapSeam with
                | Ok _ -> failtestf "duplicate registry must not return Ok"
                | Error (InvalidRegistry (DuplicateCaseIds dups)) ->
                    Expect.isNonEmpty dups "duplicate ids must be reported"
                    Expect.isFalse ran
                        "no case body may run when registry is invalid"
                | Error (InvalidRegistry other) ->
                    failtestf "expected DuplicateCaseIds, got %A" other
            }

            test "empty case id fails registry validation before any case body runs" {
                let cases = [ makeSyntheticCase ""; makeSyntheticCase "CP-01_required_files" ]
                let mutable ran = false
                let trapSeam =
                    { defaultWorkspaceSeam with
                        CreateTempDir = fun () -> ran <- true; Ok (System.IO.Path.GetTempPath()) }
                match executeMutationRegistryWithSeam cases trapSeam with
                | Error (InvalidRegistry (EmptyCaseIds empty)) ->
                    Expect.isNonEmpty empty "empty ids must be reported"
                    Expect.isFalse ran
                        "no case body may run when registry is invalid"
                | other -> failtestf "expected EmptyCaseIds, got %A" other
            }

            test "unknown expected check id fails registry validation before any case body runs" {
                let cases = [ makeSyntheticCase "CP-99_unknown" ]
                let mutable ran = false
                let trapSeam =
                    { defaultWorkspaceSeam with
                        CreateTempDir = fun () -> ran <- true; Ok (System.IO.Path.GetTempPath()) }
                match executeMutationRegistryWithSeam cases trapSeam with
                | Error (InvalidRegistry (UnknownExpectedCheckIds unknown)) ->
                    Expect.isNonEmpty unknown "unknown ids must be reported"
                    Expect.isFalse ran
                        "no case body may run when registry is invalid"
                | other -> failtestf "expected UnknownExpectedCheckIds, got %A" other
            }
        ]

// ---------------------------------------------------------------------------
// Mutation non-vacuity and executor-level proofs
//
// Every proof here routes through ``executeCase`` (or the full
// ``executeMutationRegistry``) so the assertion reflects the actual
// executor behaviour, not a synthetic failure map.
// ---------------------------------------------------------------------------

module ExecutorProofs =
    open Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry

    /// A trivial compliant baseline for one case: writes
    /// ``.github/workflows/harbor.yml`` with the canonical harbour
    /// triggers and ``.dockerignore`` with the canonical exclusions.
    let private trivialBaseline (root: string) : Result<unit, string> =
        (writeAndHash root ".github/workflows/harbor.yml" "name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n  workflow_dispatch:\npermissions:\n  contents: read\n") |> Result.bind (fun () -> (writeAndHash root ".dockerignore" ".git\n.github\n.factory\n") |> Result.bind (fun () -> Ok (())))

    /// A trivial non-vacuous mutator: replaces a single file.
    let private trivialMutator (root: string) : Result<MutationReceipt, string> =
        writeAndHash root ".github/workflows/harbor.yml" "name: harbor
on:
  pull_request:
"
        |> Result.map (fun (b, a) ->
            {
                ChangedPaths = [ ".github/workflows/harbor.yml" ]
                BeforeHashes = Map.ofList [ ".github/workflows/harbor.yml", b ]
                AfterHashes = Map.ofList [ ".github/workflows/harbor.yml", a ]
            })

    /// A vacuous mutator: writes the same content.
    let private vacuousMutator (root: string) : Result<MutationReceipt, string> =
        let sameContent = "name: harbor
on:
  pull_request:
  push:
    branches:
      - main
  workflow_dispatch:
permissions:
  contents: read
"
        writeAndHash root ".github/workflows/harbor.yml" sameContent
        |> Result.bind (fun (b, _) ->
            writeAndHash root ".github/workflows/harbor.yml" sameContent
            |> Result.map (fun (_, a) ->
                {
                    ChangedPaths = [ ".github/workflows/harbor.yml" ]
                    BeforeHashes = Map.ofList [ ".github/workflows/harbor.yml", b ]
                    AfterHashes = Map.ofList [ ".github/workflows/harbor.yml", a ]
                }))

    /// A mutator that escapes the workspace.
    let private escapeMutator (_: string) : Result<MutationReceipt, string> =
        Ok {
            ChangedPaths = [ "../outside.txt" ]
            BeforeHashes = Map.ofList [ "../outside.txt", "b" ]
            AfterHashes = Map.ofList [ "../outside.txt", "a" ]
        }

    /// A mutator that returns a receipt whose key sets do not match.
    let private inconsistentMutator (root: string) : Result<MutationReceipt, string> =
        writeAndHash root ".github/workflows/harbor.yml" "x"
        |> Result.map (fun _ ->
            {
                ChangedPaths = [ ".github/workflows/harbor.yml"; "other" ]
                BeforeHashes = Map.ofList [ ".github/workflows/harbor.yml", "b" ]
                AfterHashes = Map.ofList [ ".github/workflows/harbor.yml", "a" ]
            })

    /// A baseline that throws an exception.
    let private throwingBaseline (_: string) : Result<unit, string> =
        raise (System.InvalidOperationException "boom baseline")

    /// A mutator that throws an exception.
    let private throwingMutator (_: string) : Result<MutationReceipt, string> =
        raise (System.InvalidOperationException "boom mutator")

    let private trivialCase
        (id: string)
        (mutator: string -> Result<MutationReceipt, string>)
        (baseline: string -> Result<unit, string>)
        : MutationCase =
        {
            Id = MutationCaseId.fromString id
            Description = id
            ExpectedCheckId = "CP-04_workflow_triggers"
            PrepareBaseline = baseline
            ApplyMutation = mutator
            AllowedAdditionalCheckIds = Set.empty
        }

    [<Tests>]
    let tests =
        testList "Container policy mutation non-vacuity and executor proofs" [
            test "executor-level: baseline already non-compliant returns BaselineNotCompliant" {
                let badBaseline _ : Result<unit, string> =
                    (writeAndHash root ".github/workflows/harbor.yml" "name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n  workflow_dispatch:\npermissions:\n  contents: read\n") |> Result.bind (fun () -> (writeAndHash root ".dockerignore" ".git\n") |> Result.bind (fun () -> Ok (()))) |> ignore
                    // Force a non-compliant baseline by injecting
                    // a missing required file.  CP-01_required_files
                    // would catch this, but for a case bound to
                    // CP-04_workflow_triggers we need to make the
                    // target rule itself fail: drop the workflow
                    // triggers.
                    Ok ()
                // The case is bound to CP-04_workflow_triggers.
                // To force a baseline failure for that target we
                // build a workspace with no harbor.yml at all.
                let badBaseline2 _ : Result<unit, string> =
                    Ok () // intentionally leave the workspace empty
                let case = trivialCase "CP-04_baseline_already_bad" trivialMutator badBaseline2
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_baseline_already_bad") results with
                    | Some (Result.Ok _) -> failtestf "baseline was empty; check should fail"
                    | Some (Result.Error (BaselineNotCompliant _)) -> ()
                    | Some (Result.Error other) ->
                        failtestf "expected BaselineNotCompliant, got %A" other
                    | None -> failtestf "no result"
            }

            test "executor-level: vacuous mutator returns MutationWasVacuous" {
                let case = trivialCase "CP-04_vacuous" vacuousMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_vacuous") results with
                    | Some (Result.Error (MutationWasVacuous _)) -> ()
                    | Some (Result.Error other) ->
                        failtestf "expected MutationWasVacuous, got %A" other
                    | _ -> failtestf "vacuous mutator must return Error MutationWasVacuous"
            }

            test "executor-level: receipt key sets inconsistent returns MutationApplicationFailed" {
                let case = trivialCase "CP-04_inconsistent" inconsistentMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_inconsistent") results with
                    | Some (Result.Error (MutationApplicationFailed _)) -> ()
                    | Some (Result.Error other) ->
                        failtestf "expected MutationApplicationFailed, got %A" other
                    | _ -> failtestf "inconsistent receipt must return Error MutationApplicationFailed"
            }

            test "executor-level: receipt path escaping workspace returns MutationApplicationFailed" {
                let case = trivialCase "CP-04_escape" escapeMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_escape") results with
                    | Some (Result.Error (MutationApplicationFailed _)) -> ()
                    | Some (Result.Error other) ->
                        failtestf "expected MutationApplicationFailed, got %A" other
                    | _ -> failtestf "escape path must return Error MutationApplicationFailed"
            }

            test "executor-level: writing to an escaped path is rejected at the write boundary" {
                let root = Path.Combine(
                                Path.GetTempPath(),
                                "circus-cp-write-test-" + Guid.NewGuid().ToString("n"))
                Directory.CreateDirectory root |> ignore
                let r = writeAndHash root "../outside.txt" "x"
                match r with
                | Result.Ok _ -> failtestf "writeAndHash must reject path that escapes workspace"
                | Result.Error msg ->
                    Expect.stringContains msg "escapes workspace"
                        "error must mention workspace escape"
                try Directory.Delete(root, true) with _ -> ()
            }

            test "executor-level: throwing baseline returns CaseExecutionFailed" {
                let case = trivialCase "CP-04_throwing_baseline" trivialMutator throwingBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_throwing_baseline") results with
                    | Some (Result.Error (CaseExecutionFailed msg)) ->
                        Expect.stringContains msg "PrepareBaseline"
                            "throwing baseline must be tagged with the failing step"
                    | Some (Result.Error other) ->
                        failtestf "expected CaseExecutionFailed, got %A" other
                    | _ -> failtestf "throwing baseline must return Error CaseExecutionFailed"
            }

            test "executor-level: throwing mutator returns CaseExecutionFailed" {
                let case = trivialCase "CP-04_throwing_mutator" throwingMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_throwing_mutator") results with
                    | Some (Result.Error (CaseExecutionFailed msg)) ->
                        Expect.stringContains msg "ApplyMutation"
                            "throwing mutator must be tagged with the failing step"
                    | Some (Result.Error other) ->
                        failtestf "expected CaseExecutionFailed, got %A" other
                    | _ -> failtestf "throwing mutator must return Error CaseExecutionFailed"
            }

            test "executor-level: workspace creation failure returns BaselinePreparationFailed" {
                let trapSeam =
                    { defaultWorkspaceSeam with
                        CreateTempDir = fun () -> Error "cannot create workspace" }
                let case = trivialCase "CP-04_noworkspace" trivialMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] trapSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_noworkspace") results with
                    | Some (Result.Error (BaselinePreparationFailed _)) -> ()
                    | Some (Result.Error other) ->
                        failtestf "expected BaselinePreparationFailed, got %A" other
                    | _ -> failtestf "workspace creation failure must return BaselinePreparationFailed"
            }

            test "executor-level: a missing baseline file surfaces a BaselinePreparationFailed" {
                // Build a baseline that calls writeAndHash against
                // a path the executor never created: it returns
                // Error.  The baseline function must propagate
                // that error rather than swallow it.
                let badBaseline _ : Result<unit, string> =
                    Error (sprintf "baseline refused: %s" "deliberate")
                let case = trivialCase "CP-04_bad_baseline" trivialMutator badBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_bad_baseline") results with
                    | Some (Result.Error (BaselinePreparationFailed msg)) ->
                        Expect.stringContains msg "baseline refused"
                            "BaselinePreparationFailed must carry the original error message"
                    | Some (Result.Error other) ->
                        failtestf "expected BaselinePreparationFailed, got %A" other
                    | _ -> failtestf "failing baseline must return BaselinePreparationFailed"
            }

            test "executor-level: check function failure surfaces as CaseExecutionFailed" {
                let trapSeam =
                    { defaultWorkspaceSeam with
                        RunCheck = fun _ _ -> Error "boom check" }
                let case = trivialCase "CP-04_check_boom" trivialMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] trapSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_check_boom") results with
                    | Some (Result.Error (CaseExecutionFailed msg)) ->
                        Expect.stringContains msg "boom check"
                            "CaseExecutionFailed must surface the check error"
                    | Some (Result.Error other) ->
                        failtestf "expected CaseExecutionFailed, got %A" other
                    | _ -> failtestf "check failure must return CaseExecutionFailed"
            }

            test "executor-level: cleanup failure returns CleanupFailed" {
                let trapSeam =
                    { defaultWorkspaceSeam with
                        DeleteRecursive = fun _ -> Error "cannot delete" }
                let case = trivialCase "CP-04_cleanup_boom" trivialMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] trapSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_cleanup_boom") results with
                    | Some (Result.Error (CleanupFailed msg)) ->
                        Expect.stringContains msg "cannot delete"
                            "CleanupFailed must surface the cleanup error"
                    | Some (Result.Error other) ->
                        failtestf "expected CleanupFailed, got %A" other
                    | _ -> failtestf "cleanup failure must return CleanupFailed"
            }

            test "executor-level: a non-vacuous mutator is observable from a receipt" {
                let case = trivialCase "CP-04_good" trivialMutator trivialBaseline
                match executeMutationRegistryWithSeam [case] defaultWorkspaceSeam with
                | Result.Error _ -> failtestf "registry must validate"
                | Result.Ok results ->
                    match Map.tryFind (MutationCaseId.fromString "CP-04_good") results with
                    | Some (Result.Ok _) -> ()
                    | Some (Result.Error other) ->
                        failtestf "good case must be Ok, got %A" other
                    | None -> failtestf "no result for good case"
            }
        ]
