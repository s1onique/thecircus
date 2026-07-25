#!/usr/bin/env python3
"""Generate or verify the strict Leamas v1 projection of canonical evidence.

The repository-owned provider writes its native, deterministic artifact to
``.factory/canonical-evidence.json``.  Leamas has a fixed consumer path,
``.factory/gate-summary.json``; this program is the only writer of that path.
The projection carries the native semantic hash in every evidence reference,
which keeps one authority and one derived representation.

ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03
"""

from __future__ import annotations

import argparse
import hashlib
import json
import os
import re
import sys
import tempfile
from datetime import datetime, timezone
from pathlib import Path
from typing import NoReturn

PROVIDER_NAME = "circus-canonical-evidence"
EMPTY_SHA256 = hashlib.sha256(b"").hexdigest()
OID_RE = re.compile(r"(?:[0-9a-f]{40}|[0-9a-f]{64})\Z")
SHA256_RE = re.compile(r"[0-9a-f]{64}\Z")
STATUS_MAP = {"pass": "pass", "fail": "fail", "unavailable": "unavailable"}
CANONICAL_FIELDS = {
    "schema_version",
    "provider_name",
    "provider_version",
    "tested_commit_oid",
    "tested_tree_oid",
    "object_format",
    "active_scope_act_id",
    "active_scope_pointer_blob_oid",
    "scope_declaration_path",
    "declaration_blob_oid",
    "baseline_commit_oid",
    "checks",
    "overall_status",
    "semantic_sha256",
}
CHECK_FIELDS = {
    "id",
    "command_argv",
    "working_directory",
    "duration_ms",
    "exit_code",
    "status",
    "stdout_sha256",
    "stderr_sha256",
    "failure_kind",
}
PROJECTION_FIELDS = {"schema_version", "generated_at", "tool", "overall_status", "checks"}
PROJECTED_CHECK_FIELDS = {"name", "status", "duration_ms", "evidence"}


def fail(message: str) -> NoReturn:
    print(f"project_leamas_gate_summary: FAIL ({message})", file=sys.stderr)
    raise SystemExit(2)


def read_object(path: Path, label: str) -> dict:
    if not path.is_file():
        fail(f"{label} not found: {path}")
    try:
        raw = path.read_bytes()
    except OSError as exc:
        fail(f"cannot read {label}: {exc}")
    try:
        value = json.loads(raw.decode("utf-8"))
    except (UnicodeDecodeError, json.JSONDecodeError) as exc:
        fail(f"{label} is not strict UTF-8 JSON: {exc}")
    if not isinstance(value, dict):
        fail(f"{label} root is not an object")
    return value


def exact_fields(value: dict, expected: set[str], label: str) -> None:
    actual = set(value)
    missing = sorted(expected - actual)
    unknown = sorted(actual - expected)
    if missing:
        fail(f"{label} missing fields: {', '.join(missing)}")
    if unknown:
        fail(f"{label} unknown fields: {', '.join(unknown)}")


def required_string(value: dict, key: str, label: str) -> str:
    result = value.get(key)
    if not isinstance(result, str) or not result.strip():
        fail(f"{label}.{key} must be a non-empty string")
    return result


def required_integer(value: dict, key: str, label: str) -> int:
    result = value.get(key)
    if isinstance(result, bool) or not isinstance(result, int):
        fail(f"{label}.{key} must be an integer")
    return result


def nullable_sha(value: object, label: str) -> str | None:
    if value is None:
        return None
    if not isinstance(value, str) or not SHA256_RE.fullmatch(value):
        fail(f"{label} must be null or a lowercase SHA-256")
    return value


def canonical_form(canonical: dict) -> bytes:
    body = {key: canonical[key] for key in canonical if key != "semantic_sha256"}
    return json.dumps(body, separators=(",", ":"), ensure_ascii=False).encode("utf-8")


