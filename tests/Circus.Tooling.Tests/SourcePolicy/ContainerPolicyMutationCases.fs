module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationCases

/// Authoritative registry of all 22 container-policy negative mutation
/// cases (P0-5, CORRECTION01).  Every case definition is a value in
/// one immutable list.  No global mutable accounting.  No independent
/// per-case tests followed by an aggregate guess.  No fake passes
/// through counter manipulation.

open System
open System.IO

open Circus.Tooling.SourcePolicy.ContainerPolicy
open Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationRegistry

// ---------------------------------------------------------------------------
// Compliant reference fixtures
// ---------------------------------------------------------------------------

let private compliantDockerignore =
    ".git\n.github\n.factory\n**/bin\n**/obj\n**/node_modules\n**/elm-stuff\n**/TestResults\n.env\n.env.*\n*.pem\n*.key\n*.crt\n"

/// Truly compliant ``.github/workflows/harbor.yml`` (CORRECTION01
/// P0-5, repaired baselines).  The runner label
/// ``spbnix-k8s-docker`` is present so the CP-10 baseline proof
/// passes.
let private compliantHarbor =
    "name: harbor\n" +
    "on:\n" +
    "  pull_request:\n" +
    "  push:\n" +
    "    branches:\n" +
    "      - main\n" +
    "      - 'v*'\n" +
    "  workflow_dispatch:\n" +
    "permissions:\n" +
    "  contents: read\n" +
    "concurrency:\n" +
    "  group: circus-harbor-${{ github.ref }}\n" +
    "jobs:\n" +
    "  backend:\n" +
    "    uses: ./.github/workflows/harbor-build-image.yml\n" +
    "    with:\n" +
    "      image_name: circus-backend\n" +
    "      cache_name: circus-backend\n" +
    "  frontend:\n" +
    "    uses: ./.github/workflows/harbor-build-image.yml\n" +
    "    with:\n" +
    "      image_name: circus-frontend\n" +
    "      cache_name: circus-frontend\n" +
    "  trusted-runner-probe:\n" +
    "    runs-on: spbnix-k8s-docker\n" +
    "    steps:\n" +
    "      - run: echo spbnix-k8s-docker\n"

/// Compliant reusable workflow (CORRECTION01 P0-5, repaired
/// baselines).
let private compliantReusable =
    "name: build-image\n" +
    "on:\n  workflow_call:\n    inputs:\n" +
    "      image_name:\n        type: string\n" +
    "      dockerfile:\n        type: string\n" +
    "      context:\n        type: string\n" +
    "      cache_name:\n        type: string\n" +
    "      push:\n        type: boolean\n" +
    "      platform:\n        type: string\n" +
    "      smoke_test_kind:\n        type: string\n" +
    "    secrets:\n      SPBNIX_CA_CERT_PEM:\n        required: true\n" +
    "      HARBOR_PASSWORD:\n        required: true\n" +
    "jobs:\n  build:\n    runs-on: spbnix-k8s-docker\n" +
    "    steps:\n" +
    "      - name: Create configured BuildKit builder (trusted runner)\n" +
    "        env:\n          PUBLISH: ${{ steps.metadata.outputs.publish }}\n" +
    "          BUILDER_NAME: circus-${{ github.run_id }}\n" +
    "          BUILDKITD_CONFIG: ${{ steps.harbor_ca.outputs.path }}\n" +
    "        run: ./scripts/ci/wire_buildx_builder.sh\n" +
    "      - name: Build testable image with repository cache\n" +
    "        env:\n          CACHE_REF: harbor-pve1.spbnix.local/circus/cache/${{ inputs.cache_name }}:buildcache\n" +
    "          IMAGE_REPOSITORY: harbor-pve1.spbnix.local/circus/${{ inputs.image_name }}\n" +
    "          BUILDER_NAME: ${{ steps.builder.outputs.builder }}\n" +
    "          DRIVER: ${{ steps.builder.outputs.driver }}\n" +
    "        run: ./scripts/ci/build_image.sh\n" +
    "      - name: Publish tested revision with provenance and SBOM attempt\n" +
    "        if: steps.metadata.outputs.publish == 'true'\n" +
    "        env:\n          PUBLISH: ${{ steps.metadata.outputs.publish }}\n" +
    "          BUILDER_NAME: ${{ steps.builder.outputs.builder }}\n" +
    "          REMOTE_DIGEST: ${{ steps.remote_verify.outputs.digest }}\n" +
    "        run: ./scripts/ci/publish_image.sh\n" +
    "      - name: Verify build by digest for linux/amd64\n" +
    "        env:\n          BUILDER_NAME: ${{ steps.builder.outputs.builder }}\n" +
    "          PLATFORM: linux/amd64\n" +
    "        run: ./scripts/ci/verify_build_image.sh\n"

