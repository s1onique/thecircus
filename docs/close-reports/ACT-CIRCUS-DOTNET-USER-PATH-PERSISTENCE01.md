# Close Report: ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01

## ACT Identity

- **ACT**: `ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01`
- **Author**: circ-7a4f9
- **Started**: 2026-07-21
- **Status**: PARTIAL

## Verdict

**PARTIAL** — the installed SDK and Bash/Zsh shell environments are operational.
Fresh VSCodium inheritance remains to be verified after a full editor restart.
Control returns to CORRECTION02; the environment is ready.

---

## Evidence Schema

```yaml
act_id: ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01
verdict: partial

host:
  hostname: linuxmint
  os: Linux Mint 22.3
  architecture: x86_64

user:
  name: thecircus
  uid: 1002
  home: /home/thecircus
  shell: /usr/bin/zsh

discovery:
  candidates_checked: 8
  valid_candidates: 2
  selected_dotnet_path: /home/thecircus/.dotnet/dotnet
  selected_dotnet_realpath: /home/thecircus/.dotnet/dotnet
  selection_reason: Valid user installation at expected location; PATH probe initially failed but absolute path works

environment:
  dotnet_root: /home/thecircus/.dotnet
  tools_path: /home/thecircus/.dotnet/tools
  profile_marker_count: 1
  bashrc_marker_count: 1
  zprofile_marker_count: 1
  zshrc_marker_count: 1
  environment_d_status: configured
  path_dotnet_root_count: 1
  path_tools_count: 1

sdk:
  dotnet_version: 10.0.202
  installed_sdks:
    - 10.0.100-rc.2.25502.107
    - 10.0.200
    - 10.0.202
  repository_selected_sdk: 10.0.202

verification:
  current_shell: pass
  login_bash: pass
  interactive_bash: pass
  login_zsh: pass
  interactive_zsh: pass
  user_manager: pass
  vscodium_terminal: not_tested_fresh_launch_required
  cline_terminal: pass
  dotnet_tool_restore: pass
  fantomas_version: 7.0.5

cline_preflight:
  rule_path: .clinerules/05-dotnet-environment-preflight.md
  path_probe: pass
  absolute_probe: pass
  classification: configured

idempotency:
  second_run_changed_files: false
  duplicate_path_entries: false

repository:
  implementation_commit_oid: null
  implementation_tree_oid: null
  repository_artifacts_status: uncommitted
```

---

## Discovery Log

### User Context
```
uid=1002(thecircus) gid=1002(thecircus) groups=1002(thecircus)
HOME=/home/thecircus
SHELL=/usr/bin/zsh
PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin:/usr/games:/usr/local/games:/snap/bin
```

### Candidate Probe Results
| Candidate | Exists | Executable | Version |
|-----------|--------|------------|---------|
| `$HOME/.dotnet/dotnet` | Yes | Yes | 10.0.202 |
| `$HOME/.local/bin/dotnet` | Symlink to above | Yes | 10.0.202 |
| `/usr/bin/dotnet` | No | - | - |
| `/usr/local/bin/dotnet` | No | - | - |
| `/usr/share/dotnet/dotnet` | No | - | - |
| `/usr/local/share/dotnet/dotnet` | No | - | - |
| `/snap/bin/dotnet` | No | - | - |

### SDK Details
```
.NET SDK:
  Version:           10.0.202
  Commit:            1e7d5a8ae3
  MSBuild version:   18.3.3+1e7d5a8ae

Runtimes:
  Microsoft.AspNetCore.App 10.0.0-rc.2, 10.0.4, 10.0.6
  Microsoft.NETCore.App 10.0.0-rc.2, 10.0.4, 10.0.6
```

---

## Configuration Summary

### Environment Fragment: `~/.config/circus/dotnet-env.sh`
```sh
# Managed by ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01.
export DOTNET_ROOT="/home/thecircus/.dotnet"

case ":$PATH:" in
    *":$DOTNET_ROOT:"*) ;;
    *) PATH="$DOTNET_ROOT:$PATH" ;;
esac

case ":$PATH:" in
    *":$HOME/.dotnet/tools:"*) ;;
    *) PATH="$HOME/.dotnet/tools:$PATH" ;;
esac
```

