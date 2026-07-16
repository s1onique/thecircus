#!/usr/bin/env python3
"""Generate .factory/gate-summary.json from the canonical local checks.

This script is the deterministic, fail-closed source of truth for the
"local canonical gate is green" claim.  It runs the three executable
gate checks the reviewer's R2.2 requires and produces a JSON artefact
under ``.factory/gate-summary.json`` using the **canonical Leamas v1
status vocabulary** so the targeted digest consumer
(``leamas factory digest``) can parse every check and recognise it as
``pass`` rather than ``unavailable``.

Canonical status vocabulary (per the Leamas v1 gate-summary contract):

    overall_status: pass | fail | unavailable
    check status:   pass | fail | skip | unavailable

The script also records the Git tree OID it was generated against as
``tested_tree_oid``.  Because ``.factory/gate-summary.json`` is
untracked (see ``.gitignore``), the artefact is *detached* from the
committed tree: it records ``git rev-parse HEAD^{tree}`` so the closure
commit can prove the captured evidence matches the **committed** tree
without any self-reference.  Re-running the script from the same
checked-out revision always yields the same OID, and the OID never
participates in the tree it stamps.

Why the OID is always ``HEAD^{tree}``:

* ``.factory/gate-summary.json`` is untracked, so it does not appear in
  ``HEAD^{tree}``.
* The closure commit is committed *first* and the artefact is
  regenerated against the *committed* tree afterwards.  The recorded
  OID is therefore a property of the committed source, not of any
  in-flight working tree state.
* ``git diff --cached --quiet`` is used (with proper exit-code
  preservation) only to *warn* the operator if they regenerated the
  artefact while unrelated changes are staged; the recorded OID never
  depends on those changes.

Re-running the script from the same checked-out revision must produce
the same ``overall_status``, the same per-check statuses, and the same
``tested_tree_oid``; only ``generated_at`` changes.
"""
from __future__ import annotations

import datetime as _dt
import json
import os
import subprocess
import sys
from dataclasses import dataclass
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUMMARY_PATH = ROOT / ".factory" / "gate-summary.json"
TOOL = "circus-regenerate-gate-summary"

# Canonical Leamas v1 status vocabulary.  The policy script asserts that
# every value written here belongs to one of these sets so the targeted
# digest consumer (``leamas factory digest``) cannot be silently broken
# by future edits.
VALID_OVERALL_STATUSES = {"pass", "fail", "unavailable"}
VALID_CHECK_STATUSES = {"pass", "fail", "skip", "unavailable"}


@dataclass(frozen=True)
class GitResult:
    """Captured outcome of a single ``git`` invocation.

    ``git diff --cached --quiet`` deliberately produces **no output** and
    reports the difference through its exit status (0 = no differences,
    1 = differences, 2+ = error).  A previous revision of this script
    collapsed the return code into the empty stdout string, which made
    it impossible to distinguish "no staged changes" from "git failed".
    This dataclass keeps the three signals separate so condition checks
    can read the exit code directly.
    """

    returncode: int
    stdout: str
    stderr: str


