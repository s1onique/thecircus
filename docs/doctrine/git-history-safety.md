# Git History Safety Doctrine

**Authority:** Circus Local Policy  
**Scope:** `s1onique/thecircus` repository  
**Effective:** Upon merge to `main`  
**Supersedes:** None  
**Superseded by:** `ACT-LEAMAS-DISTRIBUTED-NO-FORCE-PUSH-POLICY01`

---

## 1. Absolute Prohibitions

No human, agent, script, Make target, workflow, hook, or generated command may:

- Force-update a remote branch or tag.
- Delete a remote branch or tag.
- Bypass the repository pre-push guard.
- Disable the authoritative GitHub protection.
- Use an equivalent low-level or API mechanism to rewrite remote history.

The ban includes "safer" force variants. This doctrine does not permit them merely because they include a lease or ancestry check.

### 1.1 Prohibited Command Forms

The following patterns are unconditionally prohibited in any governed publication surface:

| Pattern | Description |
|---------|-------------|
| `git push --force` | Long-form force option |
| `git push -f` | Short-form force option |
| `git push -uf` | Combined short-form with upstream |
| `git push --force-with-lease` | Force with lease (any value) |
| `git push --force-if-includes` | Force if includes |
| `git push <remote> +<refspec>` | Leading-plus refspec |
| `git push --delete <ref>` | Remote deletion (long) |
| `git push -d <ref>` | Remote deletion (short) |
| `git push <remote> :<ref>` | Empty-source deletion |
| `git push --mirror` | Mirror publication |
| `git push --prune` | Prune publication |
| `git push --no-verify` | Bypass hooks |
| `git send-pack ...` | Low-level protocol invocation |
| `git push "$@"` | Dynamic all-arguments push |
| `git push "$flag"` | Dynamic flag push |
| `eval "git push $args"` | Eval indirection |
| `gh api ... --field force=true` | GitHub API force |
| `curl ... DELETE /repos/.../git/refs/...` | HTTP ref deletion |

This list is not exhaustive. Detection must cover all syntactically equivalent forms.

---

## 2. Permitted Local Operations

This doctrine does **not** prohibit local rewriting of unpublished work.

These remain permitted when they do not rewrite an already-published remote ref:

- Local interactive rebase.
- Local commit amendment.
- Local reset.
- Local branch deletion.
- Recovery by fetching and rebasing local commits onto the current remote tip.
- Ordinary fast-forward publication afterward.

### 2.1 Controlling Invariant

Every update to an existing remote branch must satisfy:

```
old_remote_oid is an ancestor of new_remote_oid
```

This invariant is enforced by the semantic pre-push verifier.

---

## 3. Conflict Recovery Protocol

When ordinary publication is rejected as non-fast-forward:

1. **Stop** — do not proceed with force.
2. **Fetch** the remote state.
3. **Inspect** the divergence.
4. **Preserve** both histories.
5. **Reconcile** locally through merge or rebase.
6. **Run** the canonical gate.
7. **Publish** through an ordinary fast-forward push.

Force publication is **never** the recovery mechanism.

---

## 4. Authority Layers

This doctrine establishes four layered defenses:

| Layer | Component | Authority Level |
|-------|-----------|-----------------|
| 1 | GitHub Ruleset | **Authoritative** — active remote enforcement |
| 2 | F# Static Verifier | Repository gate — blocks prohibited command forms |
| 3 | F# Pre-Push Verifier | Developer defense-in-depth — semantic ancestry check |
| 4 | `.clinerules/10-git-history-safety.md` | Agent instructions |

**Critical:** Local hooks are **not** authoritative because they can be absent or bypassed. Remote rules remain the ultimate enforcement boundary.

---

## 5. Scope and Limitations

### 5.1 Governed Surfaces

Governed surfaces include executables, workflows, Make targets, containers, and agent-executables that perform Git publication operations. See `factory/no-force-push-surfaces.csv` for the exact inventory.

### 5.2 Excluded Content

The following are **not** governed publication surfaces and must not be scanned as commands merely because they discuss prohibited syntax:

- Historical ACT documents.
- Close reports.
- Fixture files.
- Doctrine documents.
- Test expectations describing prohibited forms for validation purposes.

### 5.3 Fail-Closed Policy

The verifier fails closed when:

- An inventory row is malformed.
- An inventory path is missing.
- An inventory path is duplicated.
- An executable publication surface is discovered but unclassified.
- A configured parser cannot parse a governed file.
- Git inventory execution fails.
- Decoded file content is invalid.
- A supposedly governed file is a symlink escaping the repository.
- Dynamic or unclassifiable Git push arguments are detected.

There is **no violation baseline**. The required production violation count is zero.

---

## 6. Command Parsing Requirements

Detection must handle:

- Arguments before or after the remote.
- Short-option bundles containing `f` or `d`.
- Shell line continuations (`\`).
- YAML literal and folded `run` blocks.
- Adjacent shell quoting such as `--for"ce"`.
- Tabs and repeated whitespace.
- Make recipe prefixes.
- `env` or `command` preceding `git`.
- Absolute or resolved Git executable paths where syntactically recognizable.
- Split command invocations via pipes or semicolons that construct push arguments dynamically.

---

## 7. GitHub Ruleset Requirements

The authoritative GitHub branch protection must evidence:

- Active enforcement.
- A rule applying to `main`.
- Non-fast-forward updates blocked.
- Ref deletion blocked.
- No bypass actor applicable to normal project automation.
- Exact repository identity (`s1onique/thecircus`).
- Exact checked branch (`main`).
- Deterministic normalized evidence output.

---

## 8. Temporal Authority

This local doctrine is **temporary authority** until superseded by:

`ACT-LEAMAS-DISTRIBUTED-NO-FORCE-PUSH-POLICY01`

The future Leamas policy must **replace, not silently duplicate**, this implementation.

---

## 9. Verification Requirements

Publication requires:

```bash
git fetch origin
# Verify: behind = 0, origin/main is ancestor of HEAD
# Verify: make publication-gate exits 0
# Verify: working tree clean
```

Publication must use an ordinary command equivalent to:

```bash
git push origin main
```

Post-publication verification:

```bash
# Verify: ahead = 0, behind = 0
# Verify: HEAD = origin/main
# Verify: force_update = false
# Verify: remote_deletion = false
```

---

**End of Doctrine**
