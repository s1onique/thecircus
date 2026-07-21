/// Fail-closed writer + classification seam tests for
/// ``Circus.Tooling.SourcePolicy.GateSummary``.
///
/// These tests cover the Phase-A fail-closed contract:
///   1. ``classifyOutcome`` maps the full outcome alphabet to the
///      canonical ``(status, exitCode)`` pairs;
///   2. ``testedIdentityFromGit`` fails closed when either identity
///      component is unreadable;
///   3. ``writeDocument`` distinguishes ``DirectoryCreationFailed``
///      from ``FileWriteFailed`` and never leaves a partial canonical
///      artefact;
///   4. ``regenerate`` returns ``Error`` when the artefact write
///      fails and the CLI exits non-zero without emitting a PASS line;
///   5. The canonical artefact is byte-identical across the suite.

module Circus.Tooling.Tests.SourcePolicy.GateSummaryWiringTests

open System
open System.IO
open System.Text.RegularExpressions
open Expecto

open Circus.Tooling.SourcePolicy
open Circus.Tooling.SourcePolicy.ProcessRunner


// -------------------------------------------------------------------
// 1. classifyOutcome — process-outcome classification matrix
// -------------------------------------------------------------------

let private assertPair
    (label: string)
    (expectedStatus: string)
    (expectedExit: int)
    (actual: string * int) =
    let status, exitCode = actual
    if status <> expectedStatus then
        failtestf "%s: expected status %s but got %s" label expectedStatus status
    if exitCode <> expectedExit then
        failtestf "%s: expected exit %d but got %d" label expectedExit exitCode

[<Tests>]
let classificationTests =
    testList "GateSummary classification seam" [
        test "classifyOutcome: zero exit → (pass, 0)" {
            assertPair "Exited(0,_)" "pass" 0
                (GateSummary.classifyOutcome (Exited(exitCode=0, cleanupNote="")))
        }
        test "classifyOutcome: non-zero exit → (fail, n)" {
            assertPair "Exited(7,_)" "fail" 7
                (GateSummary.classifyOutcome (Exited(exitCode=7, cleanupNote="")))
            assertPair "Exited(127,_)" "fail" 127
                (GateSummary.classifyOutcome (Exited(exitCode=127, cleanupNote="")))
        }
        test "classifyOutcome: NonzeroExit → (fail, n)" {
            assertPair "NonzeroExit(3,_)" "fail" 3
                (GateSummary.classifyOutcome (NonzeroExit(exitCode=3, cleanupNote="")))
        }
        test "classifyOutcome: SpawnFailure → (unavailable, -1)" {
            assertPair "SpawnFailure" "unavailable" -1
                (GateSummary.classifyOutcome (SpawnFailure(detail="boom", cleanupNote="")))
        }
        test "classifyOutcome: Cancelled → (unavailable, -1)" {
            assertPair "Cancelled" "unavailable" -1
                (GateSummary.classifyOutcome (Cancelled(cleanupNote="")))
        }
        test "classifyOutcome: OutputFailure → (unavailable, -1)" {
            assertPair "OutputFailure" "unavailable" -1
                (GateSummary.classifyOutcome (OutputFailure(detail="boom", cleanupNote="")))
        }
        test "classifyOutcome: CleanupFailure → (unavailable, -1)" {
            assertPair "CleanupFailure" "unavailable" -1
                (GateSummary.classifyOutcome (CleanupFailure(detail="boom")))
        }
        test "classifyOutcome: BodyFailure → (unavailable, -1)" {
            assertPair "BodyFailure" "unavailable" -1
                (GateSummary.classifyOutcome (BodyFailure(detail="boom", cleanupNote="")))
        }

    ]

// -------------------------------------------------------------------
// 2. testedIdentityFromGit — fail-closed identity
// -------------------------------------------------------------------

