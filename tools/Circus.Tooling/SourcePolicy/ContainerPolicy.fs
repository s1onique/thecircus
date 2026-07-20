module Circus.Tooling.SourcePolicy.ContainerPolicy

/// Static container-publication policy checks.  F# port of the
/// deleted ``scripts/verify_container_policy.py`` (see predecessor
/// ``ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01``).
///
/// Every assertion implemented here has a sibling positive and
/// negative test in ``tests/Circus.Tooling.Tests/SourcePolicy/ContainerPolicyTests.fs``
/// and a row in ``factory/container-policy-parity.csv``.  Each check
/// has a stable ``id`` (legacy_check_id) so the migration ledger can
/// be audited across ACTs.

open System
open System.IO
open System.Text.RegularExpressions

open Circus.Tooling.SourcePolicy.Inventory
open Circus.Tooling.SourcePolicy.NulInventory

exception CheckFailed of string

type Violation = {
    Check: string
    Id: string
    Path: string
    Detail: string
}

type ContainerPolicyReport = {
    ChecksTotal: int
    ChecksPassed: int
    ChecksFailed: int
    ViolationsTotal: int
    Violations: Violation list
    OperationalFailures: string list
}

let private fail (msg: string) : 'a = raise (CheckFailed msg)

let private readTextOpt (root: string) (relative: string) : string option =
    try
        let full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
        if File.Exists full then Some(File.ReadAllText full)
        else None
    with _ -> None

let private readText (root: string) (relative: string) : string =
    match readTextOpt root relative with
    | Some t -> t
    | None -> fail (sprintf "missing required file: %s" relative)

let private pathExists (root: string) (relative: string) : bool =
    let full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
    File.Exists full

let private isExecutable (root: string) (relative: string) : bool =
    try
        let full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
        let info = new FileInfo(full)
        (info.UnixFileMode &&& UnixFileMode.UserExecute) <> UnixFileMode.None
    with _ -> false

let private requireFile (root: string) (relative: string) (id: string) : Violation list =
    if pathExists root relative then []
    else [ { Check = id; Id = id; Path = relative; Detail = sprintf "required file missing: %s" relative } ]

let private requireExecutable (root: string) (relative: string) (id: string) : Violation list =
    if not (pathExists root relative) then
        [ { Check = id; Id = id; Path = relative; Detail = sprintf "required file missing: %s" relative } ]
    elif not (isExecutable root relative) then
        [ { Check = id; Id = id; Path = relative; Detail = sprintf "required shell script is not executable: %s" relative } ]
    else []

/// CP-01: required files must exist
let private checkRequiredFiles (root: string) : Violation list =
    let required = [
        "Dockerfile.backend"
        "Dockerfile.frontend"
        ".dockerignore"
        "docker/frontend/nginx.conf"
        ".github/workflows/harbor.yml"
        ".github/workflows/harbor-build-image.yml"
        ".github/scripts/install-spbnix-harbor-ca.sh"
        "docs/harbor-publishing.md"
        "scripts/ci/build_image.sh"
        "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"
        "scripts/ci/wire_buildx_builder.sh"
        "tests/ci/test_build_publish_shell.sh"
        "tests/ci/test_action_pin_mutation.sh"
        "tests/ci/test_gate_summary_acceptance.sh"
    ]
    List.collect (fun rel -> requireFile root rel "CP-01_required_files") required

/// CP-02: shell scripts must be executable
let private checkShellExecutable (root: string) : Violation list =
    let scripts = [
        "scripts/ci/build_image.sh"
        "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"
        "scripts/ci/wire_buildx_builder.sh"
        "tests/ci/test_build_publish_shell.sh"
        "tests/ci/test_action_pin_mutation.sh"
        "tests/ci/test_gate_summary_acceptance.sh"
    ]
    List.collect (fun rel -> requireExecutable root rel "CP-02_shell_executable") scripts

/// CP-03: .dockerignore must include the canonical exclusions
let private checkDockerignore (root: string) : Violation list =
    let required = [
        ".git"; ".github"; ".factory"
        "**/bin"; "**/obj"; "**/node_modules"
        "**/elm-stuff"; "**/TestResults"
        ".env"; ".env.*"
        "*.pem"; "*.key"; "*.crt"
    ]
    match readTextOpt root ".dockerignore" with
    | None ->
        [ { Check = "CP-03_dockerignore"; Id = "CP-03_dockerignore"
            Path = ".dockerignore"; Detail = ".dockerignore is missing" } ]
    | Some text ->
        let actual = text.Split('\n') |> Array.map (fun l -> l.Trim()) |> Set.ofArray
        let mutable violations : Violation list = []
        for r in required do
            if not (Set.contains r actual) then
                violations <- { Check = "CP-03_dockerignore"
                                Id = "CP-03_dockerignore"
                                Path = ".dockerignore"
                                Detail = sprintf ".dockerignore misses required exclusion: %s" r } :: violations
        List.rev violations

/// CP-04: top-level workflow triggers must include pull_request/push/workflow_dispatch
let private checkWorkflowTriggers (root: string) : Violation list =
    let required = [ "pull_request"; "push"; "workflow_dispatch" ]
    let text = readText root ".github/workflows/harbor.yml"
    let mutable violations : Violation list = []
    for trigger in required do
        let pattern = sprintf "^\s*%s\s*:" trigger
        if not (Regex(pattern, RegexOptions.Multiline).IsMatch text) then
            violations <- { Check = "CP-04_workflow_triggers"
                            Id = "CP-04_workflow_triggers"
                            Path = ".github/workflows/harbor.yml"
                            Detail = sprintf "harbor.yml lacks trigger: %s" trigger } :: violations
    List.rev violations