def validate_canonical(canonical: dict) -> None:
    exact_fields(canonical, CANONICAL_FIELDS, "canonical artifact")
    if required_integer(canonical, "schema_version", "canonical artifact") != 1:
        fail("canonical artifact schema_version must equal 1")
    if required_string(canonical, "provider_name", "canonical artifact") != PROVIDER_NAME:
        fail(f"canonical artifact provider_name must equal {PROVIDER_NAME}")
    required_string(canonical, "provider_version", "canonical artifact")
    object_format = required_string(canonical, "object_format", "canonical artifact")
    if object_format not in {"sha1", "sha256"}:
        fail("canonical artifact object_format must be sha1 or sha256")
    expected_width = 40 if object_format == "sha1" else 64
    for field in ("tested_commit_oid", "tested_tree_oid"):
        oid = required_string(canonical, field, "canonical artifact")
        if len(oid) != expected_width or not OID_RE.fullmatch(oid):
            fail(f"canonical artifact {field} is not a full {object_format} OID")
    semantic_sha = required_string(canonical, "semantic_sha256", "canonical artifact")
    if not SHA256_RE.fullmatch(semantic_sha):
        fail("canonical artifact semantic_sha256 is not lowercase SHA-256")
    recomputed = hashlib.sha256(canonical_form(canonical)).hexdigest()
    if semantic_sha != recomputed:
        fail(f"canonical artifact semantic hash mismatch: expected {recomputed}, got {semantic_sha}")
    overall = required_string(canonical, "overall_status", "canonical artifact")
    if overall not in STATUS_MAP:
        fail(f"canonical artifact has unknown overall_status: {overall}")
    checks = canonical.get("checks")
    if not isinstance(checks, list) or not checks:
        fail("canonical artifact checks must be a non-empty array")
    names: set[str] = set()
    derived = "pass"
    for index, check in enumerate(checks):
        label = f"canonical artifact checks[{index}]"
        if not isinstance(check, dict):
            fail(f"{label} is not an object")
        exact_fields(check, CHECK_FIELDS, label)
        name = required_string(check, "id", label)
        if name in names:
            fail(f"canonical artifact has duplicate check name: {name}")
        names.add(name)
        argv = check.get("command_argv")
        if not isinstance(argv, list) or not argv or any(not isinstance(item, str) or not item for item in argv):
            fail(f"{label}.command_argv must be a non-empty string array")
        required_string(check, "working_directory", label)
        duration = required_integer(check, "duration_ms", label)
        if duration < 0:
            fail(f"{label}.duration_ms must be non-negative")
        status = required_string(check, "status", label)
        if status not in STATUS_MAP:
            fail(f"{label}.status is unknown: {status}")
        exit_code = check.get("exit_code")
        if exit_code is not None and (isinstance(exit_code, bool) or not isinstance(exit_code, int)):
            fail(f"{label}.exit_code must be an integer or null")
        stdout_sha = nullable_sha(check.get("stdout_sha256"), f"{label}.stdout_sha256")
        stderr_sha = nullable_sha(check.get("stderr_sha256"), f"{label}.stderr_sha256")
        if status == "pass" and (exit_code != 0 or not stdout_sha or not stderr_sha):
            fail(f"{label} passing evidence must contain exit_code 0 and both output hashes")
        if status != "pass":
            derived = "fail"
    if overall != derived:
        fail(f"canonical artifact overall_status mismatch: expected {derived}, got {overall}")


def evidence_reference(canonical: dict, check: dict) -> str:
    stdout_sha = check.get("stdout_sha256") or EMPTY_SHA256
    stderr_sha = check.get("stderr_sha256") or EMPTY_SHA256
    exit_code = "null" if check.get("exit_code") is None else str(check["exit_code"])
    return (
        "canonical=.factory/canonical-evidence.json"
        f";semantic_sha256={canonical['semantic_sha256']}"
        f";stdout_sha256={stdout_sha};stderr_sha256={stderr_sha};exit_code={exit_code}"
        f";tested_commit_oid={canonical['tested_commit_oid']}"
        f";tested_tree_oid={canonical['tested_tree_oid']}"
    )


def project(canonical: dict, generated_at: str) -> dict:
    validate_canonical(canonical)
    return {
        "schema_version": 1,
        "generated_at": generated_at,
        "tool": (
            f"{PROVIDER_NAME}/{canonical['provider_version']}"
            f";canonical=.factory/canonical-evidence.json"
            f";semantic_sha256={canonical['semantic_sha256']}"
            f";tested_commit_oid={canonical['tested_commit_oid']}"
            f";tested_tree_oid={canonical['tested_tree_oid']}"
        ),
        "overall_status": STATUS_MAP[canonical["overall_status"]],
        "checks": [
            {
                "name": check["id"],
                "status": STATUS_MAP[check["status"]],
                "duration_ms": check["duration_ms"],
                "evidence": evidence_reference(canonical, check),
            }
            for check in canonical["checks"]
        ],
    }


