module Circus.Persistence.Postgres.Tests.Runner.PostgresTestRunner

// =============================================================================
// PostgresTestRunner – hermetic seam
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION01
//
// This module is the small support library that exposes the
// ``runWith`` seam.  The library has NO dependency on Testcontainers,
// no PostgresFixture, and no Npgsql.  It depends only on Expecto.
//
// The hermetic test executable references this library and never
// instantiates a PostgresFixture.  The production test executable
// (``tests/Circus.Persistence.Postgres.Tests``) ALSO references this
// library and composes the seam with the full PostgresFixture, but
// the hermetic tests can run without any of that baggage.
// =============================================================================

open Expecto

/// Hermetic seam around ``Tests.runTestsWithCLIArgs``.
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
/// * The integer returned by ``runner`` flows directly to the caller
///   without coercion.  A zero becomes zero; a non-zero becomes the
///   same non-zero.  No retry, normalisation, or translation occurs.
/// * Exceptions raised by ``runner`` are not caught, suppressed, or
///   remapped.  They propagate to the caller exactly as raised.
/// * No mutable global state is introduced and no PostgreSQL
///   resource is touched.
let runWith (runner: CLIArguments seq -> string array -> Test -> int) (argv: string array) (tests: Test) : int =
    runner [||] argv tests
