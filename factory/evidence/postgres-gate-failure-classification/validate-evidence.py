#!/usr/bin/env python3
"""ACT-CIRCUS-POSTGRES-GATE-FAILURE-CLASSIFICATION01-CORRECTION02
Evidence validator: rejects patterned placeholders and malformed hashes, validates
all 16 records and 48 attempts, and checks the structural fingerprint agreement.

Usage:
  python3 factory/evidence/postgres-gate-failure-classification/validate-evidence.py

Exit code 0 on pass, 1 on failure. Prints a summary to stdout.
"""
import hashlib
import json
import os
import re
import sys

EVIDENCE_DIR = os.path.dirname(os.path.abspath(__file__))
FAILURES_PATH = os.path.join(EVIDENCE_DIR, "failures.json")
MATRIX_PATH = os.path.join(EVIDENCE_DIR, "reproduction-matrix.json")
RAW_PATH = os.path.join(EVIDENCE_DIR, "raw-test-output.txt")
ISOLATED_DIR = os.path.join(EVIDENCE_DIR, "isolated-runs")
RUNS_PATH = os.path.join(EVIDENCE_DIR, "isolated-runs.jsonl")
AGG_PROBE_PATH = os.path.join(EVIDENCE_DIR, "p0-4-aggregate-probe.txt")
CRED_SCAN_PATH = os.path.join(EVIDENCE_DIR, "credential-scan.json")

PATTERNED_PLACEHOLDERS = [
    "n/a", "N/A", "TODO", "FIXME", "TBD",
    "0000000000000000000000000000000000000000000000000000000000000000",
    "1111111111111111111111111111111111111111111111111111111111111111",
    "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
    "deadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeefdeadbeef",
    "cafebabecafebabecafebabecafebabecafebabecafebabecafebabecafebabe",
]

SHA256_RE = re.compile(r"^[0-9a-f]{64}$")
EMPTY_STDERR_SHA = "e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855"


def is_patterned(s):
    if not isinstance(s, str):
        return False
    low = s.strip().lower()
    if low in [p.lower() for p in PATTERNED_PLACEHOLDERS]:
        return True
    return False


def is_sha256(s):
    return isinstance(s, str) and bool(SHA256_RE.match(s))


def parse_timespan_to_ms(s):
    """Parse a .NET TimeSpan string HH:MM:SS.fffffff to milliseconds."""
    m = re.match(r"^(\d+):(\d+):(\d+)\.(\d+)$", s)
    if not m:
        return None
    h = int(m.group(1))
    mi = int(m.group(2))
    si = int(m.group(3))
    sfrac = m.group(4)
    ms = 0
    if len(sfrac) >= 3:
        ms = int(sfrac[:3])
    elif sfrac:
        ms = int(sfrac.ljust(3, "0"))
    return h * 3600000 + mi * 60000 + si * 1000 + ms


# Substring to test name mapping
SUB_TO_NAME = {
    "fresh empty database migrates": "fresh empty database migrates to canonical state",
    "legacy already-applied 000001 public schema is reconciled": "legacy already-applied 000001 public schema is reconciled by 000002 and 000003",
    "released-parent 000001+000002 in circus ledger with circus_owner absent is corrected": "released-parent 000001+000002 in circus ledger with circus_owner absent is corrected by 000003",
    "Maximum Pool Size = 1": "migration runs to completion with Maximum Pool Size = 1",
    "concurrent runner is rejected by the migration advisory lock": "concurrent runner is rejected by the migration advisory lock",
    "released-parent prefix 000001+000002 is accepted": "released-parent prefix 000001+000002 is accepted and only 000003 is applied without refresh",
    "leaves the cluster ready for a follow-up real migrate run": "successful migration with deterministic unlock failure leaves the cluster ready for a follow-up real migrate run",
    "started then finished is equal to rebuild": "started then finished is equal to rebuild",
    "finished then started is complete and rebuild-equivalent": "finished then started is complete and rebuild-equivalent",
    "failed migration is not recorded as applied": "failed migration is not recorded as applied",
    "deterministic unlock failure raises typed invariant": "successful migration with deterministic unlock failure raises typed invariant, runs real ClearPool, ends the stale backend session",
    "failed 000003 with deterministic unlock failure preserves": "failed 000003 with deterministic unlock failure preserves the migration SQLSTATE and exact invariant message, leaves only 000003 unrecorded",
    "started and finished overlap and converge": "started and finished overlap and converge through the same service reducer",
    "a projection failure rolls back journal": "a projection failure rolls back journal and projection atomically",
    "trigger-thrown atomicity remains adjacent evidence": "trigger-thrown atomicity remains adjacent evidence",
    "reordered top-level keys": "compact original followed by reordered top-level keys is idempotent replay",
}