let private compliantReusableWithPassword =
    compliantReusable +
    "      - name: Login\n" +
    "        env:\n          HARBOR_PASSWORD: ${{ secrets.HARBOR_PASSWORD }}\n" +
    "        run: echo \"$HARBOR_PASSWORD\" | docker login harbor-pve1.spbnix.local --username circus --password-stdin\n"

let private compliantCaScript =
    "#!/usr/bin/env bash\nset -euo pipefail\necho SPBNIX_CA_CERT_PEM\necho buildkitd.toml\necho [registry.\"${HARBOR_HOST}\"]\n"

/// CORRECTION01 P0-5: build/publish scripts must use the canonical
/// ``== \"true\"`` / ``!= \"true\"`` comparison (not bare ``=
/// \"true\"``) so the CP-16 baseline proof passes.
let private compliantBuildScript =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" == \"true\" ]; then echo publish; else echo build; fi\ndocker buildx build --cache-from type=registry,ref=harbor-pve1.spbnix.local/circus/cache/x:buildcache .\necho \"builder=$BUILDER_NAME\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantPublishScript =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" == \"true\" ]; then echo publish; else echo skip; fi\ndocker buildx build --cache-to type=registry,ref=harbor-pve1.spbnix.local/circus/cache/x:buildcache,mode=max,oci-mediatypes=true,image-manifest=true --push .\necho \"digest=$digest\" >> \"$GITHUB_OUTPUT\"\n"

/// CORRECTION01 P0-5: the verify script must write through
/// ``$GITHUB_OUTPUT`` so the CP-27 baseline proof passes.
let private compliantVerify =
    "#!/usr/bin/env bash\nset -euo pipefail\ndocker pull \"${IMAGE_REPOSITORY}@${digest}\"\ndocker image inspect \"${IMAGE_REPOSITORY}@${digest}\"\necho architecture\necho \"verify_status=ok\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantVerifyPublished =
    "#!/usr/bin/env bash\nset -euo pipefail\necho architecture\n"

/// CORRECTION01 P0-5: the wire script must write through
/// ``$GITHUB_OUTPUT`` so the CP-27 baseline proof passes.
let private compliantWireScript =
    "#!/usr/bin/env bash\nset -euo pipefail\necho builder\necho \"builder=$BUILDER_NAME\" >> \"$GITHUB_OUTPUT\"\necho \"driver=docker-container\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantShellTest =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" == \"true\" ]; then echo publish; fi\nif [ \"$PUBLISH\" == \"false\" ]; then echo skip; fi\nPUBLISH=true ./scripts/ci/wire_buildx_builder.sh\nPUBLISH=false ./scripts/ci/wire_buildx_builder.sh\necho \"builder=$BUILDER_NAME\" >> \"$GITHUB_OUTPUT\"\n"

/// CORRECTION01 P0-5: the metadata script must contain
/// ``GITHUB_SHA`` and ``local-${sha}`` for the CP-18 baseline proof.
let private compliantMetadata =
    "#!/usr/bin/env bash\nset -euo pipefail\nsha=${GITHUB_SHA}\nlocal-${sha}\nv${release}\n${release}\n${major}.${minor}\n${major}\nif [ \"$GITHUB_REF\" = \"refs/heads/main\" ]; then echo latest; fi\n"

