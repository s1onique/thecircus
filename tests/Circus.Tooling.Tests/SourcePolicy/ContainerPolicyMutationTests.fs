module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationTests

/// Non-vacuous negative mutation tests with an authoritative registry
/// (CORRECTION01 §P0-5).

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.ContainerPolicy

// ---------------------------------------------------------------------------
// Helpers
// ---------------------------------------------------------------------------

let private newTempRepo () : string =
    let path = Path.Combine(Path.GetTempPath(),
        "circus-cp-mut-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory path |> ignore
    path

let private writeFile (root: string) (rel: string) (content: string) =
    let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
    let dir = Path.GetDirectoryName full
    if not (Directory.Exists dir) then Directory.CreateDirectory dir |> ignore
    File.WriteAllText(full, content)

let private makeExecutable (root: string) (rel: string) =
    let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
    try
        let info = new FileInfo(full)
        info.UnixFileMode <- UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
        info.UnixFileMode <- info.UnixFileMode ||| UnixFileMode.GroupExecute ||| UnixFileMode.OtherExecute
    with _ -> ()

// ---------------------------------------------------------------------------
// Compliant reference fixtures
// ---------------------------------------------------------------------------

let private compliantDockerignore =
    ".git\n.github\n.factory\n**/bin\n**/obj\n**/node_modules\n**/elm-stuff\n**/TestResults\n.env\n.env.*\n*.pem\n*.key\n*.crt\n"

/// Truly compliant ``.github/workflows/harbor.yml`` — every rule that
/// inspects this file (CP-04, CP-05, CP-06, CP-07, CP-11, CP-15) must
/// pass on this baseline.  Mutations only break one of those rules at
/// a time.
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
    "      cache_name: circus-frontend\n"

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
    "      - name: Create configured BuildKit builder\n" +
    "        env:\n          PUBLISH: ${{ steps.metadata.outputs.publish }}\n" +
    "          BUILDER_NAME: circus-${{ github.run_id }}\n" +
    "          BUILDKITD_CONFIG: ${{ steps.harbor_ca.outputs.path }}\n" +
    "        run: ./scripts/ci/wire_buildx_builder.sh\n" +
    "      - name: Build testable image with repository cache\n" +
    "        env:\n          BUILDER_NAME: ${{ steps.builder.outputs.builder }}\n" +
    "        run: ./scripts/ci/build_image.sh\n" +
    "      - name: Publish tested revision with provenance and SBOM attempt\n" +
    "        if: steps.metadata.outputs.publish == 'true'\n" +
    "        env:\n          PUBLISH: ${{ steps.metadata.outputs.publish }}\n" +
    "          BUILDER_NAME: ${{ steps.builder.outputs.builder }}\n" +
    "        run: ./scripts/ci/publish_image.sh\n"

let private compliantReusableWithPassword =
    compliantReusable +
    "      - name: Login\n" +
    "        env:\n          HARBOR_PASSWORD: ${{ secrets.HARBOR_PASSWORD }}\n" +
    "        run: echo \"$HARBOR_PASSWORD\" | docker login harbor-pve1.spbnix.local --username circus --password-stdin\n"

let private compliantCaScript =
    "#!/usr/bin/env bash\nset -euo pipefail\necho SPBNIX_CA_CERT_PEM\necho buildkitd.toml\necho [registry.\"${HARBOR_HOST}\"]\n"

let private compliantBuildScript =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" = \"true\" ]; then echo publish; else echo build; fi\ndocker buildx build --cache-from type=registry,ref=harbor-pve1.spbnix.local/circus/cache/x:buildcache .\necho \"builder=$BUILDER_NAME\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantPublishScript =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" = \"true\" ]; then echo publish; else echo skip; fi\ndocker buildx build --cache-to type=registry,ref=harbor-pve1.spbnix.local/circus/cache/x:buildcache,mode=max,oci-mediatypes=true,image-manifest=true --push .\necho \"digest=$digest\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantVerify =
    "#!/usr/bin/env bash\nset -euo pipefail\ndocker pull \"${IMAGE_REPOSITORY}@${digest}\"\ndocker image inspect \"${IMAGE_REPOSITORY}@${digest}\"\necho architecture\n"

let private compliantVerifyPublished =
    "#!/usr/bin/env bash\nset -euo pipefail\necho architecture\n"

let private compliantWireScript =
    "#!/usr/bin/env bash\nset -euo pipefail\necho builder\n"

let private compliantShellTest =
    "#!/usr/bin/env bash\nset -euo pipefail\nif [ \"$PUBLISH\" = \"true\" ]; then echo publish; fi\nif [ \"$PUBLISH\" = \"false\" ]; then echo skip; fi\n./scripts/ci/wire_buildx_builder.sh\necho \"builder=$BUILDER_NAME\" >> \"$GITHUB_OUTPUT\"\n"

let private compliantMetadata =
    "#!/usr/bin/env bash\nset -euo pipefail\nlocal-${sha}\nv${release}\n${release}\n${major}.${minor}\n${major}\nif [ \"$GITHUB_REF\" = \"refs/heads/main\" ]; then echo latest; fi\n"

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
    "RUN ./node_modules/.bin/elm --version | grep -q \"Elm 0.19.2\"\n" +
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

let private baselineWorkflowOnly (root: string) =
    writeFile root ".github/workflows/harbor.yml" compliantHarbor
    writeFile root ".dockerignore" compliantDockerignore

let private baselineBothWorkflows (root: string) =
    writeFile root ".github/workflows/harbor.yml" compliantHarbor
    writeFile root ".github/workflows/harbor-build-image.yml" compliantReusable
    writeFile root ".dockerignore" compliantDockerignore

let private baselineFullScriptSurface (root: string) =
    baselineBothWorkflows root
    writeFile root "scripts/ci/build_image.sh" compliantBuildScript
    makeExecutable root "scripts/ci/build_image.sh"
    writeFile root "scripts/ci/publish_image.sh" compliantPublishScript
    makeExecutable root "scripts/ci/publish_image.sh"
    writeFile root "scripts/ci/verify_build_image.sh" compliantVerify
    makeExecutable root "scripts/ci/verify_build_image.sh"
    writeFile root "scripts/ci/wire_buildx_builder.sh" compliantWireScript
    makeExecutable root "scripts/ci/wire_buildx_builder.sh"

// ---------------------------------------------------------------------------
// Authoritative mutation registry
// ---------------------------------------------------------------------------

type MutationCase = {
    Id: string
    Baseline: string -> unit
    Mutate: string -> unit
    ExpectedViolationIds: string list
    Description: string
}

let mutationRegistry : MutationCase list = [
    { Id = "CP-04_workflow_triggers"
      Baseline = baselineWorkflowOnly
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor.yml"
          "name: harbor\non:\n  push:\n    branches:\n      - main\n      - 'v*'\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n"
      ExpectedViolationIds = [ "CP-04_workflow_triggers" ]
      Description = "missing pull_request and workflow_dispatch triggers" }

    { Id = "CP-05_push_main"
      Baseline = baselineWorkflowOnly
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor.yml"
          "name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - '**'\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n"
      ExpectedViolationIds = [ "CP-05_push_main" ]
      Description = "unrestricted push branches" }

    { Id = "CP-06_minimal_permissions"
      Baseline = baselineWorkflowOnly
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor.yml"
          "name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n      - 'v*'\n  workflow_dispatch:\npermissions:\n  contents: write\nconcurrency:\n  group: circus-harbor-${{ github.ref }}\n"
      ExpectedViolationIds = [ "CP-06_minimal_permissions" ]
      Description = "non-read permissions" }

    { Id = "CP-07_concurrency"
      Baseline = baselineWorkflowOnly
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor.yml"
          "name: harbor\non:\n  pull_request:\n  push:\n    branches:\n      - main\n      - 'v*'\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-global\n"
      ExpectedViolationIds = [ "CP-07_concurrency" ]
      Description = "non-reference-scoped concurrency" }

    { Id = "CP-08_reusable_inputs"
      Baseline = baselineBothWorkflows
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor-build-image.yml"
          "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n"
      ExpectedViolationIds = [
        "CP-08_reusable_inputs"
        "CP-08_reusable_push_type"
      ]
      Description = "missing reusable workflow inputs" }

    { Id = "CP-10_trusted_runner"
      Baseline = baselineBothWorkflows
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor-build-image.yml"
          "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n"
      ExpectedViolationIds = [ "CP-10_trusted_runner" ]
      Description = "untrusted runner label" }

    { Id = "CP-11_harbor_naming"
      Baseline = baselineBothWorkflows
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor-build-image.yml"
          "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\n"
      ExpectedViolationIds = [ "CP-11_harbor_naming" ]
      Description = "missing harbor repository contract" }

    { Id = "CP-12_password_stdin"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root ".github/workflows/harbor-build-image.yml" compliantReusableWithPassword
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor-build-image.yml"
          (compliantReusableWithPassword.Replace("--password-stdin", "--password \"${HARBOR_PASSWORD}\""))
      ExpectedViolationIds = [ "CP-12_password_stdin" ]
      Description = "password-stdin not used" }

    { Id = "CP-14_ca_secret"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root ".github/scripts/install-spbnix-harbor-ca.sh" compliantCaScript
        makeExecutable root ".github/scripts/install-spbnix-harbor-ca.sh"
      Mutate = fun root ->
        writeFile root ".github/scripts/install-spbnix-harbor-ca.sh" "echo no-ca\n"
        makeExecutable root ".github/scripts/install-spbnix-harbor-ca.sh"
      ExpectedViolationIds = [
        "CP-14_ca_secret"
        "CP-14_buildkit_config"
        "CP-14_buildkit_registry"
        "CP-14_reusable_ca"
      ]
      Description = "CA secret missing" }

    { Id = "CP-15_cache_distinct"
      Baseline = baselineBothWorkflows
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor.yml"
          (compliantHarbor.Replace("cache_name: circus-backend", "cache_name: circus-shared").Replace("cache_name: circus-frontend", "cache_name: circus-shared"))
      ExpectedViolationIds = [ "CP-15_cache_distinct" ]
      Description = "shared cache reference" }

    { Id = "CP-16_publish_gating"
      Baseline = baselineFullScriptSurface
      Mutate = fun root ->
        writeFile root "scripts/ci/build_image.sh" "echo no-publish\n"
        makeExecutable root "scripts/ci/build_image.sh"
      ExpectedViolationIds = [
        "CP-16_build_publish_marker"
        "CP-16_build_compare"
      ]
      Description = "build script lacks PUBLISH gate" }

    { Id = "CP-17_cache_import_export"
      Baseline = baselineFullScriptSurface
      Mutate = fun root ->
        writeFile root "scripts/ci/build_image.sh" "docker build .\n"
        makeExecutable root "scripts/ci/build_image.sh"
      ExpectedViolationIds = [ "CP-17_cache_from" ]
      Description = "build script lacks cache-from" }

    { Id = "CP-18_immutable_tag"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root ".github/scripts/harbor-metadata.sh" compliantMetadata
        makeExecutable root ".github/scripts/harbor-metadata.sh"
      Mutate = fun root ->
        writeFile root ".github/scripts/harbor-metadata.sh" "echo v0.1.0\n"
        makeExecutable root ".github/scripts/harbor-metadata.sh"
      ExpectedViolationIds = [
        "CP-18_immutable_tag"
        "CP-18_release_tag"
        "CP-18_trusted_guard"
      ]
      Description = "metadata script lacks SHA / release tags" }

    { Id = "CP-19_latest_main_only"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root ".github/scripts/harbor-metadata.sh" compliantMetadata
        makeExecutable root ".github/scripts/harbor-metadata.sh"
      Mutate = fun root ->
        writeFile root ".github/scripts/harbor-metadata.sh"
          "echo local-AAA\necho v0\necho 0\necho 0.0\necho 0\nif [ BBB = CCC ]; then\necho latest\necho latest\nfi\n"
        makeExecutable root ".github/scripts/harbor-metadata.sh"
      ExpectedViolationIds = [
        "CP-19_latest_unique"
      ]
      Description = "latest emitted twice" }

    { Id = "CP-20_secret_marker"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "Dockerfile.frontend" compliantFrontend
      Mutate = fun root ->
        writeFile root "Dockerfile.frontend"
          "FROM node:20\nUSER 1000:1000\nRUN npm ci --ignore-scripts\nARG ELM_VERSION=0.19.2\nRUN echo Elm\nEXPOSE 8080\nCMD [\"nginx\"]\n"
      ExpectedViolationIds = [ "CP-20_secret_marker" ]
      Description = "frontend Dockerfile missing CA mount markers" }

    { Id = "CP-21_elm_marker"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "Dockerfile.frontend" compliantFrontend
      Mutate = fun root ->
        writeFile root "Dockerfile.frontend"
          "FROM node:20\nUSER 1000:1000\nRUN --mount=type=secret,id=spbnix-ca,target=/run/secrets/spbnix-ca \\\n    if [ -s /run/secrets/spbnix-ca ]; then \\\n      cp /run/secrets/spbnix-ca /etc/ssl/certs/ca-certificates.crt; \\\n      cp /run/secrets/spbnix-ca /tmp/circus-ca-bundle.pem; \\\n    fi\nENV NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca\nENV SSL_CERT_FILE=/tmp/circus-ca-bundle.pem\nRUN rm -f /tmp/circus-ca-bundle.pem\nEXPOSE 8080\nCMD [\"nginx\"]\n"
      ExpectedViolationIds = [ "CP-21_elm_marker" ]
      Description = "frontend Dockerfile missing Elm install markers" }

    { Id = "CP-24_backend_smoke"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "scripts/container-smoke.sh" compliantSmoke
        makeExecutable root "scripts/container-smoke.sh"
      Mutate = fun root ->
        writeFile root "scripts/container-smoke.sh" "echo no-smoke\n"
        makeExecutable root "scripts/container-smoke.sh"
      ExpectedViolationIds = [
        "CP-24_backend_smoke"
        "CP-24_frontend_smoke"
      ]
      Description = "smoke script missing endpoint contracts" }

    { Id = "CP-25_digest_pull"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "scripts/ci/verify_build_image.sh" compliantVerify
        makeExecutable root "scripts/ci/verify_build_image.sh"
        writeFile root "scripts/verify-published-image.sh" compliantVerifyPublished
        makeExecutable root "scripts/verify-published-image.sh"
      Mutate = fun root ->
        writeFile root "scripts/ci/verify_build_image.sh" "docker pull IMAGE\n"
        makeExecutable root "scripts/ci/verify_build_image.sh"
      ExpectedViolationIds = [
        "CP-25_digest_pull"
        "CP-25_digest_inspect"
        "CP-25_amd64_verify"
      ]
      Description = "verify script lacks digest pull/inspect" }

    { Id = "CP-26_seam_step"
      Baseline = baselineBothWorkflows
      Mutate = fun root ->
        writeFile root ".github/workflows/harbor-build-image.yml"
          (compliantReusable.Replace("Create configured BuildKit builder", "Some Other Step"))
      ExpectedViolationIds = [ "CP-26_seam_step" ]
      Description = "reusable workflow missing canonical seam step" }

    { Id = "CP-27_github_output"
      Baseline = baselineFullScriptSurface
      Mutate = fun root ->
        writeFile root "scripts/ci/build_image.sh" "docker buildx build .\n"
        makeExecutable root "scripts/ci/build_image.sh"
      ExpectedViolationIds = [ "CP-27_github_output" ]
      Description = "build script does not use GITHUB_OUTPUT" }

    { Id = "CP-30_final_stage_material"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "Dockerfile.frontend" compliantFrontend
      Mutate = fun root ->
        writeFile root "Dockerfile.frontend"
          "FROM node:20 AS build\nWORKDIR /app\nCOPY . .\nRUN npm ci\nFROM nginx:alpine AS runtime\nCOPY --from=build /app/node_modules /app/node_modules\nUSER 1000:1000\nEXPOSE 8080\n"
      ExpectedViolationIds = [ "CP-30_final_stage_material" ]
      Description = "frontend final stage mentions node_modules" }

    { Id = "CP-31_acceptance_marker"
      Baseline = fun root ->
        baselineBothWorkflows root
        writeFile root "scripts/ci/build_image.sh" compliantBuildScript
        makeExecutable root "scripts/ci/build_image.sh"
        writeFile root "scripts/ci/publish_image.sh" compliantPublishScript
        makeExecutable root "scripts/ci/publish_image.sh"
        writeFile root "scripts/ci/verify_build_image.sh" compliantVerify
        makeExecutable root "scripts/ci/verify_build_image.sh"
        writeFile root "scripts/ci/wire_buildx_builder.sh" compliantWireScript
        makeExecutable root "scripts/ci/wire_buildx_builder.sh"
        writeFile root "tests/ci/test_build_publish_shell.sh" compliantShellTest
        makeExecutable root "tests/ci/test_build_publish_shell.sh"
        writeFile root "tests/ci/test_gate_summary_acceptance.sh" compliantAcceptanceTest
        makeExecutable root "tests/ci/test_gate_summary_acceptance.sh"
      Mutate = fun root ->
        writeFile root "tests/ci/test_gate_summary_acceptance.sh" "echo incomplete\n"
        makeExecutable root "tests/ci/test_gate_summary_acceptance.sh"
      ExpectedViolationIds = [ "CP-31_acceptance_marker" ]
      Description = "acceptance test missing required markers" }
]

let private executedCases : ResizeArray<Result<string list, string>> = ResizeArray()

let private recordResult (r: Result<string list, string>) : unit =
    executedCases.Add r

let private accounting () =
    let expected = List.length mutationRegistry
    let executed = executedCases.Count
    let passed =
        executedCases
        |> Seq.filter (function
            | Result.Ok _ -> true
            | _ -> false)
        |> Seq.length
    let unknown =
        mutationRegistry
        |> List.filter (fun c -> not (List.contains c.Id CheckIds))
        |> List.map (fun c -> c.Id)
    let missing =
        (expected - executed) +
        (mutationRegistry
            |> List.filter (fun c ->
                not (executedCases |> Seq.exists (function
                    | Result.Ok _ -> true
                    | _ -> false)))
            |> List.length)
    let duplicates =
        mutationRegistry
        |> List.groupBy (fun c -> c.Id)
        |> List.filter (fun (_, g) -> List.length g > 1)
        |> List.length
    expected, executed, passed, missing, duplicates, unknown

let private runCase (c: MutationCase) : Test =
    test (sprintf "%s detects mutation (negative mutation)" c.Id) {
        let root = newTempRepo ()
        try
            c.Baseline root
            let baselineViolations = runCheckById c.Id root
            Expect.isEmpty
                (baselineViolations |> List.filter (fun v -> List.contains v.Id c.ExpectedViolationIds))
                (sprintf "baseline of %s must already pass the target rule(s): %A" c.Id baselineViolations)
            c.Mutate root
            let violations = runCheckById c.Id root
            let presentIds = violations |> List.map (fun v -> v.Id) |> List.distinct
            let missingIds = c.ExpectedViolationIds |> List.filter (fun id -> not (List.contains id presentIds))
            if not (List.isEmpty missingIds) then
                recordResult (Result.Error (sprintf "missing expected rule ids: %A (got %A)" missingIds presentIds))
                failtestf "case %s expected %A but only observed %A" c.Id c.ExpectedViolationIds presentIds
            else
                recordResult (Result.Ok presentIds)
        finally
            try Directory.Delete(root, true) with _ -> ()
    }

[<Tests>]
let tests =
    testList "Container policy negative mutations" [
        test "registry has 22 cases (mechanical invariant)" {
            Expect.equal (List.length mutationRegistry) 22 "exactly 22 registered cases"
        }

        test "every registered case id is in the production registry" {
            let unknown = mutationRegistry |> List.filter (fun c -> not (List.contains c.Id CheckIds))
            Expect.isEmpty unknown (sprintf "unknown registry ids: %A" (unknown |> List.map (fun c -> c.Id)))
        }

        test "registry has no duplicate ids" {
            let grouped = mutationRegistry |> List.groupBy (fun c -> c.Id)
            let dupes = grouped |> List.filter (fun (_, g) -> List.length g > 1) |> List.map fst
            Expect.isEmpty dupes (sprintf "duplicate registry ids: %A" dupes)
        }

        yield! mutationRegistry |> List.map runCase

        test "all 22 cases passed their expected violations" {
            let expected, executed, passed, missing, duplicates, unknown = accounting ()
            Expect.equal executed expected (sprintf "expected %d executed but got %d" expected executed)
            Expect.equal passed expected (sprintf "passed=%d expected=%d (missing=%d duplicates=%d unknown=%A)" passed expected missing duplicates unknown)
        }
    ]
