module Circus.Tooling.Tests.Program

/// Entry point for the tooling test suite.
///
/// Two execution modes:
///
///   1. ``CIRCUS_EXPECTO_META_FIXTURE=available-failing-body`` selects
///      an isolated fixture mode used by the
///      ``available test with failing body produces exactly one
///      failure`` meta-test.  In fixture mode the program:
///
///      - emits a machine-readable body marker on stdout before
///        Expecto runs,
///      - constructs exactly one test through ``makeBashDependentTest``
///        with ``BashAvailable``,
///      - fails through an Expecto assertion (NOT ``failwith`` / raw
///        exception),
///      - runs only this generated test,
///      - writes a JUnit summary via Expecto's native ``--junit-summary``
///        CLI handler (when the parent passes the flag),
///      - exits with Expecto's runner return code (0 pass / 1 failure).
///
///      The fixture mode never discovers the regular suite, so the
///      parent Expecto run is isolated from nested-runner state and
///      the JUnit summary contains exactly one ``<testcase>`` element.
///
///   2. The default mode discovers every ``[<Tests>]`` binding and runs
///      them as one suite, returning ``runTestsInAssemblyWithCLIArgs``'s
///      exit code so the canonical gate ``make test-source-policy`` is
///      fail-closed.

open System
open Expecto

open Circus.Tooling.Tests.SourcePolicy.ProcessRunnerTests



[<EntryPoint>]
let main (arguments: string[]) : int =
    let fixtureMode =
        Environment.GetEnvironmentVariable("CIRCUS_EXPECTO_META_FIXTURE")

    match fixtureMode with
    | "available-failing-body" ->
        // Fixture mode: emit the body marker on stdout before any
        // Expecto output, then run exactly one test via
        // ``makeBashDependentTest``.  The CLI args passed by the
        // parent (e.g. ``--junit-summary``) are forwarded verbatim
        // to ``runTestsWithCLIArgs`` so Expecto's own JUnit summary
        // handler writes the file the parent parses.
        printfn "%s" ChildFixtureBodyMarker
        do System.Console.Out.Flush()


        let fixtureTest =
            makeBashDependentTest
                (BashAvailable "bash")
                "deliberate failure"
                (fun (executable: string) ->
                    Expect.equal 1 2
                        (sprintf "deliberate failure for fixture proof (executable=%s)" executable))

        // ``runTestsWithCLIArgs`` signature:
        //   (cliArgs: seq<CLIArguments>, args: string[], tests: Test) -> int
        // We pass empty CLIArguments (no pre-parsed filters) and the
        // parent's raw string[] args, which Expecto parses internally
        // for ``--junit-summary``, ``--sequenced``, ``--no-spinner``,
        // and other summary/control flags.
        Expecto.Tests.runTestsWithCLIArgs [||] arguments fixtureTest
    | _ ->
        // Default mode: discover and run the entire test suite.
        // ``runTestsInAssemblyWithCLIArgs`` returns 0 when every test
        // passes and 1 when at least one test fails/is errored; we
        // forward that verbatim so the canonical gate is fail-closed.
        Expecto.Tests.runTestsInAssemblyWithCLIArgs [||] arguments