/// CORRECTION01 P0-5: the frontend Dockerfile must include the
/// literal ``Elm ${ELM_VERSION}`` marker for the CP-21 baseline
/// proof.
let private compliantFrontend =
    "FROM node:20 AS build\n" +
    "WORKDIR /app\n" +
    "RUN --mount=type=secret,id=spbnix-ca,target=/run/secrets/spbnix-ca \\\n" +
    "    if [ -s /run/secrets/spbnix-ca ]; then \\\n" +
    "      cp /run/secrets/spbnix-ca /etc/ssl/certs/ca-certificates.crt; \\\n" +
    "      cp /run/secrets/spbnix-ca /tmp/circus-ca-bundle.pem; \\\n" +
    "    fi\n" +
    "RUN npm ci --ignore-scripts\n" +
    "RUN node node_modules/elm/install.js\n" +
    "ARG ELM_VERSION=0.19.2\n" +
    "RUN ./node_modules/.bin/elm --version | grep -q \"Elm ${ELM_VERSION}\"\n" +
    "FROM nginx:alpine AS runtime\n" +
    "COPY --from=build /app/dist /usr/share/nginx/html\n" +
    "ENV NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca\n" +
    "ENV SSL_CERT_FILE=/tmp/circus-ca-bundle.pem\n" +
    "RUN rm -f /tmp/circus-ca-bundle.pem\n" +
    "USER 1000:1000\n" +
    "EXPOSE 8080\n"

let private compliantSmoke =
    "#!/usr/bin/env bash\nset -euo pipefail\ncurl /health/live\ncurl /healthz\ncurl GET /\necho '<title>The Circus</title>'\n"

let private compliantAcceptanceTest =
    "#!/usr/bin/env bash\nset -euo pipefail\necho 'leamas factory digest'\necho 'overall_status=pass'\necho 'checks_passed'\necho 'checks_unavailable'\necho pass\necho fail\necho skip\necho unavailable\n"

// ---------------------------------------------------------------------------
// Baseline materialisers — every Result is composed via result-computation
// expression so a single failure becomes the case's BaselinePreparationFailed
// without being silently swallowed.
// ---------------------------------------------------------------------------

let private baselineWorkflowOnly (root: string) : Result<unit, string> =
    (writeAndHash root ".github/workflows/harbor.yml" compliantHarbor) |> Result.bind (fun (_) -> (writeAndHash root ".dockerignore" compliantDockerignore) |> Result.bind (fun (_) -> Ok (())))

let private baselineBothWorkflows (root: string) : Result<unit, string> =
    (writeAndHash root ".github/workflows/harbor.yml" compliantHarbor) |> Result.bind (fun (_) -> (writeAndHash root ".github/workflows/harbor-build-image.yml" compliantReusable) |> Result.bind (fun (_) -> (writeAndHash root ".dockerignore" compliantDockerignore) |> Result.bind (fun (_) -> Ok (()))))

let private baselineWithCa (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root ".github/scripts/install-spbnix-harbor-ca.sh" compliantCaScript) |> Result.bind (fun (_) -> (makeExecutable root ".github/scripts/install-spbnix-harbor-ca.sh") |> Result.bind (fun (_) -> Ok (()))))

let private baselineWithPassword (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root ".github/workflows/harbor-build-image.yml" compliantReusableWithPassword) |> Result.bind (fun (_) -> Ok (())))

let private baselineWithMetadata (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root ".github/scripts/harbor-metadata.sh" compliantMetadata) |> Result.bind (fun (_) -> (makeExecutable root ".github/scripts/harbor-metadata.sh") |> Result.bind (fun (_) -> Ok (()))))

let private baselineWithFrontend (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root "Dockerfile.frontend" compliantFrontend) |> Result.bind (fun (_) -> Ok (())))