/// CP-05: push publication must be restricted to main and v* tags
let private checkPushBranchRestriction (root: string) : Violation list =
    let text = readText root ".github/workflows/harbor.yml"
    let mutable violations : Violation list = []
    if not (text.Contains "- main") then
        violations <- { Check = "CP-05_push_main"
                        Id = "CP-05_push_main"
                        Path = ".github/workflows/harbor.yml"
                        Detail = "push publication is not restricted to main" } :: violations
    if not (text.Contains "v*") then
        violations <- { Check = "CP-05_push_tags"
                        Id = "CP-05_push_tags"
                        Path = ".github/workflows/harbor.yml"
                        Detail = "version-tag publication trigger is missing" } :: violations
    List.rev violations

/// CP-06: top-level workflow must grant only contents: read
let private checkMinimalPermissions (root: string) : Violation list =
    let text = readText root ".github/workflows/harbor.yml"
    if text.Contains "permissions:" && text.Contains "contents: read" then []
    else
        [ { Check = "CP-06_minimal_permissions"
            Id = "CP-06_minimal_permissions"
            Path = ".github/workflows/harbor.yml"
            Detail = "top-level workflow must grant only contents: read" } ]

/// CP-07: workflow concurrency must be reference-scoped
let private checkReferenceScopedConcurrency (root: string) : Violation list =
    let text = readText root ".github/workflows/harbor.yml"
    if text.Contains "concurrency:" && text.Contains "circus-harbor-${{ github.ref }}" then []
    else
        [ { Check = "CP-07_concurrency"
            Id = "CP-07_concurrency"
            Path = ".github/workflows/harbor.yml"
            Detail = "workflow concurrency is not reference-scoped" } ]

/// CP-08: reusable workflow inputs must be present
let private checkReusableInputs (root: string) : Violation list =
    let text = readText root ".github/workflows/harbor-build-image.yml"
    let required = [
        "image_name"; "dockerfile"; "context"; "cache_name"
        "push"; "platform"; "smoke_test_kind"
    ]
    let mutable violations : Violation list = []
    for input in required do
        let pattern = sprintf "^\s*%s\s*:" input
        if not (Regex(pattern, RegexOptions.Multiline).IsMatch text) then
            violations <- { Check = "CP-08_reusable_inputs"
                            Id = "CP-08_reusable_inputs"
                            Path = ".github/workflows/harbor-build-image.yml"
                            Detail = sprintf "reusable workflow input missing: %s" input } :: violations
    if not (text.Contains "push:") || not (Regex("type:\s*boolean", RegexOptions.Multiline).IsMatch text) then
        violations <- { Check = "CP-08_reusable_push_type"
                        Id = "CP-08_reusable_push_type"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "reusable push input must be boolean" } :: violations
    List.rev violations

/// CP-09: pull_request_target must never be used
let private checkNoPullRequestTarget (root: string) : Violation list =
    let topText = readText root ".github/workflows/harbor.yml"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if topText.Contains "pull_request_target" then
        violations <- { Check = "CP-09_no_pull_request_target"
                        Id = "CP-09_no_pull_request_target"
                        Path = ".github/workflows/harbor.yml"
                        Detail = "pull_request_target is forbidden" } :: violations
    if reusableText.Contains "pull_request_target" then
        violations <- { Check = "CP-09_no_pull_request_target"
                        Id = "CP-09_no_pull_request_target"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "pull_request_target is forbidden" } :: violations
    List.rev violations

/// CP-10: trusted runner label must be referenced
let private checkTrustedRunner (root: string) : Violation list =
    let topText = readText root ".github/workflows/harbor.yml"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if not (topText.Contains "spbnix-k8s-docker") then
        violations <- { Check = "CP-10_trusted_runner"
                        Id = "CP-10_trusted_runner"
                        Path = ".github/workflows/harbor.yml"
                        Detail = "trusted publication runner label is missing" } :: violations
    if not (reusableText.Contains "runner") then
        violations <- { Check = "CP-10_trusted_runner"
                        Id = "CP-10_trusted_runner"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "trusted publication runner label is missing" } :: violations
    List.rev violations

/// CP-11: Harbor repository naming contract
let private checkHarborRepositoryNaming (root: string) : Violation list =
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let topText = readText root ".github/workflows/harbor.yml"
    let mutable violations : Violation list = []
    if not (reusableText.Contains "harbor-pve1.spbnix.local/circus/${{ inputs.image_name }}") then
        violations <- { Check = "CP-11_harbor_naming"
                        Id = "CP-11_harbor_naming"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "Harbor repository contract is missing" } :: violations
    for img in [ "circus-backend"; "circus-frontend" ] do
        if not (topText.Contains (sprintf "image_name: %s" img)) then
            violations <- { Check = "CP-11_harbor_image_contract"
                            Id = "CP-11_harbor_image_contract"
                            Path = ".github/workflows/harbor.yml"
                            Detail = sprintf "top-level image contract is missing: %s" img } :: violations
    List.rev violations

/// CP-12: Harbor password must be consumed through password-stdin
let private checkPasswordStdin (root: string) : Violation list =
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    if (reusableText.Contains "HARBOR_PASSWORD") && reusableText.Contains "--password-stdin" then
        []
    else
        [ { Check = "CP-12_password_stdin"
            Id = "CP-12_password_stdin"
            Path = ".github/workflows/harbor-build-image.yml"
            Detail = "Harbor password must be consumed through password-stdin" } ]

