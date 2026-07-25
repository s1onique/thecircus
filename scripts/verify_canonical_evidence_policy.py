#!/usr/bin/env python3
"""Static and mutation policy verifier for canonical evidence integration.

This verifier is intentionally independent of Make.  It proves that the native
provider, schema, registry, projection, and canonical ``gate`` wiring agree,
and mutation-tests the gate prerequisite.  It never regenerates evidence.

ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION03
"""

from __future__ import annotations

import argparse
import json
import re
import sys
from pathlib import Path

PROVIDER_NAME = "circus-canonical-evidence"
NATIVE_PATH = ".factory/canonical-evidence.json"
PROJECTION_PATH = ".factory/gate-summary.json"
PROJECTION_SCRIPT = "scripts/project_leamas_gate_summary.py"
VERIFY_POLICY_SCRIPT = "scripts/verify_canonical_evidence_policy.py"
PROVIDER_SOURCE = "tools/Circus.Tooling/CanonicalEvidence/Domain.fs"
DIGEST_EXEMPTION_RE = re.compile(
    r"(?m)^factory/evidence/digest-[^\n]*whitespace=.*(?:trailing-space|space-before-tab).*$"
)


class PolicyFailure(Exception):
    pass


def require(condition: bool, message: str) -> None:
    if not condition:
        raise PolicyFailure(message)


def read_text(root: Path, relative: str) -> str:
    path = root / relative
    try:
        return path.read_text(encoding="utf-8")
    except (OSError, UnicodeError) as exc:
        raise PolicyFailure(f"cannot read {relative}: {exc}") from exc


def read_json(root: Path, relative: str) -> dict:
    try:
        value = json.loads(read_text(root, relative))
    except json.JSONDecodeError as exc:
        raise PolicyFailure(f"invalid JSON in {relative}: {exc}") from exc
    require(isinstance(value, dict), f"{relative} root must be an object")
    return value


def provider_literal(domain_source: str) -> str:
    match = re.search(
        r'(?m)^let ProviderNameValue\s*:\s*string\s*=\s*"([^"]+)"\s*$',
        domain_source,
    )
    require(match is not None, "compiled ProviderNameValue literal not found")
    return match.group(1)


def schema_field_provider(schema: dict) -> str:
    value = schema.get("field_semantics", {}).get("provider_name")
    require(isinstance(value, str), "schema.field_semantics.provider_name must be a string")
    match = re.fullmatch(r"string; must equal ([a-z0-9-]+)", value)
    require(match is not None, "schema.field_semantics.provider_name must use exact equality syntax")
    return match.group(1)


def logical_make_lines(makefile: str) -> list[str]:
    result: list[str] = []
    pending = ""
    for physical in makefile.splitlines():
        stripped = physical.rstrip()
        if pending:
            pending += stripped.lstrip()
        else:
            pending = stripped
        if pending.endswith("\\"):
            pending = pending[:-1] + " "
        else:
            result.append(pending)
            pending = ""
    if pending:
        result.append(pending)
    return result


def target_prerequisites(makefile: str, target: str) -> list[str]:
    pattern = re.compile(rf"^{re.escape(target)}\s*:(.*)$")
    for line in logical_make_lines(makefile):
        match = pattern.match(line)
        if match:
            return match.group(1).split()
    raise PolicyFailure(f"Make target not found: {target}")


def target_recipe(makefile: str, target: str) -> str:
    lines = makefile.splitlines()
    start = None
    for index, line in enumerate(lines):
        if re.match(rf"^{re.escape(target)}\s*:", line):
            start = index + 1
            break
    require(start is not None, f"Make target not found: {target}")
    recipe: list[str] = []
    for line in lines[start:]:
        if line.startswith("\t"):
            recipe.append(line[1:])
            continue
        if not line.strip() or line.lstrip().startswith("#"):
            continue
        break
    return "\n".join(recipe)


def verify_gate_policy(makefile: str) -> None:
    prerequisites = target_prerequisites(makefile, "gate")
    require(
        prerequisites.count("verify-canonical-evidence") == 1,
        "gate must contain verify-canonical-evidence exactly once as a prerequisite",
    )
    recipe = target_recipe(makefile, "gate")
    require("=== Native gate passed ===" in recipe, "gate final PASS line is missing")
    verify_recipe = target_recipe(makefile, "verify-canonical-evidence")
    require("canonical-evidence verify" in verify_recipe, "verify target does not invoke provider verification")
    require("CANONICAL_EVIDENCE_PROJECTION" in verify_recipe, "verify target does not verify the Leamas projection")
    require("CANONICAL_EVIDENCE_POLICY" in verify_recipe, "verify target does not run integration policy verification")
    require("regenerate" not in verify_recipe, "verify target must not regenerate evidence")
    canonical_recipe = target_recipe(makefile, "canonical-evidence")
    require("canonical-evidence regenerate" in canonical_recipe, "canonical-evidence target does not regenerate native evidence")
    require("CANONICAL_EVIDENCE_PROJECTION" in canonical_recipe, "canonical-evidence target does not generate the projection")