def validate_projection(projection: dict, canonical: dict) -> None:
    exact_fields(projection, PROJECTION_FIELDS, "projection")
    if required_integer(projection, "schema_version", "projection") != 1:
        fail("projection schema_version must equal 1")
    generated_at = required_string(projection, "generated_at", "projection")
    try:
        datetime.fromisoformat(generated_at.replace("Z", "+00:00"))
    except ValueError:
        fail("projection generated_at is not RFC 3339")
    tool = required_string(projection, "tool", "projection")
    required_tokens = (
        PROVIDER_NAME,
        canonical["semantic_sha256"],
        canonical["tested_commit_oid"],
        canonical["tested_tree_oid"],
    )
    if any(token not in tool for token in required_tokens):
        fail("projection tool binding is stale or incomplete")
    if projection.get("overall_status") != canonical["overall_status"]:
        fail("projection overall_status does not match canonical artifact")
    projected_checks = projection.get("checks")
    canonical_checks = canonical["checks"]
    if not isinstance(projected_checks, list) or len(projected_checks) != len(canonical_checks):
        fail("projection check count does not match canonical artifact")
    names: set[str] = set()
    for index, (actual, source) in enumerate(zip(projected_checks, canonical_checks)):
        label = f"projection checks[{index}]"
        if not isinstance(actual, dict):
            fail(f"{label} is not an object")
        exact_fields(actual, PROJECTED_CHECK_FIELDS, label)
        name = required_string(actual, "name", label)
        if name in names:
            fail(f"projection has duplicate check name: {name}")
        names.add(name)
        if name != source["id"]:
            fail(f"{label}.name does not match canonical check")
        if actual.get("status") != source["status"]:
            fail(f"{label}.status does not match canonical check")
        if required_integer(actual, "duration_ms", label) != source["duration_ms"]:
            fail(f"{label}.duration_ms does not match canonical check")
        evidence = required_string(actual, "evidence", label)
        expected = evidence_reference(canonical, source)
        if evidence != expected:
            fail(f"{label}.evidence semantic binding mismatch")


def atomic_write(path: Path, body: bytes) -> None:
    path.parent.mkdir(parents=True, exist_ok=True)
    fd, temporary = tempfile.mkstemp(prefix=path.name + ".tmp.", dir=path.parent)
    temp_path = Path(temporary)
    try:
        with os.fdopen(fd, "wb") as handle:
            handle.write(body)
            handle.flush()
            os.fsync(handle.fileno())
        os.replace(temp_path, path)
    except BaseException:
        temp_path.unlink(missing_ok=True)
        raise


def default_timestamp(canonical_path: Path) -> str:
    return datetime.fromtimestamp(canonical_path.stat().st_mtime, timezone.utc).isoformat().replace("+00:00", "Z")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--canonical", default=".factory/canonical-evidence.json")
    parser.add_argument("--output", default=".factory/gate-summary.json")
    parser.add_argument("--generated-at", help="RFC 3339 timestamp; defaults to canonical artifact mtime")
    parser.add_argument("--verify-only", action="store_true", help="verify the existing projection without writing")
    args = parser.parse_args(argv)

    canonical_path = Path(args.canonical)
    output_path = Path(args.output)
    canonical = read_object(canonical_path, "canonical artifact")
    validate_canonical(canonical)

    if args.verify_only:
        projection = read_object(output_path, "projection")
        validate_projection(projection, canonical)
        print(
            "project_leamas_gate_summary: PASS "
            f"canonical={canonical_path} projection={output_path} "
            f"checks={len(projection['checks'])} semantic_sha256={canonical['semantic_sha256']}"
        )
        return 0

    generated_at = args.generated_at or default_timestamp(canonical_path)
    projection = project(canonical, generated_at)
    validate_projection(projection, canonical)
    body = (json.dumps(projection, indent=2, ensure_ascii=False) + "\n").encode("utf-8")
    atomic_write(output_path, body)
    persisted = read_object(output_path, "persisted projection")
    validate_projection(persisted, canonical)
    print(
        "project_leamas_gate_summary: written="
        f"{output_path} source={canonical_path} checks={len(projection['checks'])} "
        f"semantic_sha256={canonical['semantic_sha256']}"
    )
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
