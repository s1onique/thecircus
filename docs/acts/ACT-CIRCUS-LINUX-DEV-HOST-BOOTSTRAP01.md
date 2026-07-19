# ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01

## Action Title
**Bootstrap Linux Development Host for Circus Project**

## Status
PARTIAL — local bootstrap correctness and evidence defects remain;
the ACT is not yet externally blocked only.

## Created
2026-07-16

## Owner
thecircus-dev

---

## Objective

Establish a reproducible, documented procedure for a developer to configure a Linux host for full-stack Circus development (F#/ASP.NET Core backend + Elm frontend) with all required toolchain components.

---

## Scope

### In Scope
- Linux Mint 22.3 (or compatible Ubuntu/Debian derivative) x86_64
- F#/.NET 10 SDK
- Elm 0.19.2
- Node.js 22.x
- Python 3.12 with policy venv
- Docker and Buildx
- actionlint and ShellCheck
- Leamas CLI
- Circus source repository cloning
- Source compilation and test verification
- Production container build and smoke test
- Automation scripts (bootstrap, doctor, activation)

### Out of Scope
- macOS or Windows host setup
- Kubernetes deployment
- Production database configuration
- Network firewall configuration

---

## Acceptance Criteria

### AC1: Toolchain Installation
- [ ] .NET 10 SDK installed and functional
- [ ] Node.js 22.x installed and functional
- [ ] Elm 0.19.2 installed and functional
- [ ] Python 3.12 with policy venv created
- [ ] actionlint v1.7.x installed
- [ ] ShellCheck v0.11.0 installed
- [ ] Docker and Buildx functional without sudo
- [ ] Leamas CLI installed

### AC2: Source Compilation
- [ ] `dotnet restore --locked-mode` succeeds
- [ ] `dotnet build -c Release` succeeds with 0 warnings
- [ ] All unit test projects build

### AC3: Unit Test Verification
- [ ] Domain tests: 4/4 pass
- [ ] Contracts tests: 37/37 pass
- [ ] Application tests: 18/18 pass
- [ ] Persistence tests: all pass OR documented external blocker
- [ ] API tests: all pass OR documented external blocker
- [ ] Elm tests: 17/17 pass

### AC4: Container Build
- [ ] Backend Dockerfile builds successfully
- [ ] Backend container smoke test passes (GET /health/live -> 200)
- [ ] Frontend Dockerfile builds successfully
- [ ] Frontend container smoke test passes

### AC5: Automation Scripts
- [ ] `bootstrap-linux-dev.sh` runs to completion
- [ ] `dev-doctor.sh` correctly identifies missing components
- [ ] `activate-linux-dev.sh` correctly sets up environment
- [ ] All scripts pass ShellCheck analysis
- [ ] Scripts use fail-closed error handling

### AC6: Documentation
- [ ] Linux development guide created
- [ ] Bootstrap procedure documented
- [ ] Troubleshooting guide included

---

## Required Deliverables

### Scripts
- `scripts/bootstrap-linux-dev.sh` - Main bootstrap automation
- `scripts/activate-linux-dev.sh` - Environment activation helper
- `scripts/dev-doctor.sh` - Environment verification diagnostic

### Documentation
- `docs/linux-development.md` - Development setup guide

### Evidence
- Close report with test transcripts
- Gate summary evidence (detached from Git)

---

## Security Considerations

1. **Docker Group Privilege**: Users in the `docker` group have root-level privileges. Document this risk.
2. **Corporate CA Certificate**: Corporate SSL inspection requires special handling for external package fetching.
3. **PATH Integrity**: Scripts must correctly construct PATH to avoid unexpected behavior.

---

## Known External Blockers

1. **Corporate SSL Inspection**: Elm package fetching requires corporate CA certificate
2. **Docker Fresh-Login Access**: Requires `sg docker` or new login shell for Docker group access
3. **PostgreSQL Test Timezone**: Some tests have hardcoded timestamps

---

## References

- [Docker Linux Post-Installation](https://docs.docker.com/engine/install/linux-postinstall/)
- [.NET Installation Guide](https://learn.microsoft.com/en-us/dotnet/core/install/linux)
- [Elm Installation](https://guide.elm-lang.org/install/elm.html)

---

## Revision History

| Date | Version | Changes |
|------|---------|---------|
| 2026-07-16 | 1.0 | Initial draft |
| 2026-07-19 | 1.1 | Updated status and blockers |