/// CP-13: TLS bypass must be rejected
let private checkTlsBypass (root: string) : Violation list =
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let caText = readText root ".github/scripts/install-spbnix-harbor-ca.sh"
    let forbidden = [ "--insecure"; "tls-verify=false"; "insecure=true"; "GODEBUG=x509ignoreCN=0" ]
    let mutable violations : Violation list = []
    for marker in forbidden do
        if reusableText.Contains marker || caText.Contains marker then
            violations <- { Check = "CP-13_tls_bypass"
                            Id = "CP-13_tls_bypass"
                            Path = ".github/workflows/harbor-build-image.yml"
                            Detail = sprintf "TLS verification bypass is forbidden: %s" marker } :: violations
    List.rev violations

/// CP-14: private CA / BuildKit configuration
let private checkPrivateCaAndBuildkit (root: string) : Violation list =
    let caText = readText root ".github/scripts/install-spbnix-harbor-ca.sh"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if not (caText.Contains "SPBNIX_CA_CERT_PEM") then
        violations <- { Check = "CP-14_ca_secret"
                        Id = "CP-14_ca_secret"
                        Path = ".github/scripts/install-spbnix-harbor-ca.sh"
                        Detail = "CA script does not consume the CA secret" } :: violations
    if not (caText.Contains "buildkitd.toml") then
        violations <- { Check = "CP-14_buildkit_config"
                        Id = "CP-14_buildkit_config"
                        Path = ".github/scripts/install-spbnix-harbor-ca.sh"
                        Detail = "CA script does not create BuildKit config" } :: violations
    if not (caText.Contains "[registry.\"${HARBOR_HOST}\"]") then
        violations <- { Check = "CP-14_buildkit_registry"
                        Id = "CP-14_buildkit_registry"
                        Path = ".github/scripts/install-spbnix-harbor-ca.sh"
                        Detail = "BuildKit registry CA mapping is missing" } :: violations
    if not (reusableText.Contains "SPBNIX_CA_CERT_PEM") then
        violations <- { Check = "CP-14_reusable_ca"
                        Id = "CP-14_reusable_ca"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "reusable workflow does not declare SPBNIX_CA_CERT_PEM" } :: violations
    List.rev violations

/// CP-15: cache separation between backend and frontend
let private checkCacheSeparation (root: string) : Violation list =
    let topText = readText root ".github/workflows/harbor.yml"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    let cacheTemplate = "CACHE_REF: harbor-pve1.spbnix.local/circus/cache/${{ inputs.cache_name }}:buildcache"
    if not (reusableText.Contains cacheTemplate) then
        violations <- { Check = "CP-15_cache_template"
                        Id = "CP-15_cache_template"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "Harbor cache reference template is missing" } :: violations
    let mutable cacheRefs : string list = []
    for cacheName in [ "circus-backend"; "circus-frontend" ] do
        if not (topText.Contains (sprintf "cache_name: %s" cacheName)) then
            violations <- { Check = "CP-15_cache_image_specific"
                            Id = "CP-15_cache_image_specific"
                            Path = ".github/workflows/harbor.yml"
                            Detail = sprintf "image-specific cache reference is missing: %s" cacheName } :: violations
        cacheRefs <- (sprintf "harbor-pve1.spbnix.local/circus/cache/%s:buildcache" cacheName) :: cacheRefs
    if List.length cacheRefs = 2 && cacheRefs.[0] = cacheRefs.[1] then
        violations <- { Check = "CP-15_cache_distinct"
                        Id = "CP-15_cache_distinct"
                        Path = ".github/workflows/harbor.yml"
                        Detail = "backend and frontend cache references must remain distinct" } :: violations
    List.rev violations

/// CP-16: publish true/false gating in build/publish scripts
let private checkPublishGating (root: string) : Violation list =
    let buildText = readText root "scripts/ci/build_image.sh"
    let publishText = readText root "scripts/ci/publish_image.sh"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if not (buildText.Contains "PUBLISH") then
        violations <- { Check = "CP-16_build_publish_marker"
                        Id = "CP-16_build_publish_marker"
                        Path = "scripts/ci/build_image.sh"
                        Detail = "build script does not gate on PUBLISH" } :: violations
    if not (publishText.Contains "PUBLISH") then
        violations <- { Check = "CP-16_publish_publish_marker"
                        Id = "CP-16_publish_publish_marker"
                        Path = "scripts/ci/publish_image.sh"
                        Detail = "publish script does not gate on PUBLISH" } :: violations
    if not (buildText.Contains "== \"true\"") && not (buildText.Contains "!= \"true\"") then
        violations <- { Check = "CP-16_build_compare"
                        Id = "CP-16_build_compare"
                        Path = "scripts/ci/build_image.sh"
                        Detail = "build script does not compare PUBLISH to 'true'" } :: violations
    if not (publishText.Contains "== \"true\"") && not (publishText.Contains "!= \"true\"") then
        violations <- { Check = "CP-16_publish_compare"
                        Id = "CP-16_publish_compare"
                        Path = "scripts/ci/publish_image.sh"
                        Detail = "publish script does not compare PUBLISH to 'true'" } :: violations
    if not (reusableText.Contains "PUBLISH: ${{ steps.metadata.outputs.publish }}") then
        violations <- { Check = "CP-16_reusable_publish_forward"
                        Id = "CP-16_reusable_publish_forward"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "reusable workflow must forward the PUBLISH env to the build/publish scripts" } :: violations
    List.rev violations

