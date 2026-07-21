/// Regression tests proving the canonical gate artefact is wired
/// to ``make test-source-policy``.  The single producer of the
/// artefact is ``Circus.Tooling.SourcePolicy.GateSummary``; if the
/// ``externalChecks`` list ever drops the ``source-policy-tests``
/// invocation, the canonical gate would silently regress.
///
/// The tests below set ``CIRCUS_GATE_SKIP_SOURCE_POLICY=1`` while
/// exercising the producer so the make invocation that would
/// otherwise recurse is replaced with an ``unavailable`` record.
/// The escaping of the make call lets us assert that the
/// ``source-policy-tests`` name is wired into the producer
/// without entering an infinite ``make test-source-policy`` loop.

module Circus.Tooling.Tests.SourcePolicy.GateSummaryWiringTests

open Expecto

open Circus.Tooling.SourcePolicy

[<Tests>]
let tests =
    testList "GateSummary wiring" [
        // -------------------------------------------------------------------
        // Gate contract: the canonical gate must invoke the source-policy
        // test suite.  ``GateSummary.regenerate`` is the producer of the
        // canonical artefact; the regression test below invokes the producer
        // with the make-recursion escape hatch set and asserts that the
        // emitted artefact contains a check named ``source-policy-tests``.
        // Removing the invocation from ``externalChecks`` would drop this
        // check from the artefact and cause this test to fail.
        // -------------------------------------------------------------------
        test "gate contract: source-policy-tests check is wired into the canonical gate" {
            // Break the otherwise-circular dependency between
            // ``make test-source-policy`` and the gate producer so the
            // producer can be exercised without recursing into another
            // ``make test-source-policy``.  The escape hatch causes the
            // producer to record the check as ``unavailable`` with a
            // marker command string; the test asserts the check name is
            // still present in the emitted artefact.
            System.IO.File.WriteAllText(
                "/tmp/circus-test-source-policy-stub",
                "skipped via CIRCUS_GATE_SKIP_SOURCE_POLICY=1\n")
            System.Environment.SetEnvironmentVariable(
                "CIRCUS_GATE_SKIP_SOURCE_POLICY", "1")
            try
                let doc = GateSummary.regenerate (System.IO.Path.GetFullPath ".")
                let names = doc.Checks |> List.map (fun c -> c.Name)
                Expect.isTrue
                    (List.contains "source-policy-tests" names)
                    "gate artefact must contain a check named source-policy-tests"
            finally
                System.Environment.SetEnvironmentVariable(
                    "CIRCUS_GATE_SKIP_SOURCE_POLICY", null)
                try System.IO.File.Delete "/tmp/circus-test-source-policy-stub" with _ -> ()
        }

        // -------------------------------------------------------------------
        // Belt-and-braces regression: when the ``.factory/gate-summary.json``
        // artefact has been regenerated against this source tree by the
        // production path (no escape hatch), the check list MUST contain
        // ``source-policy-tests``.  This test is skipped when the artefact
        // is absent (e.g. on a fresh checkout before ``make test-source-policy``
        // has run) so the test does not itself recursively launch the make
        // invocation it is asserting.
        // -------------------------------------------------------------------
        test "gate contract: gate-summary.json artefact records source-policy-tests" {
            let repoRoot = System.IO.Path.GetFullPath "."
            let summaryPath =
                System.IO.Path.Combine(repoRoot, ".factory", "gate-summary.json")
            if not (System.IO.File.Exists summaryPath) then
                // Skip the assertion when the artefact has not yet been
                // regenerated.  The previous test still guards the wiring.
                ()
            else
                let raw = System.IO.File.ReadAllText summaryPath
                Expect.stringContains
                    raw "\"source-policy-tests\""
                    "regenerated gate-summary.json must contain source-policy-tests check"
        }
    ]
