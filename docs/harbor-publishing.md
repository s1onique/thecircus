# Circus container images and Harbor publication

## Status and architecture

This repository publishes two single-platform (`linux/amd64`) OCI images to the existing SPbNIX Harbor instance:

| Component | Repository | Runtime |
| --- | --- | --- |
| Backend | `harbor-pve1.spbnix.local/circus/circus-backend` | .NET 10 ASP.NET Core, numeric UID `65532:65532`, port `8080` |
| Frontend | `harbor-pve1.spbnix.local/circus/circus-frontend` | nginx-unprivileged, numeric UID `101:101`, port `8080` |

The final backend stage contains only the `Circus.Api` publish output and the ASP.NET Core runtime. The final frontend stage contains nginx configuration and the generated Elm static distribution (`index.html`, `styles.css`, and optimized `app.js`). There is no Kubernetes, Helm, Argo CD, deployment, migration-at-startup, signing, or multi-architecture behavior in this ACT.

The backend retains the production requirement for a syntactically valid `CIRCUS_DATABASE_URL`, but its `GET /health/live` handler does not access the data source. The container smoke test supplies a deliberately unreachable but valid connection string, so the liveness check does not require PostgreSQL. The application listens on `ASPNETCORE_HTTP_PORTS` (8080 in the image; 5000 remains the local source smoke default).

## Repository facts discovered before implementation

These are repository facts, not assumptions:

* Solution: `Circus.sln`.
* API project: `src/Circus.Api/Circus.Api.fsproj`.
* The production project closure is `Circus.Api` -> `Circus.Domain`, `Circus.Contracts`, `Circus.Application`, and `Circus.Persistence.Postgres`; the latter three transitively use the domain/contracts projects as shown in the project files and lock files. Test projects are not copied into the backend image.
* Central .NET files: `Directory.Build.props`, `Directory.Packages.props`, and `global.json`. The target framework is `net10.0`; the repository requests SDK `10.0.200` with latest patch roll-forward. Each production F# project has a `packages.lock.json`. `src/Circus.Persistence.Postgres/Circus.Persistence.Postgres.fsproj` also embeds the three `db/migrations/*.sql` files; those three files are copied into the backend restore/build context.
* Frontend directory: `web/`. The application entrypoint is `web/src/Main.elm`; `web/elm.json` declares Elm `0.19.2`; `web/package.json` declares the pinned Elm tool packages `elm@0.19.2-0`, `elm-format@0.8.7`, and `elm-test@0.19.2-0`; `web/package-lock.json` is lockfile version 3. The production command is `npm run build`, equivalent to `elm make src/Main.elm --optimize --output=dist/app.js`; tests use `npm test` / `elm-test`.
* The current generated frontend entrypoint is `web/dist/app.js`, assembled with `web/dist/index.html` and `web/dist/styles.css` by the existing Makefile.
* The backend live endpoint is `GET /health/live`, returning `{"status":"live"}`. The original source host explicitly listened on localhost:5000; the implementation now selects `ASPNETCORE_HTTP_PORTS` and binds any interface so the container contract can be 8080 while source smoke remains 5000.
* Existing task targets are in `Makefile`; the factory-generated include remains in place. Container targets added here are `container-build-backend`, `container-build-frontend`, `container-smoke-backend`, `container-smoke-frontend`, and `container-smoke`.
* Before this ACT, this repository had no GitHub Actions workflows and no container files.

## Reference infrastructure and deliberate adaptations

The current local k9b reference was inspected at repository HEAD `3897f56773afe94c85fbcc709e3261394956703c` (2026-07-16). Exact source revisions used as references were:

| Reference | Revision | What was reused or evaluated |
| --- | --- | --- |
| `k9b/.github/workflows/harbor.yml` | `eb4d0c01a21f94c49daac690630cd72a6216ad91` | `harbor-pve1.spbnix.local`, ARC runner convention, workflow split |
| `k9b/.github/workflows/harbor-build-image.yml` | `2201f006a102ea3880de573599ac8b9df5f4b96a` | shell-first Docker/Buildx wiring, Harbor cache shape, `spbnix-k8s-docker` |
| `k9b/.github/scripts/install-spbnix-harbor-ca.sh` | `434dabe4c0f5681648b48d7930b38c5b4315a552` | ARC DinD versus runner-side Docker trust behavior |
| `k9b/scripts/ci/wire_docker_buildx.sh` | `cd3c9a885a6272573a30f8e3da82610bdab96936` | `docker-container` driver and Harbor-proxied BuildKit image convention |
| `k9b/scripts/ci/docker_login.sh` | `aeec6493e167bfd2b86527927a1bac133d4f0b01` | password-stdin shell login pattern |

The k9b workflow is not copied blindly: its current reusable workflow still has mutable action references, short SHA tags, a `HARBOR_TOKEN` name, and a two-platform build without this ACT's digest pull-back/smoke contract. Circus uses a repository-owned, SHA-pinned checkout only and shell-driven Docker commands. The trusted builder uses the current k9b-proxied image convention:

`harbor-pve1.spbnix.local/dockerhub-cache/moby/buildkit:buildx-stable-1`

No local InDeep application source repository exists under the SPbNIX project tree. The local `not-so-awesome-argocd-infra` repository contains InDeep deployment references and the ARC runner definition, not a competing application build workflow. It was not treated as a source implementation.

## Workflow triggers and security boundary

`.github/workflows/harbor.yml` triggers on:

* every `pull_request`;
* pushes to `main`;
* version tags matching `v*`;
* `workflow_dispatch`.

The workflow declares `permissions: contents: read` and uses `circus-harbor-${{ github.ref }}` concurrency with cancellation. The reusable workflow accepts `image_name`, `dockerfile`, `context`, `cache_name`, `push`, `platform`, and the closed `smoke_test_kind` input (`backend` or `frontend`); it never accepts an arbitrary smoke shell command.

Pull requests always select `ubuntu-latest`, use `push=false`, do not run `docker login`, do not execute the CA script, and do not pass non-empty Harbor secrets from the caller. Harbor cache arguments are only constructed after the trusted publication metadata says `publish=true`. No `pull_request_target` trigger is used. Trusted publication is limited to a `main` push, a `vMAJOR.MINOR.PATCH` tag push, or an authorized manual dispatch of `main`/a version tag. These are the only events that select `spbnix-k8s-docker` and receive the Harbor secrets.

The on-premises runner label is exactly `spbnix-k8s-docker`. The workflow records both the requested label and the actual `RUNNER_NAME` in the job summary.

## Harbor credentials and permissions

Configure these GitHub Actions secrets at repository or approved environment scope:

* `HARBOR_USERNAME`
* `HARBOR_PASSWORD`
* `SPBNIX_CA_CERT_PEM`

`HARBOR_PASSWORD` is passed to `docker login` through stdin and is never a build argument, label, artifact, or command-line argument. The expected identity is a Harbor robot account in project `circus` with only pull/push permissions for:

* `circus/circus-backend`;
* `circus/circus-frontend`;
* `circus/cache/circus-backend`;
* `circus/cache/circus-frontend`.

Do not record the account name, password, or certificate value in this document or in the repository.

## CA and BuildKit trust

On a trusted publication, `.github/scripts/install-spbnix-harbor-ca.sh` fails when `SPBNIX_CA_CERT_PEM` is empty, validates the PEM without printing it, and writes an ephemeral BuildKit CA file and `buildkitd.toml` under `RUNNER_TEMP`. Its generated configuration is equivalent to:

```toml
[registry."harbor-pve1.spbnix.local"]
  ca = ["/runner/temp/harbor-pve1.spbnix.local.pem"]
```

The workflow follows the k9b ARC DinD convention and sets `SKIP_RUNNER_DOCKER_CERTS_D=1`; the daemon-side CA mount is then proven with a Harbor Docker pull. On a runner without that convention, the script attempts runner-side `certs.d` and system trust installation when writable. It never uses `--insecure`, disables TLS verification, or changes a BuildKit registry to an insecure mode.