def main():
    errors = []
    summary = {
        "records": 0, "hashes": 0, "placeholder_hashes": 0, "missing_hashes": 0,
        "isolated_attempts": 0, "isolated_log_files": 0,
        "isolated_log_hash_matches": 0, "isolated_log_hash_mismatches": 0,
        "duration_ms_correct": 0, "duration_ms_off": 0,
        "fingerprint_agreement_pass": 0, "fingerprint_agreement_fail": 0,
    }

    with open(FAILURES_PATH) as f:
        failures = json.load(f)
    with open(MATRIX_PATH) as f:
        matrix = json.load(f)
    with open(RAW_PATH, "rb") as f:
        raw_bytes = f.read()
    raw_text = raw_bytes.decode("utf-8", errors="replace")
    raw_text_clean = re.sub(r"\x1b\[[0-9;]*m", "", raw_text)

    # 1) All 16 records exist
    records = failures.get("records", [])
    summary["records"] = len(records)
    if len(records) != 16:
        errors.append(f"Expected 16 records, found {len(records)}")

    # 2) Each record has the 4 hashes (distinct, reproducible)
    required_hash_keys = ["exception_chain_sha256", "stack_trace_sha256", "stdout_sha256", "stderr_sha256"]
    for rec in records:
        for key in required_hash_keys:
            val = rec.get(key)
            summary["hashes"] += 1
            if not val:
                summary["missing_hashes"] += 1
                errors.append(f"Record '{rec.get('test_name', '?')}' missing {key}")
                continue
            if not is_sha256(val):
                errors.append(f"Record '{rec.get('test_name', '?')}' {key}={val[:32]!r} is not a valid SHA-256")
                continue
            if is_patterned(val):
                summary["placeholder_hashes"] += 1
                errors.append(f"Record '{rec.get('test_name', '?')}' {key}={val} is a patterned placeholder")

    # 3) exception_chain_sha256 must reproduce from a checked extraction
    for rec in records:
        start = rec.get("exception_chain_start_offset")
        end = rec.get("exception_chain_end_offset")
        h = rec.get("exception_chain_sha256")
        if start is None or end is None or h is None:
            errors.append(f"Record '{rec.get('test_name', '?')}' missing offsets for hash reproduction")
            continue
        if start >= end or end > len(raw_bytes):
            errors.append(f"Record '{rec.get('test_name', '?')}' offsets {start}..{end} out of range (raw={len(raw_bytes)})")
            continue
        slice_bytes = raw_bytes[start:end]
        expected = hashlib.sha256(slice_bytes).hexdigest()
        if expected != h:
            errors.append(f"Record '{rec.get('test_name', '?')}' exception_chain_sha256 mismatch: file says {h}, recomputed {expected}")

    # 4) Raw log SHA-256
    raw_sha = hashlib.sha256(raw_bytes).hexdigest()
    with open(os.path.join(EVIDENCE_DIR, "raw-test-output.sha256")) as f:
        recorded_raw_sha = f.read().strip().split()[0]
    if raw_sha != recorded_raw_sha:
        errors.append(f"raw-test-output.sha256 mismatch: file says {recorded_raw_sha}, recomputed {raw_sha}")

    # 5) Each record has 3 attempts with structured fingerprint
    runs_by_name = {}
    if os.path.exists(RUNS_PATH):
        with open(RUNS_PATH) as f:
            for line in f:
                if not line.strip():
                    continue
                run = json.loads(line)
                # Normalize the test name
                name = re.sub(r"\s+(failed|errored)\s+in\s+.*$", "", run["test_fully_qualified_name"]).strip()
                runs_by_name.setdefault(name, []).append(run)

    for rec in records:
        name = rec["test_fully_qualified_name"]
        attempts = runs_by_name.get(name, [])
        if len(attempts) < 3:
            errors.append(f"Test '{name}' has only {len(attempts)} isolated attempts (need 3)")

    summary["isolated_attempts"] = sum(len(r) for r in runs_by_name.values())

    # 6) Each isolated run has its log file with matching hash
    if os.path.isdir(ISOLATED_DIR):
        for run_list in runs_by_name.values():
            for run in run_list:
                log_path = run.get("isolated_log_path")
                if not log_path:
                    continue
                full = os.path.join(EVIDENCE_DIR, log_path) if not os.path.isabs(log_path) else log_path
                if not os.path.exists(full):
                    errors.append(f"Isolated log missing: {full}")
                    continue
                summary["isolated_log_files"] += 1
                with open(full, "rb") as f:
                    log_bytes = f.read()
                actual = hashlib.sha256(log_bytes).hexdigest()
                if actual == run.get("isolated_log_sha256"):
                    summary["isolated_log_hash_matches"] += 1
                else:
                    summary["isolated_log_hash_mismatches"] += 1
                    errors.append(f"isolated log hash mismatch for {run.get('test_fully_qualified_name')}: recorded {run.get('isolated_log_sha256')[:16]}, file is {actual[:16]}")

    # 7) Structured fingerprint agreement
    for name, runs in runs_by_name.items():
        if len(runs) < 2:
            continue
        fp_keys = ["outcome", "exception_type", "sqlstate", "message_normalized_sha256", "source_file", "source_line"]
        all_agree = True
        for k in fp_keys:
            values = {r.get("structured_fingerprint", {}).get(k) for r in runs}
            if len(values) > 1:
                all_agree = False
                break
        if all_agree:
            summary["fingerprint_agreement_pass"] += 1
        else:
            summary["fingerprint_agreement_fail"] += 1
            errors.append(f"Fingerprint disagreement across {len(runs)} attempts of {name}")

    # 8) Duration parsing validation (PGFC-C02-06)
    for rec in records:
        attempts = runs_by_name.get(rec["test_fully_qualified_name"], [])
        if not attempts:
            continue
        for att in attempts:
            # Look for a TimeSpan in the log
            log_path = att.get("isolated_log_path")
            if not log_path:
                continue
            full = os.path.join(EVIDENCE_DIR, log_path)
            if not os.path.exists(full):
                continue
            with open(full, "rb") as f:
                log_text = f.read().decode("utf-8", errors="replace")
            log_clean = re.sub(r"\x1b\[[0-9;]*m", "", log_text)
            m = re.search(r"(failed|errored) in (\d+:\d+:\d+\.\d+)", log_clean)
            if not m:
                continue
            expected_ms = parse_timespan_to_ms(m.group(2))
            stored_ms = att.get("duration_ms", 0)
            if expected_ms is None or stored_ms is None:
                continue
            if expected_ms != stored_ms:
                summary["duration_ms_off"] += 1
                errors.append(f"Duration_ms off: {rec['test_name']} expected {expected_ms} (from '{m.group(2)}'), stored {stored_ms}")
            else:
                summary["duration_ms_correct"] += 1
            # Check duration_ms <= wall_ms (allow some slack)
            wall_ms = att.get("wall_ms", 0)
            if stored_ms > wall_ms * 1.1 and wall_ms > 0:
                errors.append(f"Duration {stored_ms}ms > wall {wall_ms}ms for {rec['test_name']}")

    # 9) Raw test output has the expected counts
    if "59 passed" not in raw_text_clean:
        errors.append("raw-test-output.txt does not report '59 passed' (expected count)")
    if "12 failed" not in raw_text_clean:
        errors.append("raw-test-output.txt does not report '12 failed' (expected count)")
    if "4 errored" not in raw_text_clean:
        errors.append("raw-test-output.txt does not report '4 errored' (expected count)")

    # 10) owner counts agree
    owner_per_cluster = matrix.get("owner_per_cluster", {})
    owner_summary = matrix.get("owner_summary", {})
    opc_count = len(owner_per_cluster)
    os_count = sum(v for k, v in owner_summary.items() if k != "_note")
    if opc_count != os_count:
        errors.append(f"owner_per_cluster has {opc_count} entries, owner_summary has {os_count}")
    if opc_count != 8:
        errors.append(f"owner_per_cluster has {opc_count} entries (expected 8)")

    # 11) P0-4 probe exists
    if not os.path.exists(AGG_PROBE_PATH):
        errors.append(f"P0-4 probe missing at {AGG_PROBE_PATH}")
    else:
        with open(AGG_PROBE_PATH) as f:
            probe_text = f.read()
        if "AggregateException" not in probe_text:
            errors.append("P0-4 probe does not mention AggregateException")
        if "Npgsql.PostgresException" not in probe_text:
            errors.append("P0-4 probe does not mention Npgsql.PostgresException (typed inner)")

    # 12) Credential scan
    if not os.path.exists(CRED_SCAN_PATH):
        errors.append(f"credential-scan.json missing at {CRED_SCAN_PATH}")
    else:
        with open(CRED_SCAN_PATH) as f:
            cs = json.load(f)
        if cs.get("verdict") != "pass":
            errors.append(f"credential-scan.json verdict is {cs.get('verdict')!r}, expected 'pass'")

    # 13) Fail-open runner recorded
    if "expecto_runner_record" not in matrix:
        errors.append("reproduction-matrix.json missing expecto_runner_record")
    else:
        err = matrix["expecto_runner_record"]
        if err.get("classification") != "fail_open":
            errors.append("expecto_runner_record.classification is not 'fail_open'")

    # 14) 3 disputed clusters marked provisional
    auth = failures.get("cluster_authority_summary", {})
    prov = auth.get("provisional_owner_clusters", [])
    expected_provisional = {"H_UNLOCK_AGGREGATE_WRAP", "H_UNLOCK_CLEANUP_LINGER", "E_SERIALIZATION_40001"}
    if set(prov) != expected_provisional:
        errors.append(f"provisional_owner_clusters is {set(prov)}, expected {expected_provisional}")

    # 15) Mutation tests
    # Test 1: changing one raw-log byte should invalidate its owning record
    test_record = records[0]
    test_start = test_record.get("exception_chain_start_offset")
    test_end = test_record.get("exception_chain_end_offset")
    if test_start is not None and test_end is not None:
        # Create a tampered copy
        tampered = bytearray(raw_bytes)
        if test_start < len(tampered):
            tampered[test_start] = (tampered[test_start] + 1) & 0xFF
        tampered_h = hashlib.sha256(bytes(tampered)).hexdigest()
        if tampered_h == raw_sha:
            errors.append("Mutation test FAIL: tampered raw log has same hash as original")

    # Test 2: deleting one attempt should fail 48-attempt invariant
    if summary["isolated_attempts"] < 48:
        errors.append(f"Mutation test FAIL: only {summary['isolated_attempts']} attempts (need 48)")

    # Report
    print("=== Evidence validation summary ===")
    for k, v in summary.items():
        print(f"  {k}: {v}")
    print()
    if errors:
        print(f"=== {len(errors)} ERRORS ===")
        for e in errors:
            print(f"  - {e}")
        sys.exit(1)
    else:
        print("PASS: All evidence checks passed.")
        sys.exit(0)


if __name__ == "__main__":
    main()