let private baselineWithSmoke (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root "scripts/container-smoke.sh" compliantSmoke) |> Result.bind (fun (_) -> (makeExecutable root "scripts/container-smoke.sh") |> Result.bind (fun (_) -> Ok (()))))

let private baselineWithVerify (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root "scripts/ci/verify_build_image.sh" compliantVerify) |> Result.bind (fun (_) -> (makeExecutable root "scripts/ci/verify_build_image.sh") |> Result.bind (fun (_) -> (writeAndHash root "scripts/verify-published-image.sh" compliantVerifyPublished) |> Result.bind (fun (_) -> (makeExecutable root "scripts/verify-published-image.sh") |> Result.bind (fun (_) -> Ok (()))))))

let private baselineFullScriptSurface (root: string) : Result<unit, string> =
    (baselineBothWorkflows root) |> Result.bind (fun (_) -> (writeAndHash root "scripts/ci/build_image.sh" compliantBuildScript) |> Result.bind (fun (_) -> (makeExecutable root "scripts/ci/build_image.sh") |> Result.bind (fun (_) -> (writeAndHash root "scripts/ci/publish_image.sh" compliantPublishScript) |> Result.bind (fun (_) -> (makeExecutable root "scripts/ci/publish_image.sh") |> Result.bind (fun (_) -> (writeAndHash root "scripts/ci/verify_build_image.sh" compliantVerify) |> Result.bind (fun (_) -> (makeExecutable root "scripts/ci/verify_build_image.sh") |> Result.bind (fun (_) -> (writeAndHash root "scripts/ci/wire_buildx_builder.sh" compliantWireScript) |> Result.bind (fun (_) -> (makeExecutable root "scripts/ci/wire_buildx_builder.sh") |> Result.bind (fun (_) -> Ok (()))))))))))

let private baselineFullSurfaceAndAcceptance (root: string) : Result<unit, string> =
    (baselineFullScriptSurface root) |> Result.bind (fun (_) -> (writeAndHash root "tests/ci/test_build_publish_shell.sh" compliantShellTest) |> Result.bind (fun (_) -> (makeExecutable root "tests/ci/test_build_publish_shell.sh") |> Result.bind (fun (_) -> (writeAndHash root "tests/ci/test_gate_summary_acceptance.sh" compliantAcceptanceTest) |> Result.bind (fun (_) -> (makeExecutable root "tests/ci/test_gate_summary_acceptance.sh") |> Result.bind (fun (_) -> Ok (()))))))

// ---------------------------------------------------------------------------
// Mutator helpers — every mutator returns a non-vacuous receipt
// ---------------------------------------------------------------------------

let private replaceFile (root: string) (rel: string) (newContent: string)
    : Result<MutationReceipt, string> =
    writeAndHash root rel newContent
    |> Result.map (fun (b, a) ->
        {
            ChangedPaths = [ rel ]
            BeforeHashes = Map.ofList [ rel, b ]
            AfterHashes = Map.ofList [ rel, a ]
        })

// ---------------------------------------------------------------------------
// 22-case registry
// ---------------------------------------------------------------------------

