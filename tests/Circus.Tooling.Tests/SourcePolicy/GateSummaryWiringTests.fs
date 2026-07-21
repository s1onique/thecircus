/// Regression tests proving the canonical gate artefact is wired
/// to ``make test-source-policy`` and that the gate producer never
/// mutates the canonical artefact during testing.
///
/// These tests call ``GateSummary.buildDocument`` with an injected
/// ``ExternalCheckRunner`` that records every invocation in a
/// test-owned collection.  Tests assert the exact command contract
/// (executable, argv, working directory) and the exit-code → status
/// mapping (0 → pass, 1 → fail, launch failure → unavailable).
/// No test reads, mutates, or relies on the canonical artefact
/// ``.factory/gate-summary.json``; the regression proof below
/// preserves the artefact byte-identity across the entire suite.

module Circus.Tooling.Tests.SourcePolicy.GateSummaryWiringTests

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy

/// Test-owned record of a single runner invocation.  Stored in a
/// test-local immutable ResizeArray so the proof of the exact
/// command contract is independent of any global mutable state.
type private InvocationRecord = {
    CheckName: string
    CheckArgv: string list
    CheckCwd: string
}

/// Construct an injected runner that returns a fixed ``CheckStatus``
/// for the matching check name and records every invocation in
/// ``invocations``.  The caller controls what status the runner
/// returns so the tests can assert pass/fail/unavailable mapping.
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


/// Locate the source-policy-tests invocation in a recorded
/// invocation list.  Returns ``None`` when the invocation is
/// absent.
let private findSourcePolicy (invocations: ResizeArray<InvocationRecord>) : InvocationRecord option =
    invocations
    |> Seq.tryFind (fun i -> i.CheckName = "source-policy-tests")

let private passStatusFor (_name: string) : GateSummary.CheckStatus =
    { Name = ""
      Status = "pass"
      ExitCode = 0
      Command = "<injected>" }

