#!/usr/bin/env python3
"""Generate .factory/gate-summary.json from the canonical local checks.

This script is the deterministic, fail-closed source of truth for the
"local canonical gate is green" claim.  It runs the three executable
gate checks the reviewer's R2.2 requires and produces a JSON artefact
under .factory/gate-summary.json.  The artefact schema is stable and
forward-compatible:

    {
        "schema_version": 1,
        "gate_id": "circus-container-publish01",
        "doctrine": "ACT-CIRCUS-CONTAINER-HARBOR-PUBLISH01",
        "generated_at": "<RFC 3339 timestamp>",
        "generated_by": "<leamas-core@<git sha>>",
        "source": ".factory/gate-summary.json",
        "source_status": "present",
        "overall_status": "green" | "red",
        "checks_total": <int>,
        "checks_passed": <int>,
        "checks_failed": <int>,
        "checks": [ <Check> ],
        "evidence": { ... pinned SHA-256 values ... }
    }

    Check = {
        "id": "<machine id>",
        "label": "<human label>",
        "command": "<exact command>",
        "status": "passed" | "failed",
        "exit_code": <int>,
        "duration_seconds": <float>,
        "stdout_tail": "<last 4 KiB of stdout>",
        "stderr_tail": "<last 4 KiB of stderr>",
    }

The script is itself a reproducibility mechanism: re-running it from
the same tree must produce the same overall_status and the same
checks_passed / checks_failed counts.
"""
from __future__ import annotations

import datetime as _dt
import json
import os
import subprocess
import sys
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
SUMMARY_PATH = ROOT / ".factory" / "gate-summary.json"
TAIL_BYTES = 4 * 1024


def _tail(stream: bytes | str) -> str:
    if isinstance(stream, bytes):
        text = stream.decode("utf-8", errors="replace")
    else:
        text = stream
    if len(text.encode("utf-8")) > TAIL_BYTES:
        # Drop the leading bytes and resume at a UTF-8 boundary so the
        # tail never starts mid-codepoint.
        encoded = text.encode("utf-8")[-TAIL_BYTES:]
        text = encoded.decode("utf-8", errors="replace")
    return text


def _run(label: str, command: list[str]) -> dict:
    started = _dt.datetime.now(tz=_dt.timezone.utc)
    completed_proc = subprocess.run(
        command,
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=False,
    )
    finished = _dt.datetime.now(tz=_dt.timezone.utc)
    duration = (finished - started).total_seconds()
    status = "passed" if completed_proc.returncode == 0 else "failed"
    return {
        "id": label,
        "label": label,
        "command": " ".join(command),
        "status": status,
        "exit_code": completed_proc.returncode,
        "duration_seconds": round(duration, 3),
        "started_at": started.isoformat(),
        "finished_at": finished.isoformat(),
        "stdout_tail": _tail(completed_proc.stdout),
        "stderr_tail": _tail(completed_proc.stderr),
    }


def _git_short_sha() -> str:
    completed = subprocess.run(
        ["git", "rev-parse", "HEAD"],
        cwd=ROOT,
        check=False,
        capture_output=True,
        text=True,
    )
    if completed.returncode != 0:
        return "uncommitted"
    return completed.stdout.strip()[:12]


def main() -> int:
    # The three canonical local gates.  Each is a deterministic check
    # that fails closed when the staged tree regresses.
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

    results = [_run(label, command) for label, command in gates]
    passed = sum(1 for result in results if result["status"] == "passed")
    failed = len(results) - passed
    overall = "green" if failed == 0 else "red"

    summary = {
        "schema_version": 1,
        "gate_id": "circus-container-publish01",
        "doctrine": "ACT-CIRCUS-CONTAINER-HARBOR-PUBLISH01",
        "generated_at": _dt.datetime.now(tz=_dt.timezone.utc).isoformat(),
        "generated_by": f"leamas-core@{_git_short_sha()}",
        "source": ".factory/gate-summary.json",
        "source_status": "present",
        "overall_status": overall,
        "checks_total": len(results),
        "checks_passed": passed,
        "checks_failed": failed,
        "checks": results,
        "evidence": {
            # Pinned supply-chain artefacts.  Updating these requires
            # a deliberate review of the upstream release attestation.
            "shellcheck_release": {
                "version": "0.11.0",
                "asset": "shellcheck-v0.11.0.linux.x86_64.tar.xz",
                "sha256": "8afc50b302d5feeac9381ea114d563f0150d061520042b254d6eb715797c8223",
            },
            "actionlint_release": {
                "version": "1.7.12",
                "asset": "actionlint_1.7.12_linux_amd64.tar.gz",
                "sha256": "8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8",
            },
            "actions_checkout_pins": [
                "actions/checkout@de0fac2e4500dabe0009e67214ff5f5447ce83dd",
            ],
            "harbor_host": "harbor-pve1.spbnix.local",
            "harbor_project": "circus",
            "buildkit_image": "harbor-pve1.spbnix.local/dockerhub-cache/moby/buildkit:buildx-stable-1",
        },
        "remediation": {
            "container_publication_policy": "Update scripts/verify_container_policy.py and the files it inspects (Dockerfile.*, .github/workflows/*.yml, scripts/ci/*.sh).",
            "executable_shell_tests": "Update tests/ci/test_build_publish_shell.sh and the scripts/ci/*.sh files it exercises.",
            "action_pin_mutation_test": "Update scripts/verify_container_policy.py's action-pin parser so it rejects unpinned uses: entries.",
        },
    }

    SUMMARY_PATH.parent.mkdir(parents=True, exist_ok=True)
    SUMMARY_PATH.write_text(
        json.dumps(summary, indent=2, sort_keys=False) + "\n",
        encoding="utf-8",
    )

    # Emit a one-line confirmation to stdout so the caller (CI or a
    # developer) sees the overall outcome.
    print(
        f"gate summary written to {SUMMARY_PATH.relative_to(ROOT)}: "
        f"{overall} ({passed}/{len(results)} passed)"
    )
    return 0 if overall == "green" else 1


if __name__ == "__main__":
    raise SystemExit(main())