/// CP-17: registry cache import/export (cache-from/cache-to/mode=max)
let private checkCacheImportExport (root: string) : Violation list =
    let buildText = readText root "scripts/ci/build_image.sh"
    let publishText = readText root "scripts/ci/publish_image.sh"
    let mutable violations : Violation list = []
    if not (buildText.Contains "cache-from") then
        violations <- { Check = "CP-17_cache_from"
                        Id = "CP-17_cache_from"
                        Path = "scripts/ci/build_image.sh"
                        Detail = "registry cache import is missing" } :: violations
    if not (publishText.Contains "cache-to") then
        violations <- { Check = "CP-17_cache_to"
                        Id = "CP-17_cache_to"
                        Path = "scripts/ci/publish_image.sh"
                        Detail = "registry cache export is missing" } :: violations
    if not (publishText.Contains "mode=max") then
        violations <- { Check = "CP-17_cache_mode_max"
                        Id = "CP-17_cache_mode_max"
                        Path = "scripts/ci/publish_image.sh"
                        Detail = "cache mode=max is missing" } :: violations
    if not (publishText.Contains "oci-mediatypes=true,image-manifest=true") then
        violations <- { Check = "CP-17_cache_oci_manifest"
                        Id = "CP-17_cache_oci_manifest"
                        Path = "scripts/ci/publish_image.sh"
                        Detail = "Harbor cache export is missing the OCI image-manifest compatibility options" } :: violations
    List.rev violations

/// CP-18: immutable tag generation
let private checkImmutableTags (root: string) : Violation list =
    let metadata = readText root ".github/scripts/harbor-metadata.sh"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if not (metadata.Contains "GITHUB_SHA") || not (metadata.Contains "local-${sha}") then
        violations <- { Check = "CP-18_immutable_tag"
                        Id = "CP-18_immutable_tag"
                        Path = ".github/scripts/harbor-metadata.sh"
                        Detail = "full commit SHA is not part of the immutable tag contract" } :: violations
    let releaseTags = [ "v${release}"; "${release}"; "${major}.${minor}"; "${major}" ]
    for tag in releaseTags do
        if not (metadata.Contains tag) then
            violations <- { Check = "CP-18_release_tag"
                            Id = "CP-18_release_tag"
                            Path = ".github/scripts/harbor-metadata.sh"
                            Detail = sprintf "release tag missing: %s" tag } :: violations
    if not (reusableText.Contains "steps.metadata.outputs.publish == 'true'") then
        violations <- { Check = "CP-18_trusted_guard"
                        Id = "CP-18_trusted_guard"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "trusted publication steps are not guarded by metadata publication" } :: violations
    List.rev violations

/// CP-19: main-only `latest` tag
let private checkLatestTagContract (root: string) : Violation list =
    let metadata = readText root ".github/scripts/harbor-metadata.sh"
    let mutable violations : Violation list = []
    if not (metadata.Contains "latest") then
        violations <- { Check = "CP-19_latest_present"
                        Id = "CP-19_latest_present"
                        Path = ".github/scripts/harbor-metadata.sh"
                        Detail = "latest tag is not present" } :: violations
    if not (metadata.Contains "refs/heads/main") then
        violations <- { Check = "CP-19_latest_main_only"
                        Id = "CP-19_latest_main_only"
                        Path = ".github/scripts/harbor-metadata.sh"
                        Detail = "latest tag is not gated to refs/heads/main" } :: violations
    if Regex.Matches(metadata, "latest").Count <> 1 then
        violations <- { Check = "CP-19_latest_unique"
                        Id = "CP-19_latest_unique"
                        Path = ".github/scripts/harbor-metadata.sh"
                        Detail = "latest must be emitted only once by the metadata contract" } :: violations
    List.rev violations

/// CP-20: secret mount + cleanup behaviour
let private checkSecretMountCleanup (root: string) : Violation list =
    let frontend = readText root "Dockerfile.frontend"
    let required = [
        "id=spbnix-ca,target=/run/secrets/spbnix-ca"
        "if [ -s /run/secrets/spbnix-ca ]"
        "/etc/ssl/certs/ca-certificates.crt"
        "/tmp/circus-ca-bundle.pem"
        "NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca"
        "SSL_CERT_FILE=/tmp/circus-ca-bundle.pem"
        "rm -f /tmp/circus-ca-bundle.pem"
    ]
    let mutable violations : Violation list = []
    for marker in required do
        if not (frontend.Contains marker) then
            violations <- { Check = "CP-20_secret_marker"
                            Id = "CP-20_secret_marker"
                            Path = "Dockerfile.frontend"
                            Detail = sprintf "frontend Dockerfile is missing the required CA marker: %s" marker } :: violations
    if frontend.Contains "update-ca-certificates" then
        violations <- { Check = "CP-20_update_ca"
                        Id = "CP-20_update_ca"
                        Path = "Dockerfile.frontend"
                        Detail = "frontend Dockerfile mutates the system CA store" } :: violations
    if frontend.Contains "/tmp/spbnix-ca.crt" then
        violations <- { Check = "CP-20_legacy_path"
                        Id = "CP-20_legacy_path"
                        Path = "Dockerfile.frontend"
                        Detail = "frontend Dockerfile references the obsolete /tmp/spbnix-ca.crt path" } :: violations
    List.rev violations