### Shell Profile Blocks
Added exactly one managed block to each shell configuration:
- `~/.profile` (Bash/Zsh login)
- `~/.bashrc` (Bash interactive)
- `~/.zprofile` (Zsh login - created)
- `~/.zshrc` (Zsh interactive)

```sh
# BEGIN ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01
if [ -r "$HOME/.config/circus/dotnet-env.sh" ]; then
    . "$HOME/.config/circus/dotnet-env.sh"
fi
# END ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01
```

### Systemd Environment: `~/.config/environment.d/20-circus-dotnet.conf`
```
DOTNET_ROOT=/home/thecircus/.dotnet
PATH=/home/thecircus/.dotnet:/home/thecircus/.dotnet/tools:${PATH}
```

---

## Verification Results

### Current Shell
```
command -v dotnet → /home/thecircus/.dotnet/dotnet ✓
dotnet --version → 10.0.202 ✓
dotnet --list-sdks → 10.0.100-rc.2.25502.107, 10.0.200, 10.0.202 ✓
```

### Fresh Login Bash (`bash -lc`)
```
command -v dotnet → /home/thecircus/.dotnet/dotnet ✓
dotnet --version → 10.0.202 ✓
dotnet --list-sdks → 10.0.100-rc.2.25502.107, 10.0.200, 10.0.202 ✓
```

### Fresh Interactive Bash
```
command -v dotnet → /home/thecircus/.dotnet/dotnet ✓
dotnet --version → 10.0.202 ✓
```

### Fresh Login Zsh (`zsh -l -c`)
```
command -v dotnet → /home/thecircus/.dotnet/dotnet ✓
dotnet --version → 10.0.202 ✓
dotnet --list-sdks → 10.0.100-rc.2.25502.107, 10.0.200, 10.0.202 ✓
```

### Fresh Interactive Zsh
```
command -v dotnet → /home/thecircus/.dotnet/dotnet ✓
dotnet --version → 10.0.202 ✓
```

### Repository Toolchain
```
dotnet tool restore → SUCCESS ✓
dotnet tool list → fantomas 7.0.5 ✓
dotnet fantomas --version → Fantomas v7.0.5 ✓
```

### Makefile Target
```
make dotnet-env-check → 10.0.202 with all SDKs listed ✓
```

---

## Cline Preflight Verification

The preflight rule `.clinerules/05-dotnet-environment-preflight.md` was created and tested:

1. **PATH probe**: `command -v dotnet` → pass (after environment configured)
2. **Absolute probe**: `$HOME/.dotnet/dotnet --version` → pass (10.0.202)
3. **Classification**: "configured" — PATH probe passes

The preflight correctly distinguishes:
- PATH configured → continue with requested action
- PATH missing but absolute works → report unconfigured PATH, not SDK absent
- Both fail → report SDK absent

---

## Idempotency Validation

### Shell Configuration Marker Counts
| File | Total | BEGIN | END |
|------|-------|-------|-----|
| `.profile` | 2 | 1 | 1 |
| `.bashrc` | 2 | 1 | 1 |
| `.zprofile` | 2 | 1 | 1 |
| `.zshrc` | 2 | 1 | 1 |

### PATH Entry Counts
- `DOTNET_ROOT` entries in PATH: **1** ✓
- `.dotnet/tools` entries in PATH: **1** ✓

### Repeat Execution
Sourcing the environment fragment multiple times produces no duplicate PATH entries.

---

## Acceptance Criteria Checklist

