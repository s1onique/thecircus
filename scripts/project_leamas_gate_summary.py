#!/usr/bin/env python3
"""
Generate a Leamas-compatible gate-summary projection.

The canonical evidence provider emits .factory/gate-summary.json in
the "canonical-evidence-v1" wire format. The Leamas targeted digest
reader expects a different schema (the "gate-summary v1" schema from
an older doctrine contract). This script is the authority-preserving
compatibility projection: it reads the canonical artifact, validates
every required field is present, binds the projection to the
canonical artifact's semantic hash, and emits a Leamas-compatible
artifact at .factory/gate-summary.json.leamas.

The projection is GENERATED, NEVER HAND-AUTHORED. A new projection
must be produced by this script every time the canonical artifact
changes. The script fails closed if any required source field is
absent.

ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION02
"""
import argparse
import hashlib
import json
import sys
from datetime import datetime, timezone
from pathlib import Path

# Mapping from canonical-evidence-v1 status to Leamas gate-summary v1 status.
STATUS_MAP = {
    "pass": "passed",
    "fail": "failed",
    "unavailable": "unavailable",
}


def fail_closed(message: str) -> None:
    """Surface a missing field as a non-zero exit."""
    sys.stderr.write(f"project_leamas_gate_summary: FAIL ({message})\n")
    sys.exit(2)


def read_canonical(path: Path) -> dict:
    if not path.exists():
        fail_closed(f"canonical artifact not found: {path}")
    try:
        with path.open("r", encoding="utf-8") as fp:
            return json.load(fp)
    except json.JSONDecodeError as exc:
        fail_closed(f"canonical artifact not parseable: {exc}")
    return {}


def require_str(doc: dict, key: str) -> str:
    value = doc.get(key)
    if not isinstance(value, str) or not value:
        fail_closed(f"canonical artifact missing required field: {key}")
    return value


def require_int(doc: dict, key: str) -> int:
    value = doc.get(key)
    if not isinstance(value, int):
        fail_closed(f"canonical artifact missing required integer field: {key}")
    return value


def require_list(doc: dict, key: str) -> list:
    value = doc.get(key)
    if not isinstance(value, list):
        fail_closed(f"canonical artifact missing required list field: {key}")
    return value


def map_status(token: str) -> str:
    if token not in STATUS_MAP:
        fail_closed(f"canonical artifact has unknown status token: {token}")
    return STATUS_MAP[token]


def project(canonical: dict) -> dict:
    schema_version = require_int(canonical, "schema_version")
    if schema_version != 1:
        fail_closed(f"canonical schema_version mismatch: expected 1, got {schema_version}")
    provider_name = require_str(canonical, "provider_name")
    provider_version = require_str(canonical, "provider_version")
    tested_commit_oid = require_str(canonical, "tested_commit_oid")
    tested_tree_oid = require_str(canonical, "tested_tree_oid")
    object_format = require_str(canonical, "object_format")
    semantic_sha256 = require_str(canonical, "semantic_sha256")
    overall_status_token = require_str(canonical, "overall_status")
    overall_status = map_status(overall_status_token)
    checks = require_list(canonical, "checks")

    projected_checks = []
    for idx, check in enumerate(checks):
        if not isinstance(check, dict):
            fail_closed(f"check[{idx}] is not an object")
        check_id = require_str(check, "id")
        # Required: duration_ms and command_argv must be present so we
        # can populate duration_seconds and label. We do NOT
        # reinterpret missing fields as successful empty values; we
        # fail closed instead.
        duration_ms = require_int(check, "duration_ms")
        argv = require_list(check, "command_argv")
        status_token = require_str(check, "status")
        status = map_status(status_token)
        stdout_sha = check.get("stdout_sha256")
        stderr_sha = check.get("stderr_sha256")
        exit_code = check.get("exit_code")
        evidence = {
            "stdout_sha256": stdout_sha,
            "stderr_sha256": stderr_sha,
            "exit_code": exit_code,
            "command_argv": argv,
        }
        projected_checks.append({
            "id": check_id,
            "name": check_id,
            "status": status,
            "duration_seconds": round(duration_ms / 1000.0, 6),
            "exit_code": exit_code if exit_code is not None else 0,
            "evidence": evidence,
        })

    checks_passed = sum(1 for c in projected_checks if c["status"] == "passed")
    checks_failed = sum(1 for c in projected_checks if c["status"] == "failed")
    checks_unavailable = sum(
        1 for c in projected_checks if c["status"] == "unavailable"
    )

    projection = {
        "schema_version": 1,
        "gate_id": "circus-canonical-evidence",
        "doctrine": "ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01",
        "generated_at": datetime.now(timezone.utc).isoformat(),
        "generated_by": (
            f"circus-canonical-evidence/{provider_version}+"
            f"leamas-projection+{provider_name}"
        ),
        "source": ".factory/gate-summary.json",
        "source_status": "present",
        "tested_commit_oid": tested_commit_oid,
        "tested_tree_oid": tested_tree_oid,
        "object_format": object_format,
        "canonical_artifact_sha256": semantic_sha256,
        "overall_status": "green" if overall_status == "passed" else (
            "red" if overall_status == "failed" else "unavailable"
        ),
        "checks_total": len(projected_checks),
        "checks_passed": checks_passed,
        "checks_failed": checks_failed,
        "checks_unavailable": checks_unavailable,
        "checks": projected_checks,
    }
    return projection


