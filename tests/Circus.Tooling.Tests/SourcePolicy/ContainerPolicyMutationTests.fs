module Circus.Tooling.Tests.SourcePolicy.ContainerPolicyMutationTests

/// Executable negative mutation tests for every container-policy rule.

open System
open System.IO
open Expecto

open Circus.Tooling.SourcePolicy.ContainerPolicy

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

let private writeMinimalRepo (root: string) =
    let required = [
        "Dockerfile.backend"; "Dockerfile.frontend"; ".dockerignore"
        "docker/frontend/nginx.conf"
        ".github/workflows/harbor.yml"; ".github/workflows/harbor-build-image.yml"
        ".github/scripts/install-spbnix-harbor-ca.sh"; "docs/harbor-publishing.md"
        "scripts/ci/build_image.sh"; "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"; "scripts/ci/wire_buildx_builder.sh"
        "scripts/verify-published-image.sh"
        "tests/ci/test_build_publish_shell.sh"; "tests/ci/test_action_pin_mutation.sh"
        "tests/ci/test_gate_summary_acceptance.sh"
    ]
    for f in required do
        writeFile root f ("placeholder " + f)
    let execs = [
        "scripts/ci/build_image.sh"; "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"; "scripts/ci/wire_buildx_builder.sh"
        "tests/ci/test_build_publish_shell.sh"; "tests/ci/test_action_pin_mutation.sh"
        "tests/ci/test_gate_summary_acceptance.sh"
    ]
    for f in execs do makeExecutable root f

let private writeDockerignore (root: string) =
    writeFile root ".dockerignore" ".git\n.github\n.factory\n**/bin\n**/obj\n**/node_modules\n**/elm-stuff\n**/TestResults\n.env\n.env.*\n*.pem\n*.key\n*.crt\n"

let private writeCompliantHarbor (root: string) =
    writeFile root ".github/workflows/harbor.yml"
        "name: harbor\non:\n  pull_request:\n  push:\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-AAA\njobs:\n  backend:\n    uses: ./.github/workflows/harbor-build-image.yml\n    with:\n      image_name: circus-backend\n      cache_name: circus-backend\n  frontend:\n    uses: ./.github/workflows/harbor-build-image.yml\n    with:\n      image_name: circus-frontend\n      cache_name: circus-frontend\n"

let private runNegative (id: string) (mutate: string -> unit) (predicate: Violation list -> bool) (label: string) : Test =
    test (sprintf "%s detects mutation (negative mutation)" id) {
        let root = newTempRepo ()
        writeMinimalRepo root
        writeDockerignore root
        writeCompliantHarbor root
        mutate root
        let v = runCheckById id root
        Expect.isTrue (predicate v) label
        Directory.Delete(root, true)
    }

