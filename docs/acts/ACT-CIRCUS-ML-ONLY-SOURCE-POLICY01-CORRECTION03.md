# ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION03

**Status:** CLOSED — PARTIAL CHECKPOINT; canonical container gate green,
source-policy convergence still PARTIAL.  Mass operational-tooling
migration remains owed to
`EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01`.

**Predecessor ACTs:**
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01` (closed PARTIAL)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` (PARTIAL CHECKPOINT)
* `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION02` (closed PARTIAL but
  claimed PASS while the gate was actually red — the
  `action-pin-mutation-test` check was calling the deleted Python
  script)

## Scope (delivered)

1. Return the actual Expecto exit code from the test runner so
   `make test-source-policy` is fail-closed.
2. Repair the failing action-pin mutation test (the canonical
   ``bash tests/ci/test_action_pin_mutation.sh``) so it invokes the
   F# tooling from the sandbox git working directory and reproduces
   the SHA-to-tag mutation against ``actions/checkout``.
3. Make ``gate run`` validate every artefact (even valid failed
   summaries) and combine the three verdicts with severity
   ordering ``2 > 1 > 0``.
4. Surface CP-29 as an operational failure (exit 2) when
   ``git ls-files`` cannot enumerate the tracked tree, instead of
   silently passing.
5. Count failed checks separately from violations;
   ``unavailable`` is no longer double-counted in ``checks_failed``.
6. Classify process-launch failure (ExitCode = -1) as
   ``status=unavailable`` instead of ``status=fail``.
7. Read ``git ls-files -z`` output through ``StreamReader.ReadToEndAsync``
   on the raw ``BaseStream``, not the line-oriented
   ``OutputDataReceived`` / ``BeginOutputReadLine`` API; filenames with
   embedded newlines or NULs are preserved byte-for-byte.
8. ``splitNulInventory`` no longer ``Trim``s NUL-delimited paths;
   legitimate leading/trailing whitespace is preserved.
9. The CP-29 mutation test now initialises the git repository
   (``tryInitGit root``) before invoking ``gitTrackedFiles root``.
10. RID-neutral Makefile via ``CIRCUS_TOOLING_DLL`` /
    ``CIRCUS_TOOLING := $(DOTNET) $(CIRCUS_TOOLING_DLL)``.
11. ``factory/container-policy-parity.csv`` rewritten with one row per
    legacy check and an honest ``status`` column (``complete`` only
    for checks with a dedicated negative mutation test).

## Acceptance criteria (closure commit)

* [x] Test exit propagation: ``Program.fs`` returns
      ``runTestsInAssemblyWithCLIArgs``'s integer exit code.
* [x] Action-pin mutation test passes on the committed tree.
* [x] ``gate run`` validates every artefact and combines verdicts.
* [x] CP-29 fails closed (exit 2) on git failure.
* [x] Failed checks separated from violations;
      ``unavailable`` no longer double-counted.
* [x] Process-launch failure → ``status=unavailable``.
* [x] NUL-safe inventory preserves whitespace (no ``Trim``).
* [x] Raw stream reading via ``ReadToEndAsync`` (no line-oriented
      callbacks).
* [x] CP-29 mutation test initialises git first.
* [x] RID-neutral Makefile via canonical ``CIRCUS_TOOLING`` variable.
* [x] Parity CSV rewritten with honest status column.
* [x] ``git diff --check HEAD~1 HEAD`` passes.
* [x] Clean committed-tree evidence:
      ``overall_status=pass, checks_total=3, checks_passed=3,
       checks_failed=0, checks_unavailable=0``.

## Acceptance criteria (deferred to a future ACT)

* [ ] A complete 31-dedicated-mutation-test matrix.  Currently
      CP-01, CP-02, CP-03, CP-09, CP-13, CP-22, CP-23, CP-28, and
      CP-29 ship dedicated negative mutation tests; the remaining 22
      rows are marked ``partial — positive only`` in the parity CSV.
* [ ] Machine-validated parity references: an Expecto test that
      loads the parity CSV and asserts every cited test name exists
      and every ``legacy_check_id`` maps to a ``CheckIds`` member.
* [ ] Truthful ``violations_total`` semantics: either surface real
      per-child violation counts (e.g. via a sibling JSON artefact)
      or remove the field until it can be computed truthfully.

## Successor

* `EPIC-CIRCUS-ML-ONLY-OPERATIONAL-TOOLING-MIGRATION01` (per
  CORRECTION01 § 13): mass shell-script migration, container
  publication policy, Harbor build/publish orchestration, CI
  mutation and acceptance tests, GitHub helper scripts,
  development-host bootstrap, remaining stage-zero launchers,
  third-party frontend toolchain invocation.  The epic should also
  land the missing 22 dedicated negative mutation tests and a
  machine validator for the parity CSV.
