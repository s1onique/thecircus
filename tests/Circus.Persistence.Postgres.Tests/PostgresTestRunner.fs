module Circus.Persistence.Postgres.Tests.PostgresTestRunner

open Expecto

/// Hermetic seam around `Tests.runTestsWithCLIArgs`.
///
/// The production entry point delegates to this function so that the
/// OS exit code is exactly whatever the Expecto runner returns.  The
/// function takes the runner as a value rather than referencing it
/// directly so that hermetic tests can substitute a deterministic
/// runner without spinning up PostgreSQL or otherwise exercising
/// infrastructure.
///
/// Behavioural contract:
///
/// * The integer returned by `runner` flows directly to the caller
///   without coercion.  A zero becomes zero; a non-zero becomes the
///   same non-zero.  No retry, normalisation, or translation occurs.
/// * Exceptions raised by `runner` are not caught, suppressed, or
///   remapped.  They propagate to the caller exactly as raised.
/// * No mutable global state is introduced and no PostgreSQL
///   resource is touched.
let runWith
    (runner: CLIArguments seq -> string array -> Test -> int)
    (argv: string array)
    (tests: Test)
    : int =
    runner [||] argv tests