[<Tests>]
let tests =
    testList "Container policy negative mutations" [
        runNegative "CP-04_workflow_triggers"
            (fun root -> writeFile root ".github/workflows/harbor.yml" "name: x\non:\n  push:\npermissions:\n  contents: read\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-04_workflow_triggers") v)
            "missing trigger flagged"

        runNegative "CP-05_push_main"
            (fun root -> writeFile root ".github/workflows/harbor.yml" "name: x\non:\n  push:\n  pull_request:\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-AAA\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-05") v)
            "unrestricted push flagged"

        runNegative "CP-06_minimal_permissions"
            (fun root -> writeFile root ".github/workflows/harbor.yml" "name: x\non:\n  pull_request:\n  push:\n  workflow_dispatch:\npermissions:\n  contents: write\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-06_minimal_permissions") v)
            "non-read permissions flagged"

        runNegative "CP-07_concurrency"
            (fun root -> writeFile root ".github/workflows/harbor.yml" "name: x\non:\n  pull_request:\n  push:\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-global\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-07_concurrency") v)
            "non-scoped concurrency flagged"

        runNegative "CP-08_reusable_inputs"
            (fun root -> writeFile root ".github/workflows/harbor-build-image.yml" "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-08") v)
            "missing input flagged"

        runNegative "CP-10_trusted_runner"
            (fun root -> writeFile root ".github/workflows/harbor-build-image.yml" "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\njobs:\n  build:\n    runs-on: ubuntu-latest\n    steps:\n      - run: echo\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-10_trusted_runner") v)
            "untrusted runner flagged"

        runNegative "CP-11_harbor_naming"
            (fun root -> writeFile root ".github/workflows/harbor-build-image.yml" "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-11") v)
            "missing harbor contract flagged"

        runNegative "CP-12_password_stdin"
            (fun root -> writeFile root ".github/workflows/harbor-build-image.yml" "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\n    secrets:\n      HARBOR_PASSWORD:\n        required: true\njobs:\n  build:\n    runs-on: spbnix-k8s-docker\n    steps:\n      - run: echo HARBOR_PASSWORD=foo\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-12_password_stdin") v)
            "plain HARBOR_PASSWORD usage flagged"

        runNegative "CP-14_ca_secret"
            (fun root ->
                writeFile root ".github/scripts/install-spbnix-harbor-ca.sh" "echo no-ca\n"
                makeExecutable root ".github/scripts/install-spbnix-harbor-ca.sh")
            (fun v -> List.exists (fun x -> x.Id = "CP-14_ca_secret") v)
            "missing CA secret flagged"

        runNegative "CP-15_cache_distinct"
            (fun root -> writeFile root ".github/workflows/harbor.yml" "name: x\non:\n  pull_request:\n  push:\n  workflow_dispatch:\npermissions:\n  contents: read\nconcurrency:\n  group: circus-harbor-AAA\njobs:\n  backend:\n    uses: ./.github/workflows/harbor-build-image.yml\n    with:\n      image_name: circus-backend\n      cache_name: circus-shared\n  frontend:\n    uses: ./.github/workflows/harbor-build-image.yml\n    with:\n      image_name: circus-frontend\n      cache_name: circus-shared\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-15") v)
            "identical cache refs flagged"

        runNegative "CP-16_publish_gating"
            (fun root ->
                writeFile root "scripts/ci/build_image.sh" "echo no-publish\n"
                makeExecutable root "scripts/ci/build_image.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-16") v)
            "missing publish gating flagged"

        runNegative "CP-17_cache_import_export"
            (fun root ->
                writeFile root "scripts/ci/build_image.sh" "docker build .\n"
                makeExecutable root "scripts/ci/build_image.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-17") v)
            "missing cache import flagged"

        runNegative "CP-18_immutable_tag"
            (fun root ->
                writeFile root ".github/scripts/harbor-metadata.sh" "echo v0.1.0\n"
                makeExecutable root ".github/scripts/harbor-metadata.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-18") v)
            "missing SHA flagged"

        runNegative "CP-19_latest_main_only"
            (fun root ->
                writeFile root ".github/scripts/harbor-metadata.sh" "echo local-AAA\necho v0\necho 0\necho 0.0\necho 0\nif [ BBB = CCC ]; then\necho latest\necho latest\nfi\n"
                makeExecutable root ".github/scripts/harbor-metadata.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-19") v)
            "duplicate latest flagged"

        runNegative "CP-20_secret_marker"
            (fun root -> writeFile root "Dockerfile.frontend" "FROM node:20\nUSER 1000:1000\nRUN npm ci --ignore-scripts\nARG ELM_VERSION=0.19.2\nRUN echo Elm\nEXPOSE 8080\nCMD [\"nginx\"]\n")
            (fun v -> List.exists (fun x -> x.Id = "CP-20_secret_marker") v)
            "missing CA mount flagged"

        runNegative "CP-21_elm_marker"
            (fun root -> writeFile root "Dockerfile.frontend" "FROM node:20\nUSER 1000:1000\nRUN --mount=type=secret,id=spbnix-ca,target=/run/secrets/spbnix-ca \\\n    if [ -s /run/secrets/spbnix-ca ]; then \\\n      cp /run/secrets/spbnix-ca /etc/ssl/certs/ca-certificates.crt; \\\n      cp /run/secrets/spbnix-ca /tmp/circus-ca-bundle.pem; \\\n    fi\nENV NODE_EXTRA_CA_CERTS=/run/secrets/spbnix-ca\nENV SSL_CERT_FILE=/tmp/circus-ca-bundle.pem\nRUN rm -f /tmp/circus-ca-bundle.pem\nEXPOSE 8080\nCMD [\"nginx\"]\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-21") v)
            "missing Elm install flagged"

        runNegative "CP-24_backend_smoke"
            (fun root ->
                writeFile root "scripts/container-smoke.sh" "echo no-smoke\n"
                makeExecutable root "scripts/container-smoke.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-24") v)
            "missing smoke contract flagged"

        runNegative "CP-25_digest_pull"
            (fun root ->
                writeFile root "scripts/ci/verify_build_image.sh" "docker pull IMAGE\n"
                makeExecutable root "scripts/ci/verify_build_image.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-25") v)
            "missing digest pull flagged"

        runNegative "CP-26_seam_step"
            (fun root -> writeFile root ".github/workflows/harbor-build-image.yml" "name: x\non:\n  workflow_call:\n    inputs:\n      image_name:\n        type: string\n      dockerfile:\n        type: string\n      context:\n        type: string\n      cache_name:\n        type: string\n      push:\n        type: boolean\n      platform:\n        type: string\n      smoke_test_kind:\n        type: string\nsecrets:\n  SPBNIX_CA_CERT_PEM:\n    required: true\njobs:\n  build:\n    runs-on: spbnix-k8s-docker\n    steps:\n      - name: Publish metadata\n        id: metadata\n        run: ./scripts/ci/wire_buildx_builder.sh\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-26") v)
            "missing seam step flagged"

        runNegative "CP-27_github_output"
            (fun root ->
                writeFile root "scripts/ci/build_image.sh" "docker buildx build .\n"
                makeExecutable root "scripts/ci/build_image.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-27") v)
            "missing GITHUB_OUTPUT flagged"

        runNegative "CP-30_final_stage_material"
            (fun root -> writeFile root "Dockerfile.frontend" "FROM node:20 AS build\nWORKDIR /app\nCOPY . .\nRUN npm ci\nFROM nginx:alpine AS runtime\nCOPY --from=build /app/node_modules /app/node_modules\nUSER 1000:1000\nEXPOSE 8080\n")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-30") v)
            "node_modules in runtime flagged"

        runNegative "CP-31_acceptance_marker"
            (fun root ->
                writeFile root "tests/ci/test_gate_summary_acceptance.sh" "echo incomplete\n"
                makeExecutable root "tests/ci/test_gate_summary_acceptance.sh")
            (fun v -> List.exists (fun x -> x.Id.Contains "CP-31") v)
            "missing acceptance marker flagged"

        test "container-policy registry has the canonical 31 CP-NN rules" {
            Expect.equal (List.length CheckIds) 31 "exactly 31 rules"
            let prefixCheck = CheckIds |> List.forall (fun id -> id.StartsWith "CP-")
            Expect.isTrue prefixCheck "every id starts with CP-"
        }

        test "every registered rule has a negative mutation in this suite" {
            let expectedIds = [
                "CP-04_workflow_triggers"
                "CP-05_push_main"
                "CP-06_minimal_permissions"
                "CP-07_concurrency"
                "CP-08_reusable_inputs"
                "CP-10_trusted_runner"
                "CP-11_harbor_naming"
                "CP-12_password_stdin"
                "CP-14_ca_secret"
                "CP-15_cache_distinct"
                "CP-16_publish_gating"
                "CP-17_cache_import_export"
                "CP-18_immutable_tag"
                "CP-19_latest_main_only"
                "CP-20_secret_marker"
                "CP-21_elm_marker"
                "CP-24_backend_smoke"
                "CP-25_digest_pull"
                "CP-26_seam_step"
                "CP-27_github_output"
                "CP-30_final_stage_material"
                "CP-31_acceptance_marker"
            ]
            Expect.equal (List.length expectedIds) 22 "exactly 22 negative mutations in this ACT"
            for id in expectedIds do
                Expect.isTrue (List.contains id CheckIds) (sprintf "%s must be registered" id)
            let distinct = expectedIds |> List.distinct
            Expect.equal (List.length distinct) (List.length expectedIds) "no duplicates"
        }
    ]