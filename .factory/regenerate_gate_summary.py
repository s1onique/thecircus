#!/usr/bin/env python3
"""Generate .factory/gate-summary.json from the canonical local checks.

This script is the deterministic, fail-closed source of truth for the
"local canonical gate is green" claim.  It runs the three executable
gate checks the reviewer's R2.2 requires and produces a JSON artefact
under .factory/gate-summary.json using the **canonical Leamas schema** so
the targeted digest consumer (`leamas factory gate-summary`) can parse
every check and recognise it as `passed` rather than `unavailable`.

Canonical schema (the same shape `leamas factory gate-summary` writes):

    {
        "schema_version": 1,
        "generated_at":   "<RFC 3339 timestamp>",
        "tool":           "<producer identifier>",
        "overall_status": "green" | "red",
        "checks": [
            { "name": "<id>", "status": "passed" | "failed" | "unavailable" },
            ...
        ]
    }

The script also records the Git tree OID it was generated against as
`tested_tree_oid`.  This avoids the self-referential problem where the
summary contains the commit that contains the summary: the artefact
binds to the **tree** rather than to a commit.  The closure commit can
then prove `git rev-parse HEAD^{tree}` equals the recorded
`tested_tree_oid`, which means the captured evidence matches the
committed tree rather than an ancestor.

Re-running the script from the same tree must produce the same
overall_status and the same per-check statuses; only `generated_at`
and `tested_tree_oid` change.
"""
from __future__ import annotations

import datetime as _dt
import hashlib
import json
import os
import re
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUMMARY_PATH = ROOT / ".factory" / "gate-summary.json"
TOOL = "circus-regenerate-gate-summary"


def _run_git(*args: str) -> str:
    completed = subprocess.run(
        ["git", *args],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        return ""
    return completed.stdout.strip()


def _resolve_tested_tree_oid() -> str:
    """Return the tree OID that the artefact was generated against.

    We prefer the **staged tree** so the summary can be regenerated
    before `git commit` and the closure commit can prove it later via
    `git rev-parse HEAD^{tree}`.  When there are no staged changes we
    fall back to HEAD^{tree}; when there is no commit yet we fall back
    to `git write-tree` of the worktree.
    """
    staged = _run_git("diff", "--cached", "--quiet")
    if staged == "" or staged == "0":
        # `git diff --cached --quiet` prints nothing on success and the
        # exit code is 0 when there is at least one staged change.  When
        # the index matches HEAD we want the staged-tree view if the
        # index has been touched, otherwise HEAD^{tree}.
        head_tree = _run_git("rev-parse", "HEAD^{tree}")
        if head_tree:
            return head_tree
        return _run_git("write-tree")

    # Exit code 1 means there are staged differences; capture them.
    index_tree = _run_git("write-tree")
    if index_tree:
        return index_tree
    return _run_git("rev-parse", "HEAD^{tree}")


def _run_check(name: str, command: list[str]) -> dict:
    completed = subprocess.run(
        command,
        cwd=ROOT,
        check=False,
        capture_output=True,
    )
    status = "passed" if completed.returncode == 0 else "failed"
    # The canonical schema only requires `name` and `status`, but we
    # keep the exit_code so a human reviewer can triage failures from
    # the JSON without re-running the checks.
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

    checks = [_run_check(name, command) for name, command in gates]
    passed_count = sum(1 for check in checks if check["status"] == "passed")
    overall = "green" if passed_count == len(checks) else "red"

    tested_tree_oid = _resolve_tested_tree_oid()

    summary = {
        "schema_version": 1,
        "generated_at": _dt.datetime.now(tz=_dt.timezone.utc).isoformat(),
        "tool": TOOL,
        "overall_status": overall,
        "checks_total": len(checks),
        "checks_passed": passed_count,
        "checks_failed": sum(1 for check in checks if check["status"] == "failed"),
        "checks_unavailable": sum(
            1 for check in checks if check["status"] == "unavailable"
        ),
        "checks": checks,
        "tested_tree_oid": tested_tree_oid,
    }

    SUMMARY_PATH.parent.mkdir(parents=True, exist_ok=True)
    SUMMARY_PATH.write_text(
        json.dumps(summary, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    print(
        f"gate summary written to {SUMMARY_PATH.relative_to(ROOT)}: "
        f"{overall} ({passed_count}/{len(checks)} passed) "
        f"tree={tested_tree_oid[:12]}"
    )
    return 0 if overall == "green" else 1


if __name__ == "__main__":
    raise SystemExit(main())