def mutation_test_gate(makefile: str) -> None:
    marker = "verify-canonical-evidence"
    prerequisites = target_prerequisites(makefile, "gate")
    require(marker in prerequisites, "cannot run mutation test: verification prerequisite absent")
    gate_pattern = re.compile(r"(?m)^(gate\s*:[^\n]*)$")
    match = gate_pattern.search(makefile)
    require(match is not None, "cannot locate gate declaration for mutation test")
    mutated_line = re.sub(r"(?:^|\s+)verify-canonical-evidence(?=\s|$)", " ", match.group(1))
    mutated = makefile[: match.start()] + mutated_line + makefile[match.end() :]
    try:
        verify_gate_policy(mutated)
    except PolicyFailure:
        return
    raise PolicyFailure("mutation test failed: removing verify-canonical-evidence was not detected")


def verify(root: Path) -> None:
    registry = read_json(root, ".factory/evidence-provider-registry.json")
    schema = read_json(root, ".factory/evidence-provider-schema.json")
    artifact = read_json(root, NATIVE_PATH)
    domain = read_text(root, PROVIDER_SOURCE)
    makefile = read_text(root, "Makefile")
    attributes = read_text(root, ".gitattributes")
    projection_script = read_text(root, PROJECTION_SCRIPT)
    policy_source = read_text(root, VERIFY_POLICY_SCRIPT)

    names = {
        "registry.provider_name": registry.get("provider_name"),
        "schema.provider": schema.get("provider"),
        "schema.field_semantics.provider_name": schema_field_provider(schema),
        "artifact.provider_name": artifact.get("provider_name"),
        "compiled ProviderNameValue": provider_literal(domain),
    }
    for label, value in names.items():
        require(value == PROVIDER_NAME, f"{label} mismatch: expected {PROVIDER_NAME}, got {value!r}")

    require(registry.get("schema_path") == ".factory/evidence-provider-schema.json", "registry schema_path mismatch")
    require(registry.get("output_path") == NATIVE_PATH, "registry output_path mismatch")
    require(registry.get("compatibility_projection_path") == PROJECTION_PATH, "registry projection path mismatch")
    require(registry.get("projection_command") == f"python3 {PROJECTION_SCRIPT} --canonical {NATIVE_PATH} --output {PROJECTION_PATH}", "registry projection command mismatch")
    require(registry.get("projection_validator_command") == f"python3 {PROJECTION_SCRIPT} --canonical {NATIVE_PATH} --output {PROJECTION_PATH} --verify-only", "registry projection validator mismatch")
    require(registry.get("required_gate") is True, "registry required_gate must be true")
    require(schema.get("schema_name") == "canonical-evidence-v1", "schema_name mismatch")
    require(schema.get("provider") == PROVIDER_NAME, "schema provider mismatch")
    require(set(registry.get("checks", [])) == set(schema.get("supported_check_ids", [])), "registry/schema check catalogs differ")
    require(set(registry.get("checks", [])) == {check.get("id") for check in artifact.get("checks", [])}, "registry/artifact check catalogs differ")

    verify_gate_policy(makefile)
    mutation_test_gate(makefile)

    require(not DIGEST_EXEMPTION_RE.search(attributes), "digest-specific whitespace suppression remains")
    require(".factory/canonical-evidence.json" in projection_script, "projection script does not select native artifact")
    require('default=".factory/gate-summary.json"' in projection_script, "projection script does not write Leamas fixed path")
    require("--verify-only" in projection_script, "projection script lacks fail-closed verification mode")

    forbidden_seam_tokens = (
        "setGitExecutable",
        "resetGitExecutable",
    )
    canonical_sources = "\n".join(
        read_text(root, str(path.relative_to(root)))
        for path in sorted((root / "tools/Circus.Tooling/CanonicalEvidence").glob("*.fs"))
    )
    for token in forbidden_seam_tokens:
        require(token not in canonical_sources, f"CanonicalEvidence production source references Git executable mutator: {token}")

    require("mutation_test_gate" in policy_source, "policy verifier lost its gate mutation test")


def main(argv: list[str]) -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--repo-root", default=".")
    args = parser.parse_args(argv)
    root = Path(args.repo_root).resolve()
    try:
        verify(root)
    except PolicyFailure as exc:
        print(f"canonical-evidence policy: FAIL ({exc})", file=sys.stderr)
        return 1
    print("canonical-evidence policy: PASS (provider/schema/registry/projection/gate agreement; mutation detected)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))