## Tags, labels, and authoritative digests

The SHA tag is the full 40-character `${GITHUB_SHA}` and is always present on a trusted publication:

* `main` push: `${GITHUB_SHA}`, `latest`, `main`;
* `v1.2.3` push: `v1.2.3`, `1.2.3`, `1.2`, `1`, `${GITHUB_SHA}`;
* pull requests and non-publishing manual revisions: local smoke-only tags, never Harbor tags.

`latest` is emitted only by a `main` publication. Tags are convenience references; the registry digest captured from the full-SHA tag is authoritative. The workflow pulls and inspects `repository@sha256:<digest>` and runs the same repository-owned smoke test against that digest.

The workflow supplies these dynamic OCI labels to both Dockerfiles:

* `org.opencontainers.image.title`;
* `org.opencontainers.image.description`;
* `org.opencontainers.image.source`;
* `org.opencontainers.image.revision`;
* `org.opencontainers.image.version`;
* `org.opencontainers.image.created`.

`revision` must equal the full GitHub SHA. The commit timestamp is used for `created`, rather than wall-clock build time, to keep metadata tied to the source revision.

## BuildKit caches

Backend and frontend use separate persistent registry caches:

```text
harbor-pve1.spbnix.local/circus/cache/circus-backend:buildcache
harbor-pve1.spbnix.local/circus/cache/circus-frontend:buildcache
```

Trusted builds use `cache-from=type=registry,ref=...` and `cache-to=type=registry,ref=...,mode=max,oci-mediatypes=true,image-manifest=true`. Pull requests neither import nor export Harbor cache. The local build log is retained in the runner temp directory and the summary identifies the selected cache and any explicit `importing cache manifest` / `exporting cache` output. A cache export failure fails the build; it is not hidden behind `continue-on-error`.

## Local developer contract

The canonical local commands are:

```bash
make container-build-backend
make container-build-frontend
make container-smoke-backend
make container-smoke-frontend
make container-smoke
```

The Makefile defaults to `docker` and `linux/amd64`; override for a Docker-compatible local runtime when needed:

```bash
make CONTAINER_CLI=podman container-smoke
```

The smoke script uses deterministic local ports 18081 (backend) and 18082 (frontend), overridable with `SMOKE_HOST_PORT`, retries for a bounded 60 seconds, checks the running state and numeric non-root UID, prints container logs on failure, and removes the container through an EXIT trap. Backend smoke checks `/health/live` without PostgreSQL. Frontend smoke checks `/` for `<title>The Circus</title>` and checks `/healthz` independently of Elm browser startup.

The existing source-level smoke command remains:

```bash
make smoke
```

The application keeps its source default of port 5000, while the smoke script
uses the overridable local port 18080 by default (`SMOKE_PORT=5000 make smoke`
restores the old port when it is free). It uses the same no-PostgreSQL liveness
arrangement.

## Manual publication procedure

1. Confirm the three secrets exist and the Harbor robot account has only the permissions above.
2. Confirm `harbor-pve1.spbnix.local` resolves from the `spbnix-k8s-docker` runner and that the ARC DinD sidecar has the SPbNIX CA mount.
3. Push to `main` or create a `vMAJOR.MINOR.PATCH` tag, or use **Run workflow** on `main`/that version tag. Do not use an arbitrary feature branch for publication.
4. Open the resulting `Circus Harbor publication` run and record the requested/actual runner, Docker/buildx versions, BuildKit driver/image, cache activity, both image digests, and both digest smoke results from the job summaries.
5. Treat the digest, not `latest`, `main`, or a release convenience tag, as the deployment input for any future deployment ACT.

This ACT does not deploy either image.

## Remote verification

After a trusted run supplies a digest, verify each image from a machine that trusts the SPbNIX CA and has Harbor pull permission:

```bash
export REGISTRY=harbor-pve1.spbnix.local
export PROJECT=circus
export DIGEST=sha256:REPLACE_WITH_RUN_DIGEST

# Repeat with the backend repository and then the frontend repository.
export IMAGE="$REGISTRY/$PROJECT/circus-backend"
docker pull "$IMAGE@$DIGEST"
docker image inspect "$IMAGE@$DIGEST"
CONTAINER_CLI=docker EXPECTED_REVISION=FULL_GITHUB_SHA \
  scripts/verify-published-image.sh backend "$IMAGE" "$DIGEST"
CONTAINER_CLI=docker scripts/container-smoke.sh backend "$IMAGE@$DIGEST"
```

For the frontend, substitute `circus-frontend`, `frontend`, and its own digest. Do not use a mutable tag as a substitute for digest-qualified verification. The inspect contract checks Linux/amd64, port 8080, non-root runtime user, OCI revision/source/all required labels, and the smoke script checks the live HTTP behavior.

## Troubleshooting

### Runner not selected

Check the job summary's requested label and actual runner name. The trusted job must say `spbnix-k8s-docker`; verify the ARC scale set label, runner availability, and that a pull request was not used to test the publication path.

### Harbor certificate errors from Docker

The CA secret must contain a complete PEM certificate. Confirm the DinD sidecar mounts it at `/etc/docker/certs.d/harbor-pve1.spbnix.local/ca.crt`; the workflow's busybox proxy-cache pull is the daemon trust probe. Do not add `--insecure`.

### BuildKit certificate errors

Inspect the CA step output path under `RUNNER_TEMP`, confirm `buildkitd.toml` contains the Harbor registry `ca` mapping, and confirm the builder was created with the Harbor-proxied BuildKit image and `--buildkitd-config`. Do not disable TLS verification.

### Authentication failures

Check the robot account, project membership, repository pull/push permissions, username, and password secret. The workflow expects `HARBOR_PASSWORD`, not `HARBOR_TOKEN`, and consumes it through password stdin. Never put the password in a build arg or command line.

### Missing Harbor project

The required project is `circus`. It must contain the two image repositories and the two cache repositories, or Harbor must allow them to be created by the robot account according to local policy.

### Cache import failure

An empty cache on the first run is normal. A registry TLS/auth/project error is not. Confirm the image-specific cache reference and robot pull permission. Review the plain BuildKit log for `importing cache manifest`; never make backend and frontend share a cache repository.

### Cache export failure

Confirm robot push permission on the matching `circus/cache/*` repository and Harbor support for `oci-mediatypes=true,image-manifest=true`. The build step fails rather than silently publishing an un-cached image.

### Backend health timeout

Inspect the local container logs. Confirm the image has `CIRCUS_DATABASE_URL` set to a syntactically valid PostgreSQL connection string and `ASPNETCORE_HTTP_PORTS=8080`. Liveness itself should not connect to PostgreSQL; an invalid connection-string error indicates runtime configuration, not a database availability probe.

### Frontend static-server failure

Check that the Elm build completed and `/usr/share/nginx/html/index.html`, `app.js`, and `styles.css` exist in the image. Confirm nginx is listening on 8080 as UID 101 and that `/healthz` is served by nginx configuration rather than Elm.

### Attestation incompatibility

Trusted publication attempts `--provenance=mode=min --sbom=true`. If the deployed BuildKit/Harbor combination rejects OCI attestations, retain the exact plain build error and inspect Harbor's artifact UI/API before changing anything. Do not silently disable the flags. If attestations are mandatory under repository policy, stop and open a narrowly scoped follow-up ACT; this publication ACT is then PARTIAL.

## Repository-owned checks

Run the static policy suite with:

```bash
python3 scripts/verify_container_policy.py
```

It parses both workflows, checks trigger/runner/secrets/cache/tag/digest security boundaries, verifies numeric users and ports, checks action SHA pins, and checks that tracked secret-like files cannot enter the Docker context.