def run_git(*args: str, cwd: Path | None = None) -> GitResult:
    """Run ``git <args>`` and return a :class:`GitResult`.

    The caller decides what to do with the exit code; this function
    never raises on a non-zero exit code so condition commands such as
    ``git diff --cached --quiet`` can be inspected.
    """
    completed = subprocess.run(
        ["git", *args],
        cwd=cwd or ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    return GitResult(
        returncode=completed.returncode,
        stdout=completed.stdout,
        stderr=completed.stderr,
    )


def has_staged_changes() -> bool:
    """Return ``True`` iff the index differs from ``HEAD``.

    ``git diff --cached --quiet`` exits 0 when the index matches HEAD,
    exits 1 when the index has staged differences, and exits >=2 on
    error.  We translate that into a Python boolean and only raise on
    unexpected exit codes.
    """
    diff = run_git("diff", "--cached", "--quiet")
    if diff.returncode == 0:
        return False
    if diff.returncode == 1:
        return True
    raise RuntimeError(
        f"git diff --cached --quiet failed with exit {diff.returncode}: "
        f"{diff.stderr.strip() or '<no stderr>'}"
    )


def resolve_tested_tree_oid() -> str:
    """Return the tree OID the artefact was generated against.

    The committed tree (``HEAD^{tree}``) is the canonical subject of
    every Circus gate run: the closure commit is committed *first*,
    the artefact is regenerated against the committed tree
    *afterwards*.  The recorded OID therefore must equal
    ``HEAD^{tree}`` for the binding to hold on a fresh checkout.

    If the operator runs the script while unrelated changes are staged,
    we still record ``HEAD^{tree}`` so the binding remains stable, and
    we print a warning to stderr so the operator can decide whether to
    commit those changes first.
    """
    if has_staged_changes():
        print(
            "warning: staged changes detected; gate summary still records "
            "HEAD^{tree} so the binding matches the committed tree. "
            "Commit the staged changes before opening the PR.",
            file=sys.stderr,
        )

    head_tree = run_git("rev-parse", "HEAD^{tree}")
    if head_tree.returncode != 0:
        raise RuntimeError(
            f"git rev-parse HEAD^{{tree}} failed with exit "
            f"{head_tree.returncode}: "
            f"{head_tree.stderr.strip() or '<no stderr>'}"
        )
    oid = head_tree.stdout.strip()
    if not oid:
        raise RuntimeError("git rev-parse HEAD^{tree} returned no OID")
    return oid


def run_check(name: str, command: list[str]) -> dict:
    """Run one gate check and return its canonical record.

    The status is the **canonical** ``pass`` / ``fail`` value; the
    ``skip`` and ``unavailable`` values are reserved for cases where
    the check cannot be executed at all (handled by the caller).
    """
    completed = subprocess.run(
        command,
        cwd=ROOT,
        check=False,
        capture_output=True,
    )
    status = "pass" if completed.returncode == 0 else "fail"
    return {
        "name": name,
        "status": status,
        "exit_code": completed.returncode,
        "command": " ".join(command),
    }


def main() -> int:
    gates: list[tuple[str, list[str]]] = [
        (
            "container-publication-policy",
            [sys.executable, "scripts/verify_container_policy.py"],
        ),
        (
            "executable-shell-tests",
            ["bash", "tests/ci/test_build_publish_shell.sh"],
        ),
        (
            "action-pin-mutation-test",
            ["bash", "tests/ci/test_action_pin_mutation.sh"],
        ),
    ]

    checks = [run_check(name, command) for name, command in gates]
    passed_count = sum(1 for check in checks if check["status"] == "pass")
    failed_count = sum(1 for check in checks if check["status"] == "fail")

    if failed_count:
        overall = "fail"
    else:
        overall = "pass"

    tested_tree_oid = resolve_tested_tree_oid()

    summary = {
        "schema_version": 1,
        "generated_at": _dt.datetime.now(tz=_dt.timezone.utc).isoformat(),
        "tool": TOOL,
        "overall_status": overall,
        "checks_total": len(checks),
        "checks_passed": passed_count,
        "checks_failed": failed_count,
        "checks_unavailable": 0,
        "checks": checks,
        "tested_tree_oid": tested_tree_oid,
    }

    # Sanity check: every value we are about to write must be canonical.
    # This guards the generator against future edits that re-introduce
    # non-canonical vocabulary such as ``green`` / ``passed``.
    if summary["overall_status"] not in VALID_OVERALL_STATUSES:
        raise SystemExit(
            f"internal error: refusing to write non-canonical "
            f"overall_status {summary['overall_status']!r}; expected one "
            f"of {sorted(VALID_OVERALL_STATUSES)}"
        )
    for index, check in enumerate(summary["checks"]):
        if check["status"] not in VALID_CHECK_STATUSES:
            raise SystemExit(
                f"internal error: refusing to write non-canonical "
                f"checks[{index}].status {check['status']!r}; expected "
                f"one of {sorted(VALID_CHECK_STATUSES)}"
            )

    SUMMARY_PATH.parent.mkdir(parents=True, exist_ok=True)
    SUMMARY_PATH.write_text(
        json.dumps(summary, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    print(
        f"gate summary written to {SUMMARY_PATH.relative_to(ROOT)}: "
        f"{overall} ({passed_count}/{len(checks)} pass) "
        f"tree={tested_tree_oid[:12]}"
    )
    return 0 if overall == "pass" else 1


if __name__ == "__main__":
    raise SystemExit(main())