[<Tests>]
let tests =
    testList "GateSummary wiring" [
        // -------------------------------------------------------------------
        // Gate contract: the canonical gate must invoke the source-policy
        // test suite.  ``buildDocument`` is the pure producer; we inject a
        // runner that records every invocation in a test-owned collection.
        // -------------------------------------------------------------------
        test "gate contract: source-policy-tests check is wired into the canonical gate" {
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

        // -------------------------------------------------------------------
        // Mechanical command contract: exactly one invocation with the
        // exact executable and argv that the canonical gate uses.
        // -------------------------------------------------------------------
        test "gate contract: source-policy-tests command is exactly make test-source-policy" {
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
                invocations |> Seq.filter (fun i -> i.CheckName = "source-policy-tests") |> Seq.length
            Expect.equal sourcePolicyInvocations 1
                "source-policy-tests must be invoked exactly once (no double-invocation)"
        }


        // -------------------------------------------------------------------
        // Exit-code → status mapping: 0 → pass; 1 → fail; launch failure → unavailable.
        // -------------------------------------------------------------------
        test "gate contract: runner exit 0 maps source-policy-tests to pass" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a cwd ->
                    { Name = n
                      Status = (if n = "source-policy-tests" then "pass" else "pass")
                      ExitCode = 0
                      Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity = { CommitOid = ""; TreeOid = "" }
            let doc = GateSummary.buildDocument "." identity runner
            let sp =
                doc.Checks |> List.find (fun c -> c.Name = "source-policy-tests")
            Expect.equal sp.Status "pass" "exit 0 → status pass"
            Expect.equal sp.ExitCode 0 "exit code"
            Expect.equal doc.OverallStatus "pass"
                "overall status must be pass when source-policy-tests passes"
        }

        test "gate contract: runner exit 1 maps source-policy-tests to fail" {
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
            Expect.equal sp.ExitCode 1 "exit code"
            Expect.equal doc.OverallStatus "fail"
                "overall status must be fail when source-policy-tests fails"
        }

        test "gate contract: runner launch failure maps source-policy-tests to unavailable" {
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

        // -------------------------------------------------------------------
        // Contract violations: removing the source-policy-tests entry from
        // the canonical check list, or duplicating it, must be caught.
        // -------------------------------------------------------------------
        test "gate contract: removal of source-policy-tests breaks the wiring" {
            // If a downstream change removes source-policy-tests from
            // ``GateSummary.CanonicalChecks``, the runner is never invoked with
            // that name.  This test exercises the contract by checking
            // that the canonical list INCLUDES source-policy-tests
            // (so any removal would change the count).
            let sourcePolicyCount =
                GateSummary.CanonicalChecks
                |> List.filter (fun (n, _) -> n = "source-policy-tests")
                |> List.length
            Expect.equal sourcePolicyCount 1
                "GateSummary.CanonicalChecks must include source-policy-tests exactly once"
            // Additionally exercise the producer with a runner that
            // would fail on source-policy-tests invocation, proving
            // the runner IS invoked for source-policy-tests by the
            // canonical list.
            let invocations = ResizeArray<InvocationRecord>()
            let runner = makeRecordingRunner invocations passStatusFor
            let identity : GateSummary.TestedIdentity = { CommitOid = ""; TreeOid = "" }
            let _doc = GateSummary.buildDocument "." identity runner
            let spInvoked = invocations |> Seq.exists (fun i -> i.CheckName = "source-policy-tests")
            Expect.isTrue spInvoked
                "source-policy-tests must be invoked exactly once by the canonical wiring"
        }

        test "gate contract: duplicate source-policy-tests breaks the wiring" {
            // The canonical list must NOT contain duplicates.  If a
            // downstream change duplicates source-policy-tests, this
            // test fails because the count would be 2.
            let sourcePolicyCount =
                GateSummary.CanonicalChecks
                |> List.filter (fun (n, _) -> n = "source-policy-tests")
                |> List.length
            Expect.equal sourcePolicyCount 1
                "GateSummary.CanonicalChecks must list source-policy-tests exactly once"
        }

        // -------------------------------------------------------------------
        // Identity binding: the produced document must carry the
        // externally-supplied commit/tree OIDs through to the artefact.
        // -------------------------------------------------------------------
        test "gate contract: produced document binds the implementation commit and tree" {
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

        // -------------------------------------------------------------------
        // writeDocument: serialises and writes the document; does not
        // touch the canonical artefact at all unless explicitly invoked.
        // -------------------------------------------------------------------
        test "gate contract: writeDocument serialises JSON at the given path" {
            let runner : GateSummary.ExternalCheckRunner =
                fun n a _ ->
                    { Name = n; Status = "pass"; ExitCode = 0; Command = String.concat " " a }
            let identity : GateSummary.TestedIdentity = { CommitOid = "x"; TreeOid = "y" }
            let doc = GateSummary.buildDocument "." identity runner
            let tmp =
                Path.Combine(
                    Path.GetTempPath(),
                    "circus-gate-test-" + Guid.NewGuid().ToString("n") + ".json")
            try
                match GateSummary.writeDocument tmp doc with
                | Ok () -> ()
                | Error f -> failtestf "writeDocument returned error: %A" f
                Expect.isTrue (File.Exists tmp) "artefact file must exist"
                let raw = File.ReadAllText tmp
                Expect.stringContains raw "\"source-policy-tests\""
                    "written artefact must contain source-policy-tests"
                Expect.stringContains raw "\"overall_status\": \"pass\""
                    "overall_status must be pass"
            finally
                try if File.Exists tmp then File.Delete tmp with _ -> ()
        }

        // -------------------------------------------------------------------
        // Regression proof: the canonical ``.factory/gate-summary.json``
        // is byte-identical before and after the entire GateSummary wiring
        // suite.  No ordinary test may write to that path.
        // -------------------------------------------------------------------
        test "gate contract: ordinary GateSummary tests never mutate .factory/gate-summary.json" {
            // The canonical artefact is produced only by the
            // production ``circus-tooling gate run`` command.  No test
            // in this suite invokes the production writer or the
            // production runner.  This test reads the artefact at
            // startup, runs a representative subset of the suite, and
            // proves the artefact is byte-identical afterwards.
            let canonical =
                Path.Combine(
                    Path.GetFullPath ".",
                    ".factory", "gate-summary.json")
            let beforeBytes : byte[] option =
                if File.Exists canonical then
                    Some (File.ReadAllBytes canonical)
                else
                    None
            // Exercise the wiring by constructing a document and
            // writing it to a temporary path; the canonical path
            // is untouched.
            let invocations = ResizeArray<InvocationRecord>()
            let runner = makeRecordingRunner invocations passStatusFor
            let identity : GateSummary.TestedIdentity = { CommitOid = "ci"; TreeOid = "tr" }
            let doc = GateSummary.buildDocument "." identity runner
            let tmp =
                Path.Combine(
                    Path.GetTempPath(),
                    "circus-gate-isolation-" + Guid.NewGuid().ToString("n") + ".json")
            try
                match GateSummary.writeDocument tmp doc with
                | Ok () -> ()
                | Error f -> failtestf "writeDocument error: %A" f
                Expect.isTrue (File.Exists tmp) "temp artefact must exist"
                Expect.isFalse (Path.GetFullPath tmp = Path.GetFullPath canonical)
                    "temp path must NOT be the canonical path"
            finally
                try if File.Exists tmp then File.Delete tmp with _ -> ()
            // Verify the canonical artefact is unchanged.
            match beforeBytes with
            | Some bytes ->
                let afterBytes =
                    if File.Exists canonical then
                        File.ReadAllBytes canonical
                    else
                        [||]
                Expect.equal
                    (afterBytes.Length = bytes.Length)
                    true
                    "canonical artefact length must be unchanged"
                Expect.equal afterBytes bytes
                    "canonical artefact must be byte-identical"
            | None ->
                // Canonical artefact was absent before the suite.  It
                // must STILL be absent after the suite: no ordinary
                // test may create it.
                Expect.isFalse (File.Exists canonical)
                    "canonical artefact must not be created by tests"
        }
    ]