def bind_semantic_hash(projection: dict, semantic_sha256: str) -> dict:
    """Bind the projection to the canonical artifact's semantic hash.

    The Leamas contract requires the projection to carry a stable
    identifier that ties it back to the canonical artifact. We embed
    the canonical semantic hash in a ``canonical_artifact_sha256``
    field that the canonical provider can re-verify on demand.
    """
    projection["canonical_artifact_sha256"] = semantic_sha256
    return projection


def verify(projection: dict, canonical_path: Path) -> bool:
    """Re-read the canonical artifact and confirm the projection is bound
    to its current semantic hash. This is the canonical provider's
    compatibility verification.
    """
    canonical = read_canonical(canonical_path)
    expected = canonical.get("semantic_sha256")
    actual = projection.get("canonical_artifact_sha256")
    return bool(expected) and bool(actual) and expected == actual


def main(argv: list) -> int:
    parser = argparse.ArgumentParser(
        description="Generate a Leamas-compatible gate-summary projection."
    )
    parser.add_argument(
        "--canonical",
        default=".factory/gate-summary.json",
        help="Path to the canonical-evidence artifact",
    )
    parser.add_argument(
        "--output",
        default=".factory/gate-summary.json.leamas",
        help="Path to write the Leamas projection",
    )
    parser.add_argument(
        "--verify",
        action="store_true",
        help="Re-verify the projection against the canonical artifact's "
             "current semantic hash after writing",
    )
    args = parser.parse_args(argv)

    canonical_path = Path(args.canonical)
    canonical = read_canonical(canonical_path)
    semantic_sha256 = require_str(canonical, "semantic_sha256")
    projection = project(canonical)
    projection = bind_semantic_hash(projection, semantic_sha256)

    output_path = Path(args.output)
    output_path.parent.mkdir(parents=True, exist_ok=True)
    body = json.dumps(projection, indent=2, sort_keys=True) + "\n"
    output_path.write_text(body, encoding="utf-8")

    if args.verify:
        if not verify(projection, canonical_path):
            fail_closed(
                "projection is not bound to the canonical artifact's "
                "current semantic hash"
            )

    stdout = (
        f"project_leamas_gate_summary: written={output_path} "
        f"checks={projection['checks_total']} "
        f"passed={projection['checks_passed']} "
        f"failed={projection['checks_failed']} "
        f"canonical_sha256={semantic_sha256}"
    )
    sys.stdout.write(stdout + "\n")
    return 0


if __name__ == "__main__":
    sys.exit(main(sys.argv[1:]))
