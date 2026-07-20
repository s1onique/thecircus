module Circus.Tooling.Tests.Program

/// Entry point for the tooling test suite.
///
/// ``Expecto.Tests.runTestsInAssemblyWithCLIArgs`` discovers every
/// ``[<Tests>]`` binding in the executing assembly and runs them as
/// one suite.  Crucially, **it returns a process exit code**: ``0``
/// when every test passes and ``1`` when at least one test fails,
/// is errored, or is otherwise non-green.  We forward that value
/// verbatim so the canonical gate ``make test-source-policy`` is
/// fail-closed: a failing suite propagates a non-zero status up to
/// the gate runner.
///
/// We avoid depending on the version-specific shape of
/// ``runTestsWithCLIArgs`` (which takes a manually-supplied
/// ``Test`` value and is sensitive to the FocusState API changes
/// in Expecto 11.x) and instead use the assembly-level runner that
/// has been stable since Expecto 9.x.
open Expecto

[<EntryPoint>]
let main (arguments: string[]) : int =
    Expecto.Tests.runTestsInAssemblyWithCLIArgs [||] arguments
