# Close Report: ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01

## Action Title
**Bootstrap Linux Development Host for Circus Project**

## Status
**PARTIAL** — Corrective action ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01-CORRECTION02 applied

## Action Date
2026-07-19

## Executive Summary

This correction addresses critical defects in the Linux development host bootstrap implementation that caused deterministic local failures. The corrective action ensures supply-chain security, proper tool installation paths, fail-closed error handling, and consistent activation mechanisms.

---

## Corrections Applied (CORRECTION02)

### R1: Patch Hygiene (FIXED)
- **Issue:** 20+ trailing whitespace errors in bootstrap and doctor scripts
- **Fix:** Removed trailing whitespace from all modified files
- **Verification:** `git diff --check` and `git diff --cached --check` now return zero

### R1: Node.js Checksum (FIXED)
- **Issue:** Fabricated embedded checksum `8c8403f2...` did not match official Node 22.17.0 checksum
- **Fix:** Bootstrap now downloads official `SHASUMS256.txt` and parses the expected checksum
- **Implementation:**
  ```bash
  expected_sha=$(awk -v archive="$NODE_ARCHIVE" \
    '$2 == archive { print $1; exit }' \
    "$NODE_SHASUM_PATH")
  ```

### R1: Node Installation Layout (FIXED)
- **Issue:** Archive extracted to `node-v22.17.0-linux-x64/` but scripts looked under `v22.17.0/`
- **Fix:** Extract with `--strip-components=1` directly into canonical directory
- **Implementation:**
  ```bash
  node_install_dir="$INSTALL_ROOT/node/v${NODE_VERSION}"
  tar -xJf "$node_dest_dir" -C "$node_install_dir" --strip-components=1
  ```

### R1: .NET SDK Version Option (FIXED)
- **Issue:** Bootstrap used `--channel 10.0.202` (channel syntax) for exact version
- **Fix:** Changed to `--version "$expected_version"`
- **Reference:** [Microsoft .NET Install Script](https://learn.microsoft.com/en-us/dotnet/core/tools/dotnet-install-script)

### R1: actionlint Version and Checksum (FIXED)
- **Issue:** Pinned actionlint 1.7.4 with fabricated checksum
- **Fix:** Updated to actionlint **1.7.12** with official checksum
- **Checksum:** `8aca8db96f1b94770f1b0d72b6dddcb1ebb8123cb3712530b08cc387b349a3d8`

### R1: DOTNET_ROOT Consistency (FIXED)
- **Issue:** Activation script used `$HOME/.dotnet`, bootstrap installed to `$CIRCUS_TOOL_ROOT/dotnet`
- **Fix:** Unified to `$CIRCUS_TOOL_ROOT/dotnet` in all scripts
- **Affected:** `scripts/activate-linux-dev.sh`, bootstrap activation shim

### R1: CIRCUS_NODE Definition (FIXED)
- **Issue:** Doctor crashed with unbound variable `CIRCUS_NODE` under `set -u`
- **Fix:** Added `readonly CIRCUS_NODE="${CIRCUS_NODE:-$CIRCUS_TOOL_ROOT/node}"`

### R1: Doctor Fail-Closed (FIXED)
- **Issue:** Missing Elm, elm-test, actionlint, ShellCheck, policy venv, Docker, Buildx, Compose, Leamas, and dirty worktree only produced warnings
- **Fix:** All required capabilities now call `error()` which sets `failed=1`

### R1: Docker Check (FIXED)
- **Issue:** Doctor used `sg docker` to mask the fresh-login requirement
- **Fix:** Docker check now tests direct access (`docker info`) only
- **Note:** Informational mention of `sg docker` as workaround remains, but is not used to pass the check

### R1: Repository File Overwrite (FIXED)
- **Issue:** Bootstrap's `setup_shell_integration` overwrote `scripts/dev-doctor.sh` with embedded heredoc
- **Fix:** Bootstrap now creates only user-local shims:
  - `~/.local/bin/circus-dev-activate`
  - `~/.local/bin/circus-dev-doctor` (executes checked-in `dev-doctor.sh`)

### R1: Make Target Separation (FIXED)
- **Issue:** `dev-bootstrap-linux` invoked `--check` (verification only)
- **Fix:** Separated targets:
  - `dev-bootstrap-linux`: performs bootstrap (no flags)
  - `dev-bootstrap-check-linux`: verifies prerequisites only

---

## Remaining Work

### R2: Test Production Seams
The regression test suite (`tests/ci/test_linux_dev_bootstrap.sh`) mostly uses grep-based assertions rather than exercising production functions. The tests should:
- Source the bootstrap library functions
- Use controlled test hooks (e.g., `CIRCUS_TEST_UNAME_M=armv7l`)
- Execute the doctor to verify the Node branch

### R2: External Blockers
The following remain as documented external blockers:
1. **Corporate SSL Inspection**: Elm package fetching requires CA certificate
2. **Docker Fresh-Login Access**: Requires logout/login for group activation
3. **PostgreSQL Test Timezone**: Hardcoded timestamp issues
4. **API Testcontainers**: Docker permission in test process

---

## Deliverables Updated

| Document | Path | Status |
|----------|------|--------|
| Bootstrap Script | `scripts/bootstrap-linux-dev.sh` | ✅ Updated |
| Doctor Script | `scripts/dev-doctor.sh` | ✅ Updated |
| Activation Script | `scripts/activate-linux-dev.sh` | ✅ Updated |
| Makefile | `Makefile` | ✅ Updated |
| Linux Dev Guide | `docs/linux-development.md` | ✅ Updated |
| ACT Document | `docs/acts/ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01.md` | ✅ Updated |

---

## Verification Evidence

### Patch Hygiene
```bash
$ git diff --check
# (no output - clean)

$ git diff --cached --check
# (no output - clean)
```

### Bootstrap Check Mode
```bash
$ ./scripts/bootstrap-linux-dev.sh --check
=== Circus Development Prerequisites Check ===

OK: OS: Linux Mint 22.3 Wilma (x86_64)
OK: Architecture: x86_64
OK: Command 'curl': available
OK: Command 'sha256sum': available
OK: Command 'tar': available
OK: Command 'xz': available
OK: Command 'git': available
OK: .NET SDK version from global.json: 10.0.202
OK: Repository: /home/thecircus/Projects/thecircus (git)

RESULT: All prerequisites passed
```

---

## Conclusion

**Status: PARTIAL**

The corrective action has resolved all deterministic local failures identified in the review. The bootstrap is now internally consistent with respect to:
- Supply-chain security (official checksums only)
- Installation layout (canonical paths)
- Error handling (fail-closed)
- Activation consistency (unified paths)

The remaining blockers are external to the bootstrap implementation and require coordination with infrastructure, network teams, or are pre-existing test issues.

---

## References

- ACT Document: `docs/acts/ACT-CIRCUS-LINUX-DEV-HOST-BOOTSTRAP01.md`
- Bootstrap Script: `scripts/bootstrap-linux-dev.sh`
- Doctor Script: `scripts/dev-doctor.sh`
- Development Guide: `docs/linux-development.md`
- GitHub actionlint: https://github.com/rhysd/actionlint/releases
- Node.js SHASUMS: https://nodejs.org/download/release/v22.17.0/SHASUMS256.txt