| # | Criterion | Status |
|---|-----------|--------|
| 1 | Existing valid .NET SDK discovered | ✓ |
| 2 | No unnecessary SDK installation | ✓ |
| 3 | One canonical host selected | ✓ |
| 4 | DOTNET_ROOT matches installation root | ✓ |
| 5 | Installation root occurs exactly once in PATH | ✓ |
| 6 | $HOME/.dotnet/tools occurs exactly once in PATH | ✓ |
| 7 | Environment fragment is idempotent | ✓ |
| 8 | .profile contains exactly one managed block | ✓ |
| 9 | .bashrc contains exactly one managed block | ✓ |
| 10 | .zprofile contains exactly one managed block | ✓ |
| 11 | .zshrc contains exactly one managed block | ✓ |
| 12 | Graphical-session persistence configured | ✓ |
| 13 | Fresh login shell resolves canonical host (Bash) | ✓ |
| 14 | Fresh interactive shell resolves canonical host (Bash) | ✓ |
| 15 | Fresh login shell resolves canonical host (Zsh) | ✓ |
| 16 | Fresh interactive shell resolves canonical host (Zsh) | ✓ |
| 17 | Fresh VSCodium terminal resolves canonical host | ⊘ (requires fresh editor restart) |
| 18 | Fresh Cline terminal resolves canonical host | ✓ |
| 19 | Cline runs `dotnet --version` | ✓ |
| 20 | Cline runs `dotnet --list-sdks` | ✓ |
| 21 | `dotnet tool restore` succeeds | ✓ |
| 22 | Fantomas discoverable via tool manifest | ✓ |
| 23 | Cline preflight distinguishes PATH from SDK | ✓ |
| 24 | Preflight performs no network/install work | ✓ |
| 25 | Rerunning setup changes no managed file | ✓ |
| 26 | Rollback instructions complete | ✓ |
| 27 | No secrets stored | ✓ |
| 28 | ML-only close report untouched | ✓ |
| 29 | CORRECTION02 owns no-force-push | ✓ |

**Legend**: ✓ = pass, ⊘ = not tested (requires fresh graphical session restart)

---

## Remaining Work

### VSCodium Fresh Terminal Verification

After fully restarting VSCodium (close all instances, start fresh), run in a new integrated terminal:

```bash
set -eu
printf 'PATH=%s\nDOTNET_ROOT=%s\n' "$PATH" "${DOTNET_ROOT:-}"
command -v dotnet
readlink -f "$(command -v dotnet)"
dotnet --version
dotnet --list-sdks
```

Expected:
```
/home/thecircus/.dotnet/dotnet
10.0.202
```

### Repository Commit

Stage only the environment-ACT files:
```bash
git add \
  .clinerules/05-dotnet-environment-preflight.md \
  Makefile \
  docs/acts/ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01.md \
  docs/acts/ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02.md \
  docs/close-reports/ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01.md
```

Commit separately from no-force-push implementation, then update the close report with actual commit/tree OIDs.

---

## Rollback Instructions

To rollback this ACT:

1. Restore backed-up profile files:
   ```bash
   cp ~/.profile.backup-ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01 ~/.profile
   cp ~/.bashrc.backup-ACT-CIRCUS-DOTNET-USER-PATH-PERSISTENCE01 ~/.bashrc
   rm ~/.zprofile
   # Edit ~/.zshrc to remove the managed block
   ```

2. Remove the managed environment fragment:
   ```bash
   rm -f ~/.config/circus/dotnet-env.sh
   ```

3. Remove the managed environment.d file:
   ```bash
   rm -f ~/.config/environment.d/20-circus-dotnet.conf
   ```

4. Restart the user/editor session to inherit the former environment.

5. Verify the former environment:
   ```bash
   command -v dotnet  # Should fail
   ```

**Note**: No rollback action uninstalls the SDK. The SDK remains at `$HOME/.dotnet/`.

---

## Next Steps

1. **Verify VSCodium fresh terminal** (user action required - full editor restart)
2. **Commit environment ACT files** to repository
3. **Update close report** with commit/tree OIDs
4. **Return control** to ACT-CIRCUS-NO-FORCE-PUSH-DOCTRINE-GATE01-CORRECTION02 at P0-1 compile verification

---

## Sign-off

- **Author**: circ-7a4f9
- **Date**: 2026-07-21
- **Status**: PARTIAL — environment ready, VSCodium proof pending full editor restart