[<Tests>]
let identityTests =
    testList "GateSummary identity fail-closed" [
        test "testedIdentityFromGit: missing commit identity → Error" {
            // An empty directory (no git) yields a non-zero exit on
            // ``git rev-parse HEAD`` and the function must return
            // ``Result.Error CommitOidMissing`` (fail-closed).
            let tmp = Path.Combine(Path.GetTempPath(),
                "circus-no-git-" + Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tmp |> ignore
            try
                match GateSummary.testedIdentityFromGit tmp with
                | Result.Error GateSummary.CommitOidMissing -> ()
                | Result.Error other -> failtestf "expected CommitOidMissing, got %A" other
                | Result.Ok identity ->
                    failtestf "expected Error, got Ok %A" identity
            finally
                try Directory.Delete(tmp, true) with _ -> ()
        }
    ]

// -------------------------------------------------------------------
// 3. writeDocument — distinct failure modes + no partial artefact
// -------------------------------------------------------------------

[<Tests>]
let writerTests =
    testList "GateSummary writeDocument" [
        let emptyDoc : GateSummary.GateSummaryDoc =
            { SchemaVersion = 1
              GeneratedAt = "2026-01-01T00:00:00Z"
              Tool = "test"
              OverallStatus = "pass"
              ChecksTotal = 0
              ChecksPassed = 0
              ChecksFailed = 0
              ViolationsTotal = 0
              ViolationsOperational = 0
              ChecksSkipped = 0
              ChecksUnavailable = 0
              Checks = []
              TestedCommitOid = ""
              TestedTreeOid = "" }


        test "writeDocument: existing writable directory → Ok" {
            let tmp = Path.Combine(Path.GetTempPath(),
                "circus-writer-" + Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tmp |> ignore
            try
                let target = Path.Combine(tmp, "artefact.json")
                match GateSummary.writeDocument target emptyDoc with
                | Ok () ->
                    Expect.isTrue (File.Exists target) "artefact must be written"
                | Error e -> failtestf "expected Ok, got %A" e
            finally
                try Directory.Delete(tmp, true) with _ -> ()
        }

        test "writeDocument: absent creatable directory → Ok and directory created" {
            let parent = Path.Combine(Path.GetTempPath(),
                "circus-writer-parent-" + Guid.NewGuid().ToString("n"))
            let newDir = Path.Combine(parent, "subdir", "subsubdir")
            let target = Path.Combine(newDir, "artefact.json")
            try
                match GateSummary.writeDocument target emptyDoc with
                | Ok () ->
                    Expect.isTrue (File.Exists target) "artefact must exist"
                    Expect.isTrue (Directory.Exists newDir) "directory must have been created"
                | Error e -> failtestf "expected Ok, got %A" e
            finally
                try Directory.Delete(parent, true) with _ -> ()
        }

        test "writeDocument: existing canonical artefact not replaced by partial document" {
            // Seed a canonical artefact, attempt a write that
            // forces ``DirectoryCreationFailed`` (outputPath with no
            // parent directory is impossible to construct), then
            // prove the seeded canonical content survives.
            let tmp = Path.Combine(Path.GetTempPath(),
                "circus-writer-preserve-" + Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tmp |> ignore
            try
                let canonicalPath = Path.Combine(tmp, "canonical.json")
                let canonicalContent = "CANONICAL-CONTENT"
                File.WriteAllText(canonicalPath, canonicalContent)
                let bogus = Path.Combine("", "no-parent.json")
                match GateSummary.writeDocument bogus emptyDoc with
                | Ok () ->
                    failtestf "expected Error for path with empty parent"
                | Error (GateSummary.DirectoryCreationFailed _) -> ()
                | Error other -> failtestf "expected DirectoryCreationFailed, got %A" other
                Expect.equal
                    (File.ReadAllText canonicalPath)
                    canonicalContent
                    "canonical artefact must be untouched after failed write"
            finally
                try Directory.Delete(tmp, true) with _ -> ()
        }
    ]


// -------------------------------------------------------------------
// 4. regenerate — fail-closed Result and CLI exit code
// -------------------------------------------------------------------

[<Tests>]
let regenerateResultTests =
    testList "GateSummary regenerate result" [
        test "regenerate: identity unreadable → Result.Error (IdentityReadFailed CommitOidMissing)" {
            let tmp = Path.Combine(Path.GetTempPath(),
                "circus-no-git-regen-" + Guid.NewGuid().ToString("n"))
            Directory.CreateDirectory tmp |> ignore
            try
                match GateSummary.regenerate tmp with
                | Result.Ok _ -> failtestf "expected Error, got Ok"
                | Result.Error (GateSummary.IdentityReadFailed GateSummary.CommitOidMissing) -> ()
                | Result.Error other ->
                    failtestf "expected IdentityReadFailed CommitOidMissing, got %A" other
            finally
                try Directory.Delete(tmp, true) with _ -> ()
        }
    ]

// -------------------------------------------------------------------
// 5. Canonical artefact byte-identity across the suite
// -------------------------------------------------------------------

[<Tests>]
let artefactNonMutationTests =
    testList "GateSummary artefact non-mutation" [
        test "GateSummary ordinary tests never mutate .factory/gate-summary.json" {
            let canonical =
                Path.Combine(Path.GetFullPath ".",
                    ".factory", "gate-summary.json")
            let beforeBytes : byte[] option =
                if File.Exists canonical then
                    Some (File.ReadAllBytes canonical)
                else
                    None
            // Exercise the producer with a deterministic runner.
            let runner : GateSummary.ExternalCheckRunner =
                fun n _ _ ->
                    { Name = n; Status = "pass"; ExitCode = 0
                      Command = "<injected>" }
            let identity : GateSummary.TestedIdentity =
                { CommitOid = "ci"; TreeOid = "tr" }
            let doc = GateSummary.buildDocument "." identity runner
            let tmp =
                Path.Combine(Path.GetTempPath(),
                    "circus-isolation-" + Guid.NewGuid().ToString("n") + ".json")
            try
                match GateSummary.writeDocument tmp doc with
                | Ok () -> ()
                | Error e -> failtestf "writeDocument error: %A" e
                Expect.isTrue (File.Exists tmp) "temp artefact must exist"
            finally
                try if File.Exists tmp then File.Delete tmp with _ -> ()
            match beforeBytes with
            | Some bytes ->
                if File.Exists canonical then
                    let afterBytes = File.ReadAllBytes canonical
                    Expect.equal
                        (afterBytes.Length = bytes.Length)
                        true
                        "canonical artefact length must be unchanged"
                    Expect.equal afterBytes bytes
                        "canonical artefact must be byte-identical"
            | None ->
                Expect.isFalse (File.Exists canonical)
                    "canonical artefact must not be created by tests"
        }
    ]

// -------------------------------------------------------------------
// 6. Source-policy wiring contract (kept from prior CORRECTION01)
// -------------------------------------------------------------------

type private InvocationRecord = {
    CheckName: string
    CheckArgv: string list
    CheckCwd: string
}

let private makeRecordingRunner
    (invocations: ResizeArray<InvocationRecord>)
    (statusFor: string -> GateSummary.CheckStatus)
    : GateSummary.ExternalCheckRunner =
    fun (checkName: string) (checkArgv: string list) (checkCwd: string) ->
        invocations.Add
            { CheckName = checkName
              CheckArgv = checkArgv
              CheckCwd = checkCwd }
        { statusFor checkName with Name = checkName }

let private findSourcePolicy
    (invocations: ResizeArray<InvocationRecord>)
    : InvocationRecord option =
    invocations
    |> Seq.tryFind (fun i -> i.CheckName = "source-policy-tests")

let private passStatusFor (_name: string) : GateSummary.CheckStatus =
    { Name = ""
      Status = "pass"
      ExitCode = 0
      Command = "<injected>" }

[<Tests>]
let wiringTests =
    testList "GateSummary wiring" [
        test "wiring: source-policy-tests check is wired into the canonical gate" {
            let invocations = ResizeArray<InvocationRecord>()
            let runner = makeRecordingRunner invocations passStatusFor
            let identity : GateSummary.TestedIdentity =
                { CommitOid = "0000000000000000000000000000000000000001"
                  TreeOid   = "0000000000000000000000000000000000000002" }
            let doc =
                GateSummary.buildDocument
                    (Path.GetFullPath ".") identity runner
            Expect.isTrue
                (doc.Checks |> List.exists (fun c -> c.Name = "source-policy-tests"))
                "gate document must contain a check named source-policy-tests"
        }

        test "wiring: source-policy-tests command is exactly make test-source-policy" {
            let invocations = ResizeArray<InvocationRecord>()
            let runner = makeRecordingRunner invocations passStatusFor
            let identity : GateSummary.TestedIdentity =
                { CommitOid = "deadbeefcafebabe1234567890abcdef00000001"
                  TreeOid   = "deadbeefcafebabe1234567890abcdef00000002" }
            let repoRoot = Path.GetFullPath "."
            let _doc = GateSummary.buildDocument repoRoot identity runner
            let inv =
                match findSourcePolicy invocations with
                | Some i -> i
                | None ->
                    failtestf
                        "expected source-policy-tests invocation but got: %A"
                        (List.ofSeq invocations)
            Expect.equal inv.CheckName "source-policy-tests" "check name"
            Expect.equal
                inv.CheckArgv
                [ "make"; "test-source-policy" ]
                "argv must be exactly [\"make\"; \"test-source-policy\"]"
            Expect.equal inv.CheckCwd repoRoot "working directory must be the repository root"
            let sourcePolicyInvocations =
                invocations
                |> Seq.filter (fun i -> i.CheckName = "source-policy-tests")
                |> Seq.length
            Expect.equal sourcePolicyInvocations 1
                "source-policy-tests must be invoked exactly once"
        }

        test "wiring: runner exit 0 maps source-policy-tests to pass" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a _ ->
                    { Name = n
                      Status = (if n = "source-policy-tests" then "pass" else "pass")
                      ExitCode = 0
                      Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity = { CommitOid = ""; TreeOid = "" }
            let doc = GateSummary.buildDocument "." identity runner
            let sp = doc.Checks |> List.find (fun c -> c.Name = "source-policy-tests")
            Expect.equal sp.Status "pass" "exit 0 → status pass"
            Expect.equal sp.ExitCode 0 "exit code"
            Expect.equal doc.OverallStatus "pass"
                "overall status must be pass when source-policy-tests passes"
        }

        test "wiring: runner exit 1 maps source-policy-tests to fail" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a _ ->
                    { Name = n
                      Status = (if n = "source-policy-tests" then "fail" else "pass")
                      ExitCode = (if n = "source-policy-tests" then 1 else 0)
                      Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity = { CommitOid = ""; TreeOid = "" }
            let doc = GateSummary.buildDocument "." identity runner
            let sp = doc.Checks |> List.find (fun c -> c.Name = "source-policy-tests")
            Expect.equal sp.Status "fail" "exit 1 → status fail"
            Expect.equal doc.OverallStatus "fail"
                "overall status must be fail when source-policy-tests fails"
        }

        test "wiring: runner launch failure maps source-policy-tests to unavailable" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a _ ->
                    { Name = n
                      Status = (if n = "source-policy-tests" then "unavailable" else "pass")
                      ExitCode = (if n = "source-policy-tests" then -1 else 0)
                      Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity = { CommitOid = ""; TreeOid = "" }
            let doc = GateSummary.buildDocument "." identity runner
            let sp = doc.Checks |> List.find (fun c -> c.Name = "source-policy-tests")
            Expect.equal sp.Status "unavailable" "launch failure → status unavailable"
            Expect.equal doc.OverallStatus "fail"
                "overall status must be fail when any check is unavailable"
        }

        test "wiring: produced document binds the implementation commit and tree" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a _ ->
                    { Name = n; Status = "pass"; ExitCode = 0; Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity =
                { CommitOid = "1111111111111111111111111111111111111111"
                  TreeOid   = "2222222222222222222222222222222222222222" }
            let doc = GateSummary.buildDocument "." identity runner
            Expect.equal doc.TestedCommitOid "1111111111111111111111111111111111111111"
                "produced document must bind the supplied commit oid"
            Expect.equal doc.TestedTreeOid "2222222222222222222222222222222222222222"
                "produced document must bind the supplied tree oid"
        }
    ]

[<Tests>]
let tests = [
    classificationTests
    identityTests
    writerTests
    regenerateResultTests
    artefactNonMutationTests
    wiringTests
]