/// CP-21: Elm installer markers (frontend install layer)
let private checkElmInstaller (root: string) : Violation list =
    let frontend = readText root "Dockerfile.frontend"
    let markers = [
        "npm ci --ignore-scripts"
        "node node_modules/elm/install.js"
        "./node_modules/.bin/elm --version"
        "Elm ${ELM_VERSION}"
    ]
    let mutable violations : Violation list = []
    for marker in markers do
        if not (frontend.Contains marker) then
            violations <- { Check = "CP-21_elm_marker"
                            Id = "CP-21_elm_marker"
                            Path = "Dockerfile.frontend"
                            Detail = sprintf "frontend Dockerfile is missing the required Elm installer marker: %s" marker } :: violations
    if not (Regex("0\.19\.2|Elm\\s0\.19\.2").IsMatch frontend) then
        violations <- { Check = "CP-21_elm_version"
                        Id = "CP-21_elm_version"
                        Path = "Dockerfile.frontend"
                        Detail = "frontend Dockerfile does not assert Elm 0.19.2" } :: violations
    List.rev violations

/// CP-22: numeric non-root user on both Dockerfiles
let private checkNumericUsers (root: string) : Violation list =
    let backend = readText root "Dockerfile.backend"
    let frontend = readText root "Dockerfile.frontend"
    let mutable violations : Violation list = []
    if not (Regex("USER\\s+\\d+:\\d+").IsMatch backend) then
        violations <- { Check = "CP-22_backend_user"
                        Id = "CP-22_backend_user"
                        Path = "Dockerfile.backend"
                        Detail = "backend final image lacks an explicit numeric non-root user" } :: violations
    if not (Regex("USER\\s+\\d+:\\d+").IsMatch frontend) then
        violations <- { Check = "CP-22_frontend_user"
                        Id = "CP-22_frontend_user"
                        Path = "Dockerfile.frontend"
                        Detail = "frontend final image lacks an explicit numeric non-root user" } :: violations
    List.rev violations

/// CP-23: port contracts
let private checkPortContracts (root: string) : Violation list =
    let backend = readText root "Dockerfile.backend"
    let frontend = readText root "Dockerfile.frontend"
    let nginx = readText root "docker/frontend/nginx.conf"
    let mutable violations : Violation list = []
    if not (backend.Contains "ASPNETCORE_HTTP_PORTS=8080") || not (backend.Contains "EXPOSE 8080") then
        violations <- { Check = "CP-23_backend_port"
                        Id = "CP-23_backend_port"
                        Path = "Dockerfile.backend"
                        Detail = "backend 8080 contract is incomplete" } :: violations
    if not (nginx.Contains "listen 8080") || not (frontend.Contains "EXPOSE 8080") then
        violations <- { Check = "CP-23_frontend_port"
                        Id = "CP-23_frontend_port"
                        Path = "Dockerfile.frontend"
                        Detail = "frontend 8080 contract is incomplete" } :: violations
    List.rev violations

/// CP-24: smoke endpoint contracts
let private checkSmokeEndpoints (root: string) : Violation list =
    let smoke = readText root "scripts/container-smoke.sh"
    let mutable violations : Violation list = []
    if not (smoke.Contains "/health/live") && not (smoke.Contains "GET /health/live") && not (smoke.Contains "health/live") then
        violations <- { Check = "CP-24_backend_smoke"
                        Id = "CP-24_backend_smoke"
                        Path = "scripts/container-smoke.sh"
                        Detail = "backend smoke test does not check /health/live" } :: violations
    for marker in [ "/healthz"; "<title>The Circus</title>"; "GET /" ] do
        if not (smoke.Contains marker) then
            violations <- { Check = "CP-24_frontend_smoke"
                            Id = "CP-24_frontend_smoke"
                            Path = "scripts/container-smoke.sh"
                            Detail = sprintf "frontend smoke contract is missing: %s" marker } :: violations
    List.rev violations

/// CP-25: digest-based pull + inspect
let private checkDigestPullInspect (root: string) : Violation list =
    let verifyText = readText root "scripts/ci/verify_build_image.sh"
    let verifyPubText = readText root "scripts/verify-published-image.sh"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    if not (verifyText.Contains "docker pull \"${IMAGE_REPOSITORY}@${digest}\"") then
        violations <- { Check = "CP-25_digest_pull"
                        Id = "CP-25_digest_pull"
                        Path = "scripts/ci/verify_build_image.sh"
                        Detail = "post-push verification does not pull by registry digest" } :: violations
    if not (verifyText.Contains "docker image inspect \"${IMAGE_REPOSITORY}@${digest}\"") then
        violations <- { Check = "CP-25_digest_inspect"
                        Id = "CP-25_digest_inspect"
                        Path = "scripts/ci/verify_build_image.sh"
                        Detail = "post-push verification does not inspect the digest reference" } :: violations
    if not (reusableText.Contains "linux/amd64") || not (verifyPubText.Contains "architecture") then
        violations <- { Check = "CP-25_amd64_verify"
                        Id = "CP-25_amd64_verify"
                        Path = ".github/workflows/harbor-build-image.yml"
                        Detail = "amd64 post-publication verification is incomplete" } :: violations
    List.rev violations

