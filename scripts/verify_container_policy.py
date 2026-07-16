#!/usr/bin/env python3
"""Repository-owned static policy checks for Circus container publication."""
from __future__ import annotations

import json
import os
import re
import subprocess
import sys
from pathlib import Path

try:
    import yaml
except ImportError as exc:  # pragma: no cover - dependency error is actionable
    raise SystemExit("verify_container_policy.py requires PyYAML 6.x") from exc

ROOT = Path(__file__).resolve().parents[1]


def fail(message: str) -> None:
    raise AssertionError(message)


def read(relative: str) -> str:
    path = ROOT / relative
    if not path.is_file():
        fail(f"missing required file: {relative}")
    return path.read_text(encoding="utf-8")


def script_name(path: str) -> str:
    return path.rsplit("/", 1)[-1]


def workflow(relative: str) -> tuple[dict, str]:
    text = read(relative)
    parsed = yaml.safe_load(text)
    if not isinstance(parsed, dict):
        fail(f"workflow is not a YAML mapping: {relative}")
    return parsed, text


def workflow_triggers(parsed: dict) -> dict:
    # YAML 1.1 loaders treat the GitHub key `on` as boolean True.
    value = parsed.get("on", parsed.get(True))
    if not isinstance(value, dict):
        fail("workflow trigger block is missing or is not a mapping")
    return value


