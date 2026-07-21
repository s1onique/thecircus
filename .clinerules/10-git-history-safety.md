# Git History Safety — Agent Instructions

**Applies to:** All agents, scripts, and automated systems operating in `s1onique/thecircus`

---

## Core Rule

**Never force-push. Never delete remote refs.**

This is not a guideline. It is an unconditional policy violation if disobeyed.

---

## What This Means

### Forbidden Operations

- `git push --force` or `-f`
- `git push --force-with-lease`
- `git push --force-if-includes`
- `git push <remote> +<refspec>`
- `git push --delete` or `-d`
- `git push <remote> :<ref>`
- `git push --mirror`
- `git push --prune`
- `git push --no-verify`
- `git send-pack ...`
- Any dynamic construction: `git push "$@"` or `flag=--force; git push $flag`
- GitHub API ref updates with `force=true`
- `gh api` forced ref mutation
- HTTP DELETE against Git ref endpoints

### Permitted Operations

- `git push origin main` (ordinary fast-forward)
- `git push --atomic origin main`
- `git push --follow-tags origin main`
- `git fetch origin`
- `git rebase origin/main`
- Local history rewriting (rebase, amend, reset) is fine **as long as you don't force-push**

---

## Conflict Recovery

If `git push` fails with non-fast-forward:

1. **Stop**
2. Fetch: `git fetch origin`
3. Inspect: `git log --oneline origin/main..HEAD`
4. Choose: rebase onto origin/main, or merge, or discuss
5. Verify: run `make gate`
6. Push: `git push origin main`

**Do not** use force to skip this process.

---

## Why This Matters

Force-pushing to `main` rewrites history for everyone. It breaks CI, causes lost commits, and creates confusion. The policy exists because it is the only way to protect shared history.

---

## Verification

Before any push, the pre-push hook will check:

- New branches: allowed
- Fast-forward updates to existing branches: allowed
- Non-fast-forward updates: **rejected**
- Branch deletion: **rejected**
- Tag creation: allowed
- Tag replacement or deletion: **rejected**

The hook runs automatically. It cannot be bypassed locally without removing it from `.githooks/`.

---

## If You Need Help

If you need to recover from a situation where force-push seems necessary:

1. **Do not force-push**
2. Ask for assistance
3. Explain what you're trying to achieve
4. A non-destructive solution exists

---

## The Invariant

```
Every update to an existing remote branch must satisfy:
  old_remote_oid is an ancestor of new_remote_oid
```

This invariant is enforced automatically. Respect it.