let mutationCases : MutationCase list = [
    { Id = MutationCaseId.fromString "CP-04_workflow_triggers"
      Description = "missing pull_request and workflow_dispatch triggers"
      ExpectedCheckId = "CP-04_workflow_triggers"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineWorkflowOnly
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor.yml"
          ("name: harbor\non:\n  push:\n    branches:\n      - main\n      - 'v*'\n" +
           "  trusted-runner-probe:\n    runs-on: spbnix-k8s-docker\n    steps:\n      - run: echo spbnix-k8s-docker\n" +
           "permissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n") }

    { Id = MutationCaseId.fromString "CP-05_push_main"
      Description = "unrestricted push branches"
      ExpectedCheckId = "CP-05_push_main"
      AllowedAdditionalCheckIds = set [ "CP-05_push_tags" ]
      PrepareBaseline = baselineWorkflowOnly
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor.yml"
          ("name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - '**'\n" +
           "  workflow_dispatch:\n  trusted-runner-probe:\n    runs-on: spbnix-k8s-docker\n" +
           "    steps:\n      - run: echo spbnix-k8s-docker\n" +
           "permissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n") }

    { Id = MutationCaseId.fromString "CP-06_minimal_permissions"
      Description = "non-read permissions"
      ExpectedCheckId = "CP-06_minimal_permissions"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineWorkflowOnly
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor.yml"
          ("name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n      - 'v*'\n" +
           "  workflow_dispatch:\n  trusted-runner-probe:\n    runs-on: spbnix-k8s-docker\n" +
           "    steps:\n      - run: echo spbnix-k8s-docker\n" +
           "permissions:\n  contents: write\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n") }

    { Id = MutationCaseId.fromString "CP-07_concurrency"
      Description = "non-reference-scoped concurrency"
      ExpectedCheckId = "CP-07_concurrency"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineWorkflowOnly
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor.yml"
          ("name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n      - 'v*'\n" +
           "  workflow_dispatch:\n  trusted-runner-probe:\n    runs-on: spbnix-k8s-docker\n" +
           "    steps:\n      - run: echo spbnix-k8s-docker\n" +
           "permissions:\n  contents: read\nconcurrency:\n  group: circus-global\n") }

    { Id = MutationCaseId.fromString "CP-08_reusable_inputs"
      Description = "missing reusable workflow inputs"
      ExpectedCheckId = "CP-08_reusable_inputs"
      AllowedAdditionalCheckIds = set [ "CP-08_reusable_push_type" ]
      PrepareBaseline = baselineBothWorkflows
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor-build-image.yml"
          "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n" }

    { Id = MutationCaseId.fromString "CP-10_trusted_runner"
      Description = "untrusted runner label"
      ExpectedCheckId = "CP-10_trusted_runner"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineBothWorkflows
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor-build-image.yml"
          ("name: x\non:\n  workflow_call:\n    inputs:\n" +
           "      image_name:\n        type: string\n" +
           "      dockerfile:\n        type: string\n" +
           "      context:\n        type: string\n" +
           "      cache_name:\n        type: string\n" +
           "      push:\n        type: boolean\n" +
           "      platform:\n        type: string\n" +
           "      smoke_test_kind:\n        type: string\n" +
           "jobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n") }

    { Id = MutationCaseId.fromString "CP-11_harbor_naming"
      Description = "missing harbor repository contract"
      ExpectedCheckId = "CP-11_harbor_naming"
      AllowedAdditionalCheckIds = set [ "CP-11_harbor_image_contract" ]
      PrepareBaseline = baselineBothWorkflows
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor-build-image.yml"
          ("name: x\non:\n  workflow_call:\n    inputs:\n" +
           "      image_name:\n        type: string\n" +
           "      dockerfile:\n        type: string\n" +
           "      context:\n        type: string\n" +
           "      cache_name:\n        type: string\n" +
           "      push:\n        type: boolean\n" +
           "      platform:\n        type: string\n" +
           "      smoke_test_kind:\n        type: string\n") }

    { Id = MutationCaseId.fromString "CP-12_password_stdin"
      Description = "password-stdin not used"
      ExpectedCheckId = "CP-12_password_stdin"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineWithPassword
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor-build-image.yml"
          (compliantReusableWithPassword.Replace("--password-stdin", "--password \"${HARBOR_PASSWORD}\"")) }

    { Id = MutationCaseId.fromString "CP-14_ca_secret"
      Description = "CA secret missing"
      ExpectedCheckId = "CP-14_ca_secret"
      AllowedAdditionalCheckIds = set [ "CP-14_buildkit_config"; "CP-14_buildkit_registry" ]
      PrepareBaseline = baselineWithCa
      ApplyMutation = fun root ->
        replaceFile root ".github/scripts/install-spbnix-harbor-ca.sh" "echo no-ca\n" }

    { Id = MutationCaseId.fromString "CP-15_cache_distinct"
      Description = "shared cache reference"
      ExpectedCheckId = "CP-15_cache_distinct"
      AllowedAdditionalCheckIds = set [ "CP-15_cache_template"; "CP-15_cache_image_specific" ]
      PrepareBaseline = baselineBothWorkflows
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor.yml"
          (compliantHarbor.Replace("cache_name: circus-backend", "cache_name: circus-shared")
                            .Replace("cache_name: circus-frontend", "cache_name: circus-shared")) }

    { Id = MutationCaseId.fromString "CP-16_publish_gating"
      Description = "build script lacks PUBLISH gate"
      ExpectedCheckId = "CP-16_publish_gating"
      AllowedAdditionalCheckIds = set [
          "CP-16_build_publish_marker"
          "CP-16_build_compare"
          "CP-16_publish_publish_marker"
          "CP-16_publish_compare"
          "CP-16_reusable_publish_forward" ]
      PrepareBaseline = baselineFullScriptSurface
      ApplyMutation = fun root ->
        replaceFile root "scripts/ci/build_image.sh" "echo no-publish\n" }

    { Id = MutationCaseId.fromString "CP-17_cache_import_export"
      Description = "build script lacks cache-from"
      ExpectedCheckId = "CP-17_cache_import_export"
      AllowedAdditionalCheckIds = set [
          "CP-17_cache_from"
          "CP-17_cache_to"
          "CP-17_cache_mode_max"
          "CP-17_cache_oci_manifest" ]
      PrepareBaseline = baselineFullScriptSurface
      ApplyMutation = fun root ->
        replaceFile root "scripts/ci/build_image.sh" "docker build .\n" }

    { Id = MutationCaseId.fromString "CP-18_immutable_tag"
      Description = "metadata script lacks SHA / release tags"
      ExpectedCheckId = "CP-18_immutable_tag"
      AllowedAdditionalCheckIds = set [ "CP-18_release_tag"; "CP-18_trusted_guard" ]
      PrepareBaseline = baselineWithMetadata
      ApplyMutation = fun root ->
        replaceFile root ".github/scripts/harbor-metadata.sh" "echo v0.1.0\n" }

    { Id = MutationCaseId.fromString "CP-19_latest_main_only"
      Description = "latest emitted twice"
      ExpectedCheckId = "CP-19_latest_main_only"
      AllowedAdditionalCheckIds = set [ "CP-19_latest_present"; "CP-19_latest_unique" ]
      PrepareBaseline = baselineWithMetadata
      ApplyMutation = fun root ->
        replaceFile root ".github/scripts/harbor-metadata.sh"
          "echo local-AAA\necho v0\necho 0\necho 0.0\necho 0\nif [ \"$GITHUB_REF\" = \"refs/heads/main\" ]; then echo latest; fi\nif [ \"$GITHUB_REF\" = \"refs/heads/main\" ]; then echo latest; fi\n" }

    { Id = MutationCaseId.fromString "CP-20_secret_marker"
      Description = "frontend Dockerfile missing CA mount markers"
      ExpectedCheckId = "CP-20_secret_marker"
      AllowedAdditionalCheckIds = set [ "CP-20_update_ca"; "CP-20_legacy_path" ]
      PrepareBaseline = baselineWithFrontend
      ApplyMutation = fun root ->
        replaceFile root "Dockerfile.frontend"
          ("FROM node:20\nUSER 1000:1000\nRUN npm ci --ignore-scripts\n" +
           "ARG ELM_VERSION=0.19.2\nRUN echo Elm\nEXPOSE 8080\nCMD [\"nginx\"]\n") }

    { Id = MutationCaseId.fromString "CP-21_elm_marker"
      Description = "frontend Dockerfile missing Elm install markers"
      ExpectedCheckId = "CP-21_elm_marker"
      AllowedAdditionalCheckIds = set [ "CP-21_elm_version" ]
      PrepareBaseline = baselineWithFrontend
      ApplyMutation = fun root ->
        replaceFile root "Dockerfile.frontend"
          ("FROM node:20 AS build\nWORKDIR /app\n" +
           "RUN --mount=type=secret,id=spbnix-ca,target=/run/secrets/spbnix-ca \\\n" +
           "    if [ -s /run/secrets/spbnix-ca ]; then \\\n" +
           "      cp /run/secrets/spbnix-ca /etc/ssl/certs/ca-certificates.crt; \\\n" +
           "      cp /run/secrets/spbnix-ca /tmp/circus-ca-bundle.pem; \\\n" +
           "    fi\n" +
           "ENV NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca\n" +
           "ENV SSL_CERT_FILE=/tmp/circus-ca-bundle.pem\n" +
           "RUN rm -f /tmp/circus-ca-bundle.pem\n" +
           "EXPOSE 8080\n" +
           "CMD [\"nginx\"]\n") }

    { Id = MutationCaseId.fromString "CP-24_backend_smoke"
      Description = "smoke script missing endpoint contracts"
      ExpectedCheckId = "CP-24_backend_smoke"
      AllowedAdditionalCheckIds = set [ "CP-24_frontend_smoke" ]
      PrepareBaseline = baselineWithSmoke
      ApplyMutation = fun root ->
        replaceFile root "scripts/container-smoke.sh" "echo no-smoke\n" }

    { Id = MutationCaseId.fromString "CP-25_digest_pull"
      Description = "verify script lacks digest pull/inspect"
      ExpectedCheckId = "CP-25_digest_pull"
      AllowedAdditionalCheckIds = set [ "CP-25_digest_inspect"; "CP-25_amd64_verify" ]
      PrepareBaseline = baselineWithVerify
      ApplyMutation = fun root ->
        replaceFile root "scripts/ci/verify_build_image.sh" "docker pull IMAGE\n" }

    { Id = MutationCaseId.fromString "CP-26_seam_step"
      Description = "reusable workflow missing canonical seam step"
      ExpectedCheckId = "CP-26_seam_step"
      AllowedAdditionalCheckIds = set [ "CP-26_seam_forward" ]
      PrepareBaseline = baselineBothWorkflows
      ApplyMutation = fun root ->
        replaceFile root ".github/workflows/harbor-build-image.yml"
          (compliantReusable.Replace("Create configured BuildKit builder", "Some Other Step")) }

    { Id = MutationCaseId.fromString "CP-27_github_output"
      Description = "build script does not use GITHUB_OUTPUT"
      ExpectedCheckId = "CP-27_github_output"
      AllowedAdditionalCheckIds = set [ "CP-27_workflow_output" ]
      PrepareBaseline = baselineFullScriptSurface
      ApplyMutation = fun root ->
        replaceFile root "scripts/ci/build_image.sh" "docker buildx build .\n" }

    { Id = MutationCaseId.fromString "CP-30_final_stage_material"
      Description = "frontend final stage mentions node_modules"
      ExpectedCheckId = "CP-30_final_stage_material"
      AllowedAdditionalCheckIds = Set.empty
      PrepareBaseline = baselineWithFrontend
      ApplyMutation = fun root ->
        replaceFile root "Dockerfile.frontend"
          ("FROM node:20 AS build\nWORKDIR /app\nCOPY . .\nRUN npm ci\n" +
           "FROM nginx:alpine AS runtime\n" +
           "COPY --from=build /app/node_modules /app/node_modules\n" +
           "USER 1000:1000\nEXPOSE 8080\n") }

    { Id = MutationCaseId.fromString "CP-31_acceptance_marker"
      Description = "acceptance test missing required markers"
      ExpectedCheckId = "CP-31_acceptance_marker"
      AllowedAdditionalCheckIds = set [
          "CP-31_acceptance_vocab"
          "CP-31_publish_branch_coverage"
          "CP-31_wire_coverage"
          "CP-31_github_output_assertion" ]
      PrepareBaseline = baselineFullSurfaceAndAcceptance
      ApplyMutation = fun root ->
        replaceFile root "tests/ci/test_gate_summary_acceptance.sh" "echo incomplete\n" }
]