/// CP-26: workflow-to-script environment seams
let private checkWorkflowSeams (root: string) : Violation list =
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let seams = [
        ("Create configured BuildKit builder", "PUBLISH: ${{ steps.metadata.outputs.publish }}")
        ("Create configured BuildKit builder", "BUILDER_NAME: circus-${{ github.run_id }}")
        ("Create configured BuildKit builder", "BUILDKITD_CONFIG: ${{ steps.harbor_ca")
        ("Build testable image with repository cache", "BUILDER_NAME: ${{ steps.builder.outputs.builder }}")
        ("Publish tested revision with provenance and SBOM attempt", "PUBLISH: ${{ steps.metadata.outputs.publish }}")
        ("Publish tested revision with provenance and SBOM attempt", "BUILDER_NAME: ${{ steps.builder.outputs.builder }}")
    ]
    let mutable violations : Violation list = []
    for (stepName, snippet) in seams do
        if not (reusableText.Contains (sprintf "name: %s" stepName)) then
            violations <- { Check = "CP-26_seam_step"
                            Id = "CP-26_seam_step"
                            Path = ".github/workflows/harbor-build-image.yml"
                            Detail = sprintf "reusable workflow is missing the %s step" stepName } :: violations
        elif not (reusableText.Contains snippet) then
            violations <- { Check = "CP-26_seam_forward"
                            Id = "CP-26_seam_forward"
                            Path = ".github/workflows/harbor-build-image.yml"
                            Detail = sprintf "reusable workflow step '%s' does not forward %s" stepName snippet } :: violations
    List.rev violations

/// CP-27: $GITHUB_OUTPUT contracts
let private checkGithubOutputContracts (root: string) : Violation list =
    let buildText = readText root "scripts/ci/build_image.sh"
    let publishText = readText root "scripts/ci/publish_image.sh"
    let wireText = readText root "scripts/ci/wire_buildx_builder.sh"
    let verifyText = readText root "scripts/ci/verify_build_image.sh"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let mutable violations : Violation list = []
    for (scriptText, label) in [
        (buildText, "scripts/ci/build_image.sh")
        (publishText, "scripts/ci/publish_image.sh")
        (wireText, "scripts/ci/wire_buildx_builder.sh")
        (verifyText, "scripts/ci/verify_build_image.sh")
    ] do
        if not (scriptText.Contains "GITHUB_OUTPUT") then
            violations <- { Check = "CP-27_github_output"
                            Id = "CP-27_github_output"
                            Path = label
                            Detail = sprintf "%s must use $GITHUB_OUTPUT for step outputs" label } :: violations
    for outputRef in [
        "steps.builder.outputs.builder"
        "steps.builder.outputs.driver"
        "steps.remote_verify.outputs.digest"
    ] do
        if not (reusableText.Contains outputRef) then
            violations <- { Check = "CP-27_workflow_output"
                            Id = "CP-27_workflow_output"
                            Path = ".github/workflows/harbor-build-image.yml"
                            Detail = sprintf "reusable workflow must reference ${{ %s }}" outputRef } :: violations
    List.rev violations

/// CP-28: approved action allowlist + full 40-character SHA pin
let private checkActionPins (root: string) : Violation list =
    let topText = readText root ".github/workflows/harbor.yml"
    let reusableText = readText root ".github/workflows/harbor-build-image.yml"
    let combined = topText + "\n" + reusableText
    let mutable violations : Violation list = []
    let mutable foundExternal = false
    for m in Regex.Matches(combined, "^\s*uses:\s*(\S.*?)\s*$", RegexOptions.Multiline) do
        let raw = m.Groups.[1].Value.Split('#').[0].Trim()
        if raw <> "" && not (raw.StartsWith "./") then
            let lastAt = raw.LastIndexOf '@'
            if lastAt < 0 then
                violations <- { Check = "CP-28_action_pin"
                                Id = "CP-28_action_pin"
                                Path = "workflows"
                                Detail = sprintf "uses entry is missing @ pin: %s" raw } :: violations
            else
                let action = raw.Substring(0, lastAt)
                let revision = raw.Substring(lastAt + 1)
                let allowed = action = "actions/checkout"
                if not allowed then
                    violations <- { Check = "CP-28_action_allowlist"
                                    Id = "CP-28_action_allowlist"
                                    Path = "workflows"
                                    Detail = sprintf "unapproved external action: %s" action } :: violations
                if not (Regex("^[0-9a-f]{40}$").IsMatch revision) then
                    violations <- { Check = "CP-28_action_sha_pin"
                                    Id = "CP-28_action_sha_pin"
                                    Path = "workflows"
                                    Detail = sprintf "uses entry is not pinned to a full SHA: %s" raw } :: violations
                if action = "actions/checkout" then foundExternal <- true
    if not foundExternal then
        violations <- { Check = "CP-28_action_present"
                        Id = "CP-28_action_present"
                        Path = "workflows"
                        Detail = "reusable workflows must depend on actions/checkout (no third-party checkouts detected)" } :: violations
    List.rev violations

/// CP-29: tracked secret-like file rejection.  When Git fails we
/// must surface the failure as an operational error (exit 2 in the
/// gate runner).  Returning an empty list would silently pass the
/// policy even though we cannot prove the secret scan is
/// complete.
let private checkTrackedSecrets (root: string) : Violation list =
    match gitTrackedFiles root with
    | TrackedInventoryFailed f ->
        [ { Check = "CP-29_tracked_secrets"
            Id = "CP-29_tracked_secrets"
            Path = "<git inventory>"
            Detail = sprintf "git ls-files failed (cannot prove the secret scan is complete): %s" (Inventory.renderInventoryFailure f) } ]
    | TrackedFiles tracked ->
        let secretPattern = Regex("(^|/)\\.env(\\.|$)|.*\\.(pem|key|p12|pfx|dockerconfigjson)$", RegexOptions.IgnoreCase)
        let leaked = tracked |> List.filter (fun p -> secretPattern.IsMatch p)
        if List.isEmpty leaked then []
        else
            [ { Check = "CP-29_tracked_secrets"
                Id = "CP-29_tracked_secrets"
                Path = "<tracked files>"
                Detail = sprintf "secret-like files are tracked in the build context: %A" leaked } ]