def main() -> int:
    backend = read("Dockerfile.backend")
    frontend = read("Dockerfile.frontend")
    dockerignore = read(".dockerignore")
    nginx = read("docker/frontend/nginx.conf")
    smoke = read("scripts/container-smoke.sh")
    verify = read("scripts/verify-published-image.sh")
    verify_build_image_text = read("scripts/ci/verify_build_image.sh")
    metadata = read(".github/scripts/harbor-metadata.sh")
    ca = read(".github/scripts/install-spbnix-harbor-ca.sh")
    build_script = read("scripts/ci/build_image.sh")
    publish_script = read("scripts/ci/publish_image.sh")
    wire_buildx_builder_text = read("scripts/ci/wire_buildx_builder.sh")
    reusable, reusable_text = workflow(".github/workflows/harbor-build-image.yml")
    top, top_text = workflow(".github/workflows/harbor.yml")
    shell_test = read("tests/ci/test_build_publish_shell.sh")

    required_files = [
        "Dockerfile.backend",
        "Dockerfile.frontend",
        ".dockerignore",
        "docker/frontend/nginx.conf",
        ".github/workflows/harbor.yml",
        ".github/workflows/harbor-build-image.yml",
        ".github/scripts/install-spbnix-harbor-ca.sh",
        "docs/harbor-publishing.md",
        "scripts/ci/build_image.sh",
        "scripts/ci/publish_image.sh",
        "scripts/ci/verify_build_image.sh",
        "scripts/ci/wire_buildx_builder.sh",
        "tests/ci/test_build_publish_shell.sh",
        "tests/ci/test_action_pin_mutation.sh",
        "tests/ci/test_gate_summary_acceptance.sh",
    ]
    for relative in required_files:
        if not (ROOT / relative).is_file():
            fail(f"required file missing: {relative}")

    # The shell scripts must be executable so the reusable workflow can call
    # them by name.  Missing or incorrect permissions is a CI failure, not
    # just a local inconvenience.
    for relative in (
        "scripts/ci/build_image.sh",
        "scripts/ci/publish_image.sh",
        "scripts/ci/verify_build_image.sh",
        "scripts/ci/wire_buildx_builder.sh",
        "tests/ci/test_build_publish_shell.sh",
        "tests/ci/test_action_pin_mutation.sh",
        "tests/ci/test_gate_summary_acceptance.sh",
    ):
        path = ROOT / relative
        st = path.stat()
        if not (st.st_mode & 0o111):
            fail(f"required shell script is not executable: {relative}")

    top_on = workflow_triggers(top)
    for trigger in ("pull_request", "push", "workflow_dispatch"):
        if trigger not in top_on:
            fail(f"harbor.yml lacks trigger: {trigger}")
    push_on = top_on["push"]
    if "main" not in push_on.get("branches", []):
        fail("push publication is not restricted to main")
    if "v*" not in push_on.get("tags", []):
        fail("version-tag publication trigger is missing")
    if top.get("permissions", {}).get("contents") != "read":
        fail("top-level workflow must grant only contents: read")
    if top.get("concurrency", {}).get("group") != "circus-harbor-${{ github.ref }}":
        fail("workflow concurrency is not reference-scoped")

    call_inputs = reusable.get("on", reusable.get(True)).get("workflow_call", {}).get("inputs", {})
    for input_name in (
        "image_name",
        "dockerfile",
        "context",
        "cache_name",
        "push",
        "platform",
        "smoke_test_kind",
    ):
        if input_name not in call_inputs:
            fail(f"reusable workflow input missing: {input_name}")
    if call_inputs["push"].get("type") != "boolean":
        fail("reusable push input must be boolean")

    if "pull_request_target" in top_text or "pull_request_target" in reusable_text:
        fail("pull_request_target is forbidden")
    if "spbnix-k8s-docker" not in top_text or "runner" not in reusable_text:
        fail("trusted publication runner label is missing")
    if "harbor-pve1.spbnix.local/circus/${{ inputs.image_name }}" not in reusable_text:
        fail("Harbor repository contract is missing")
    for image_name in ("circus-backend", "circus-frontend"):
        if f"image_name: {image_name}" not in top_text:
            fail(f"top-level image contract is missing: {image_name}")
    if "image_name: circus-backend" not in top_text or "image_name: circus-frontend" not in top_text:
        fail("both image_name invocations must be present in harbor.yml")

    if "HARBOR_PASSWORD" not in reusable_text or "--password-stdin" not in reusable_text:
        fail("Harbor password must be consumed through password-stdin")
    forbidden_tls = ("--insecure", "tls-verify=false", "insecure=true", "GODEBUG=x509ignoreCN=0")
    for marker in forbidden_tls:
        if marker in (reusable_text + ca):
            fail(f"TLS verification bypass is forbidden: {marker}")
    if "SPBNIX_CA_CERT_PEM" not in ca or "buildkitd.toml" not in ca:
        fail("CA script does not consume the CA secret and create BuildKit config")
    if "[registry.\"${HARBOR_HOST}\"]" not in ca or "ca = [\"${ca_file}\"]" not in ca:
        fail("BuildKit registry CA mapping is missing")
    if "SPBNIX_CA_CERT_PEM" not in reusable_text:
        fail("reusable workflow does not declare SPBNIX_CA_CERT_PEM")

    if "CACHE_REF: harbor-pve1.spbnix.local/circus/cache/${{ inputs.cache_name }}:buildcache" not in reusable_text:
        fail("Harbor cache reference template is missing")
    cache_refs_in_top: list[str] = []
    for cache_name in ("circus-backend", "circus-frontend"):
        if f"cache_name: {cache_name}" not in top_text:
            fail(f"image-specific cache reference is missing: {cache_name}")
        cache_refs_in_top.append(f"harbor-pve1.spbnix.local/circus/cache/{cache_name}:buildcache")
    if cache_refs_in_top[0] == cache_refs_in_top[1]:
        fail("backend and frontend cache references must remain distinct")

    # The build/publish logic now lives in scripts/ci/build_image.sh and
    # scripts/ci/publish_image.sh.  The reusable workflow delegates to those
    # scripts, so the cache arguments are checked there instead of in the
    # workflow YAML itself.
    if "cache-from" not in build_script or "cache-to" not in publish_script or "mode=max" not in publish_script:
        fail("registry cache import/export is incomplete")
    if "oci-mediatypes=true,image-manifest=true" not in publish_script:
        fail("Harbor cache export is missing the OCI image-manifest compatibility options")
    # The shell unit tests must cover both publish=true and publish=false so
    # the reusable shell logic is not silently broken.
    if "PUBLISH=true" not in shell_test or "PUBLISH=false" not in shell_test:
        fail("shell test suite does not exercise both PUBLISH branches")
    # The reusable workflow delegates the cache and publication gating to
    # scripts/ci/build_image.sh and scripts/ci/publish_image.sh, both of
    # which read the PUBLISH env variable that the workflow forwards from
    # steps.metadata.outputs.publish.  The check therefore covers the
    # forwarder in the workflow plus the guard in the shell scripts.
    if "PUBLISH: ${{ steps.metadata.outputs.publish }}" not in reusable_text:
        fail("reusable workflow must forward the PUBLISH env to the build/publish scripts")
    for script in (build_script, publish_script):
        # Either `if [[ "$PUBLISH" == "true" ]]` (build) or
        # `if [[ "${PUBLISH:-false}" != "true" ]]` (publish) is acceptable.
        if "PUBLISH" not in script:
            fail("build/publish shell scripts do not gate behavior on PUBLISH")
        if '== "true"' not in script and '!= "true"' not in script:
            fail("build/publish shell scripts do not compare PUBLISH to 'true'")

    if "GITHUB_SHA" not in metadata or "local-${sha}" not in metadata:
        fail("full commit SHA is not part of the immutable tag contract")
    if "latest" not in metadata or "refs/heads/main" not in metadata:
        fail("latest tag is not present under the main-branch-only contract")
    if metadata.count("latest") != 1:
        fail("latest must be emitted only once by the metadata contract")
    for release_tag in ("v${release}", "${release}", "${major}.${minor}", "${major}"):
        if release_tag not in metadata:
            fail(f"release tag missing: {release_tag}")
    if "steps.metadata.outputs.publish == 'true'" not in reusable_text:
        fail("trusted publication steps are not guarded by metadata publication")

    # Frontend CA contract: the Dockerfile uses the documented BuildKit
    # secret `id=spbnix-ca` mounted at /run/secrets/spbnix-ca, never
    # copies the CA into a layer, and points NODE_EXTRA_CA_CERTS at the
    # secret mount and SSL_CERT_FILE at a /tmp combined bundle.  Docker's
    # build-secret mechanism exposes the secret only to the relevant RUN
    # instruction rather than baking it into the image or metadata.
    #
    # mode=max exports intermediate build stages into the Harbor cache;
    # if the CA were copied into a build layer the cache would carry it,
    # so the contract requires the secret-only mount and an explicit
    # cleanup of the /tmp combined bundle at the end of each RUN.
    required_ca_markers = (
        "id=spbnix-ca,target=/run/secrets/spbnix-ca",
        "if [ -s /run/secrets/spbnix-ca ]",
        "/etc/ssl/certs/ca-certificates.crt",
        "/tmp/circus-ca-bundle.pem",
        "NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca",
        "SSL_CERT_FILE=/tmp/circus-ca-bundle.pem",
        "rm -f /tmp/circus-ca-bundle.pem",
    )
    for marker in required_ca_markers:
        if marker not in frontend:
            fail(f"frontend Dockerfile is missing the required CA marker: {marker!r}")
    if "update-ca-certificates" in frontend:
        fail("frontend Dockerfile mutates the system CA store; that change would persist in mode=max cache")
    # The previous revision of the policy also forbade `cp /tmp/spbnix-ca.crt`
    # to keep the secret out of any layer.  The new contract uses the
    # combined bundle at /tmp/circus-ca-bundle.pem instead and removes it
    # explicitly at the end of each RUN, so that exact-string check is
    # intentionally retired.  The secret must still never be written to a
    # layer; we enforce that by checking the cleanup marker instead.
    if "/tmp/spbnix-ca.crt" in frontend:
        fail("frontend Dockerfile references the obsolete /tmp/spbnix-ca.crt path; use /tmp/circus-ca-bundle.pem")
    # The frontend install layer must explicitly run the Elm installer
    # script (`node node_modules/elm/install.js`) after the
    # script-suppressed `npm ci --ignore-scripts`.  The `elm@0.19.2-0`
    # npm package ships its platform-specific binary through the
    # `install` lifecycle script, so disabling all lifecycle scripts
    # leaves the layer with no Elm CLI.  The Dockerfile must also
    # assert the freshly-installed Elm CLI reports `0.19.2` (or
    # `Elm 0.19.2`) before the build layer is allowed to proceed.
    elm_install_markers = (
        "npm ci --ignore-scripts",
        "node node_modules/elm/install.js",
        "./node_modules/.bin/elm --version",
        "Elm ${ELM_VERSION}",
        "0.19.2|Elm\\ 0.19.2",
    )
    for marker in elm_install_markers:
        if marker not in frontend:
            fail(f"frontend Dockerfile is missing the required Elm installer marker: {marker!r}")

    if not re.search(r"USER\s+\d+:\d+", backend):
        fail("backend final image lacks an explicit numeric non-root user")
    if not re.search(r"USER\s+\d+:\d+", frontend):
        fail("frontend final image lacks an explicit numeric non-root user")
    if "ASPNETCORE_HTTP_PORTS=8080" not in backend or "EXPOSE 8080" not in backend:
        fail("backend 8080 contract is incomplete")
    if "listen 8080" not in nginx or "EXPOSE 8080" not in frontend:
        fail("frontend 8080 contract is incomplete")
    for marker in ("/health/live", "GET /health/live", "health/live"):
        if marker not in smoke:
            fail("backend smoke test does not check /health/live")
    for marker in ("/healthz", "<title>The Circus</title>", "GET /"):
        if marker not in smoke:
            fail(f"frontend smoke contract is missing: {marker}")

    # The post-push digest verification moved to scripts/ci/verify_build_image.sh.
    if 'docker pull "${IMAGE_REPOSITORY}@${digest}"' not in verify_build_image_text:
        fail("post-push verification does not pull by registry digest")
    if 'docker image inspect "${IMAGE_REPOSITORY}@${digest}"' not in verify_build_image_text:
        fail("post-push verification does not inspect the digest reference")
    if "linux/amd64" not in reusable_text or "architecture" not in verify:
        fail("amd64 post-publication verification is incomplete")

    # Workflow-to-script seam checks.  These are the integration points the
    # executable shell test cannot observe directly, so we assert the
    # workflow YAML forwards every variable the scripts require.
    seam_checks = (
        ("Create configured BuildKit builder", "PUBLISH: ${{ steps.metadata.outputs.publish }}"),
        ("Create configured BuildKit builder", "BUILDER_NAME: circus-${{ github.run_id }}"),
        ("Create configured BuildKit builder", "BUILDKITD_CONFIG: ${{ steps.harbor_ca"),
        ("Build testable image with repository cache", "BUILDER_NAME: ${{ steps.builder.outputs.builder }}"),
        ("Publish tested revision with provenance and SBOM attempt", "PUBLISH: ${{ steps.metadata.outputs.publish }}"),
        ("Publish tested revision with provenance and SBOM attempt", "BUILDER_NAME: ${{ steps.builder.outputs.builder }}"),
    )
    for step_name, snippet in seam_checks:
        if f"name: {step_name}" not in reusable_text:
            fail(f"reusable workflow is missing the {step_name!r} step")
        if snippet not in reusable_text:
            fail(f"reusable workflow {step_name!r} does not forward {snippet!r}")

    # The shell scripts must surface their outputs via $GITHUB_OUTPUT so the
    # reusable workflow can reference them as step outputs.
    for script_text, label in (
        (build_script, "scripts/ci/build_image.sh"),
        (publish_script, "scripts/ci/publish_image.sh"),
        (wire_buildx_builder_text, "scripts/ci/wire_buildx_builder.sh"),
        (verify_build_image_text, "scripts/ci/verify_build_image.sh"),
    ):
        if "GITHUB_OUTPUT" not in script_text:
            fail(f"{label} must use $GITHUB_OUTPUT for step outputs")
    # The workflow summary must consume each script's outputs.
    for output_ref in (
        "steps.builder.outputs.builder",
        "steps.builder.outputs.driver",
        "steps.remote_verify.outputs.digest",
    ):
        if output_ref not in reusable_text:
            fail(f"reusable workflow summary must reference ${{ {output_ref} }}")

    # Action-pin check.  The repository's supply-chain policy is that every
    # third-party `uses:` entry must be pinned to a full 40-character SHA
    # and must reference an action that has been explicitly approved.
    # `actions/checkout` is the only third-party action this workflow tree
    # depends on.  Local reusable-workflow references like `uses: ./...`
    # are not third-party pins and are skipped.
    #
    # The previous revision of this check tried to split the captured
    # line on `:` again after `re.findall` had already consumed the
    # `uses:` prefix; that left the `@v6` mutation silently empty because
    # the captured value contains no colon and `split(":", 1)[1]` would
    # IndexError.  The replacement parser handles the line uniformly:
    # strip the comment, skip local refs, rpartition on `@`, then check
    # the action allowlist and the SHA shape.
    allowed_actions = {"actions/checkout"}
    all_uses_lines = re.findall(
        r"^\s*uses:\s*(\S.*?)\s*$",
        top_text + "\n" + reusable_text,
        re.MULTILINE,
    )
    if not all_uses_lines:
        fail("no `uses:` lines found in any workflow file")

    seen_actions: set[str] = set()
    for raw_value in all_uses_lines:
        value = raw_value.split("#", 1)[0].strip()
        if not value or value.startswith("./"):
            continue
        action, separator, revision = value.rpartition("@")
        if not separator:
            fail(f"uses entry is missing @ pin: {value!r}")
        if action not in allowed_actions:
            fail(f"unapproved external action: {action}")
        if not re.fullmatch(r"[0-9a-f]{40}", revision):
            fail(f"uses entry is not pinned to a full SHA: {value!r}")
        seen_actions.add(action)

    if "actions/checkout" not in seen_actions:
        fail("reusable workflows must depend on actions/checkout (no third-party checkouts detected)")

    required_ignore = {
        ".git",
        ".github",
        ".factory",
        "**/bin",
        "**/obj",
        "**/node_modules",
        "**/elm-stuff",
        "**/TestResults",
        ".env",
        ".env.*",
        "*.pem",
        "*.key",
        "*.crt",
    }
    ignore_lines = {line.strip() for line in dockerignore.splitlines() if line.strip() and not line.startswith("#")}
    missing_ignore = required_ignore - ignore_lines
    if missing_ignore:
        fail(f".dockerignore misses required exclusions: {sorted(missing_ignore)}")
    tracked = subprocess.run(
        ["git", "ls-files", "--cached", "--others", "--exclude-standard"],
        cwd=ROOT,
        check=True,
        capture_output=True,
        text=True,
    ).stdout.splitlines()
    secret_like = re.compile(r"(^|/)(\.env(?:\.|$)|.*\.(?:pem|key|p12|pfx|dockerconfigjson))$", re.IGNORECASE)
    leaked = [path for path in tracked if secret_like.search(path)]
    if leaked:
        fail(f"secret-like files are tracked in the build context: {leaked}")
    # The forbidden-runtime-material check must inspect only the final
    # runtime stage of the Dockerfile, not the multi-stage build graph.
    # The build stage legitimately references node_modules to drive
    # `npm ci`/`npm run build`; the runtime stage (the published image)
    # is the one that must not carry that material.  The final stage
    # starts at the last `FROM ... AS runtime` (or the last unaliased
    # `FROM` when there is no `AS runtime`) and extends to the end of
    # the file.
    final_stage_match = list(
        re.finditer(
            r"^FROM\s+\S+(?:\s+AS\s+runtime)?\s*$",
            frontend,
            re.MULTILINE | re.IGNORECASE,
        )
    )
    if final_stage_match:
        final_stage = frontend[final_stage_match[-1].start():]
    else:
        final_stage = frontend
    for forbidden in ("node_modules", "elm-stuff", "TestResults"):
        if forbidden in final_stage:
            fail(f"frontend final image mentions forbidden runtime material: {forbidden}")

    # The shell test must also exercise wire_buildx_builder.sh because the
    # previous revision left the workflow-to-script seam for that script
    # untested; the build_image.sh/publish_image.sh tests in isolation
    # masked the BUILDER_NAME/PUBLISH forwarding defects.
    if "wire_buildx_builder.sh" not in shell_test:
        fail("shell test suite does not exercise wire_buildx_builder.sh")
    if "GITHUB_OUTPUT" not in shell_test:
        fail("shell test suite does not assert GITHUB_OUTPUT is used for step outputs")

    # The gate-summary acceptance test must validate the canonical Leamas
    # v1 vocabulary and exercise the targeted digest consumer
    # (leamas factory digest).  This protects against the R1 regression
    # where the raw JSON used non-canonical green/passed values while
    # the digest consumer reported every check as unavailable.
    acceptance_test = read("tests/ci/test_gate_summary_acceptance.sh")
    for marker in (
        "pass", "fail", "skip", "unavailable",
        "leamas factory digest",
        "overall_status=pass",
        "checks_passed",
        "checks_unavailable",
    ):
        if marker not in acceptance_test:
            fail(f"gate-summary acceptance test is missing required marker: {marker!r}")


    # The gate-summary validation (canonical Leamas v1 vocabulary +
    # tree-OID binding + targeted-digest integration) is intentionally
    # NOT performed here.  This script is itself one of the three gates
    # the gate-summary records, so depending on a present
    # ``.factory/gate-summary.json`` would create a chicken-and-egg: the
    # regenerate script could not invoke this policy until after it had
    # generated the artefact, and the artefact could not be generated
    # until the policy had passed.  Instead,
    # tests/ci/test_gate_summary_acceptance.sh runs after this policy and
    # exercises the full regenerate -> validate -> targeted-digest -> assert
    # loop against the canonical Leamas v1 status vocabulary.

    print("container publication policy checks passed")
    return 0


if __name__ == "__main__":
    try:
        raise SystemExit(main())
    except AssertionError as error:
        print(f"container publication policy FAILED: {error}", file=sys.stderr)
        raise SystemExit(1)