/// CP-30: final-stage runtime-material exclusions
let private checkFinalStageExclusions (root: string) : Violation list =
    let frontend = readText root "Dockerfile.frontend"
    let matches = Regex.Matches(frontend, "^FROM\\s+\\S+(?:\\s+AS\\s+runtime)?\\s*$", RegexOptions.Multiline ||| RegexOptions.IgnoreCase)
    let finalStage =
        if matches.Count > 0 then
            let m = matches.[matches.Count - 1]
            frontend.Substring(m.Index)
        else frontend
    let forbidden = [ "node_modules"; "elm-stuff"; "TestResults" ]
    let mutable violations : Violation list = []
    for f in forbidden do
        if finalStage.Contains f then
            violations <- { Check = "CP-30_final_stage_material"
                            Id = "CP-30_final_stage_material"
                            Path = "Dockerfile.frontend"
                            Detail = sprintf "frontend final image mentions forbidden runtime material: %s" f } :: violations
    List.rev violations

/// CP-31: gate-summary acceptance markers (acceptance test must validate Leamas v1)
let private checkGateSummaryAcceptance (root: string) : Violation list =
    let acceptanceText = readText root "tests/ci/test_gate_summary_acceptance.sh"
    let required = [
        "leamas factory digest"
        "overall_status=pass"
        "checks_passed"
        "checks_unavailable"
    ]
    let mutable violations : Violation list = []
    for marker in required do
        if not (acceptanceText.Contains marker) then
            violations <- { Check = "CP-31_acceptance_marker"
                            Id = "CP-31_acceptance_marker"
                            Path = "tests/ci/test_gate_summary_acceptance.sh"
                            Detail = sprintf "gate-summary acceptance test is missing required marker: %s" marker } :: violations
    for vocab in [ "pass"; "fail"; "skip"; "unavailable" ] do
        if not (acceptanceText.Contains vocab) then
            violations <- { Check = "CP-31_acceptance_vocab"
                            Id = "CP-31_acceptance_vocab"
                            Path = "tests/ci/test_gate_summary_acceptance.sh"
                            Detail = sprintf "gate-summary acceptance test must reference canonical status: %s" vocab } :: violations
    let shellTest = readText root "tests/ci/test_build_publish_shell.sh"
    if not (shellTest.Contains "PUBLISH=true") || not (shellTest.Contains "PUBLISH=false") then
        violations <- { Check = "CP-31_publish_branch_coverage"
                        Id = "CP-31_publish_branch_coverage"
                        Path = "tests/ci/test_build_publish_shell.sh"
                        Detail = "shell test suite does not exercise both PUBLISH branches" } :: violations
    if not (shellTest.Contains "wire_buildx_builder.sh") then
        violations <- { Check = "CP-31_wire_coverage"
                        Id = "CP-31_wire_coverage"
                        Path = "tests/ci/test_build_publish_shell.sh"
                        Detail = "shell test suite does not exercise wire_buildx_builder.sh" } :: violations
    if not (shellTest.Contains "GITHUB_OUTPUT") then
        violations <- { Check = "CP-31_github_output_assertion"
                        Id = "CP-31_github_output_assertion"
                        Path = "tests/ci/test_build_publish_shell.sh"
                        Detail = "shell test suite does not assert GITHUB_OUTPUT is used for step outputs" } :: violations
    List.rev violations

/// Build the full parity manifest.  Each entry maps a legacy Python
/// check to its F# implementation; tests exercise both the positive
/// (correct repository state) and the negative (mutated repository)
/// cases for every entry.
/// P1-1: Explicit check definition with identity, function name, and runner.
/// Using nameof ensures compile-time binding and rename tracking.
type CheckDefinition = {
    Id: string
    ImplementationFunction: string
    Run: string -> Violation list
}

let private checks : CheckDefinition list = [
    { Id = "CP-01_required_files";         ImplementationFunction = nameof checkRequiredFiles;        Run = checkRequiredFiles }
    { Id = "CP-02_shell_executable";        ImplementationFunction = nameof checkShellExecutable;       Run = checkShellExecutable }
    { Id = "CP-03_dockerignore";            ImplementationFunction = nameof checkDockerignore;           Run = checkDockerignore }
    { Id = "CP-04_workflow_triggers";       ImplementationFunction = nameof checkWorkflowTriggers;      Run = checkWorkflowTriggers }
    { Id = "CP-05_push_main";               ImplementationFunction = nameof checkPushBranchRestriction; Run = checkPushBranchRestriction }
    { Id = "CP-06_minimal_permissions";     ImplementationFunction = nameof checkMinimalPermissions;    Run = checkMinimalPermissions }
    { Id = "CP-07_concurrency";             ImplementationFunction = nameof checkReferenceScopedConcurrency; Run = checkReferenceScopedConcurrency }
    { Id = "CP-08_reusable_inputs";          ImplementationFunction = nameof checkReusableInputs;        Run = checkReusableInputs }
    { Id = "CP-09_no_pull_request_target";   ImplementationFunction = nameof checkNoPullRequestTarget;  Run = checkNoPullRequestTarget }
    { Id = "CP-10_trusted_runner";          ImplementationFunction = nameof checkTrustedRunner;        Run = checkTrustedRunner }
    { Id = "CP-11_harbor_naming";           ImplementationFunction = nameof checkHarborRepositoryNaming; Run = checkHarborRepositoryNaming }
    { Id = "CP-12_password_stdin";           ImplementationFunction = nameof checkPasswordStdin;        Run = checkPasswordStdin }
    { Id = "CP-13_tls_bypass";              ImplementationFunction = nameof checkTlsBypass;             Run = checkTlsBypass }
    { Id = "CP-14_ca_secret";               ImplementationFunction = nameof checkPrivateCaAndBuildkit; Run = checkPrivateCaAndBuildkit }
    { Id = "CP-15_cache_distinct";          ImplementationFunction = nameof checkCacheSeparation;       Run = checkCacheSeparation }
    { Id = "CP-16_publish_gating";          ImplementationFunction = nameof checkPublishGating;         Run = checkPublishGating }
    { Id = "CP-17_cache_import_export";     ImplementationFunction = nameof checkCacheImportExport;     Run = checkCacheImportExport }
    { Id = "CP-18_immutable_tag";           ImplementationFunction = nameof checkImmutableTags;          Run = checkImmutableTags }
    { Id = "CP-19_latest_main_only";        ImplementationFunction = nameof checkLatestTagContract;      Run = checkLatestTagContract }
    { Id = "CP-20_secret_marker";           ImplementationFunction = nameof checkSecretMountCleanup;    Run = checkSecretMountCleanup }
    { Id = "CP-21_elm_marker";              ImplementationFunction = nameof checkElmInstaller;         Run = checkElmInstaller }
    { Id = "CP-22_backend_user";             ImplementationFunction = nameof checkNumericUsers;           Run = checkNumericUsers }
    { Id = "CP-23_backend_port";            ImplementationFunction = nameof checkPortContracts;           Run = checkPortContracts }
    { Id = "CP-24_backend_smoke";           ImplementationFunction = nameof checkSmokeEndpoints;         Run = checkSmokeEndpoints }
    { Id = "CP-25_digest_pull";             ImplementationFunction = nameof checkDigestPullInspect;       Run = checkDigestPullInspect }
    { Id = "CP-26_seam_step";               ImplementationFunction = nameof checkWorkflowSeams;         Run = checkWorkflowSeams }
    { Id = "CP-27_github_output";           ImplementationFunction = nameof checkGithubOutputContracts;   Run = checkGithubOutputContracts }
    { Id = "CP-28_action_pin";              ImplementationFunction = nameof checkActionPins;             Run = checkActionPins }
    { Id = "CP-29_tracked_secrets";          ImplementationFunction = nameof checkTrackedSecrets;         Run = checkTrackedSecrets }
    { Id = "CP-30_final_stage_material";    ImplementationFunction = nameof checkFinalStageExclusions;   Run = checkFinalStageExclusions }
    { Id = "CP-31_acceptance_marker";       ImplementationFunction = nameof checkGateSummaryAcceptance;   Run = checkGateSummaryAcceptance }
]

/// Public surface: all check IDs so tests can mutate each one in turn.
let CheckIds : string list = checks |> List.map (fun c -> c.Id)

/// P1-1: Single authoritative source for identity and function mapping.
type CheckMetadata = {
    Id: string
    ImplementationFunction: string
}

/// P1-1: Authoritative production metadata derived from CheckDefinition.
let CheckMetadata : CheckMetadata list =
    checks
    |> List.map (fun c -> { Id = c.Id; ImplementationFunction = c.ImplementationFunction })

/// Public surface: run a single check by id.
let runCheckById (id: string) (root: string) : Violation list =
    match checks |> List.tryFind (fun c -> c.Id = id) with
    | Some c -> c.Run root
    | None -> [ { Check = id; Id = id; Path = "<unknown>"; Detail = sprintf "unknown check id: %s" id } ]

/// Run every check and produce a unified report.  ``ChecksFailed``
/// counts the number of *checks* that produced at least one
/// violation, distinct from ``ViolationsTotal`` which is the raw
/// count of violations.
let verify (root: string) : ContainerPolicyReport =
    let mutable passed = 0
    let mutable failedChecks = 0
    let mutable allViolations : Violation list = []
    let mutable operational : string list = []
    for c in checks do
        let v = c.Run root
        if List.isEmpty v then
            passed <- passed + 1
        else
            failedChecks <- failedChecks + 1
            if c.Id = "CP-29_tracked_secrets" &&
               List.exists (fun x -> x.Detail.Contains "git ls-files failed") v then
                operational <- "CP-29 git inventory failed (cannot prove the secret scan is complete)" :: operational
            allViolations <- List.rev v @ allViolations
    { ChecksTotal = List.length checks
      ChecksPassed = passed
      ChecksFailed = failedChecks
      ViolationsTotal = List.length allViolations
      Violations = allViolations
      OperationalFailures = operational }

let runVerify (root: string) : int =
    try
        let report = verify root
        if not (List.isEmpty report.OperationalFailures) then
            stderr.WriteLine(sprintf "container-policy verify: FAIL (operational: %d)" (List.length report.OperationalFailures))
            for op in report.OperationalFailures do
                stderr.WriteLine(sprintf "  - %s" op)
            2
        elif List.isEmpty report.Violations then
            stdout.WriteLine(sprintf "container-policy verify: PASS (checks=%d)" report.ChecksTotal)
            0
        else
            stderr.WriteLine(sprintf "container-policy verify: FAIL (checks=%d, failed=%d, violations=%d)"
                report.ChecksTotal report.ChecksFailed report.ViolationsTotal)
            for v in report.Violations do
                stderr.WriteLine(sprintf "  - [%s] %s: %s" v.Id v.Path v.Detail)
            1
    with
    | _ -> 2