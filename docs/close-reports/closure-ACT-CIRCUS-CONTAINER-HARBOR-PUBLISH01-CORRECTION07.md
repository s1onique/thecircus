# Closure report — ACT-CIRCUS-CONTAINER-HARBOR-PUBLISH01-CORRECTION07

**Subject:** Canonical Leamas v1 gate-summary vocabulary, detached-evidence
binding, and targeted-digest integration assertion.

**Reviewer verdict addressed:** PARTIAL — CORRECTION07 required.

**Closure commit:** `c46cca6` on
`act/circus-container-harbor-publish01-correction07`.

---

## 1. What CORRECTION07 closes

### R1 — canonical gate-summary status vocabulary

The committed artefact emitted non-canonical values that the targeted
digest consumer rejected:

```text
overall_status: "green"
check status:   "passed"
```

The reviewer proved the mismatch with the digest output:

```text
overall_status=green
checks_passed=0
checks_unavailable=3
```

CORRECTION07 swaps the producer to the canonical Leamas v1 vocabulary:

```python
VALID_OVERALL_STATUSES = {"pass", "fail", "unavailable"}
VALID_CHECK_STATUSES   = {"pass", "fail", "skip", "unavailable"}
```

The generator (`factory/regenerate_gate_summary.py`) refuses to write
non-canonical values via a `main()`-level sanity check, so future edits
cannot silently regress the contract.

### R1 — self-referential `tested_tree_oid` binding

A tracked `.factory/gate-summary.json` cannot contain the hash of the
tree that contains itself; the reviewer proved the implementation
defect (collapsed exit code, unreachable `git write-tree` path) and the
deeper structural problem.

CORRECTION07 adopts the **preferred detached-evidence** model:

1. `.factory/gate-summary.json` is in `.gitignore` and removed from
   the git index.
2. The regenerate script records `HEAD^{tree}` unconditionally and
   warns (without failing) when staged changes are present.
3. The closure commit is committed first; the artefact is
   regenerated against the committed tree afterwards.

The new
`tests/ci/test_gate_summary_acceptance.sh` asserts the `.gitignore`
exclusion so the detachment contract cannot silently regress.

### R2 — exit-code-preserving Git command handling

`_run_git` previously collapsed the exit code into an empty stdout
string, so `git diff --cached --quiet` always looked like the success
branch and the staged-tree path was unreachable. CORRECTION07 replaces
the helper with `run_git()` returning a `GitResult(returncode, stdout,
stderr)` dataclass. `has_staged_changes()` reads the exit code
directly, so the staged-detection logic cannot silently degrade.

---

## 2. Files changed

| File | Change |
| ---- | ------ |
| `.gitignore` | Exclude `.factory/gate-summary.json` (detached evidence). |
| `.factory/regenerate_gate_summary.py` | Canonical vocabulary, `GitResult`, exit-code preservation, detached-evidence binding. |
| `.github/workflows/harbor.yml` | New gate-summary acceptance step. |
| `scripts/verify_container_policy.py` | Drop gate-summary validation; require the new acceptance test; require the new test files. |
| `tests/ci/test_action_pin_mutation.sh` | Drop tracked-artefact re-stamp; copy the new test files into the sandbox. |
| `tests/ci/test_gate_summary_acceptance.sh` | **NEW**: regenerate, vocabulary check, tree-OID check, `leamas factory digest` integration assertion. |
| `.factory/gate-summary.json` | **Deleted** from the index (preferred detached-evidence model). |

`mode 100755 → 100644` for `scripts/verify_container_policy.py` is
expected: the file no longer ships with an executable bit. It is
invoked as `python3 scripts/verify_container_policy.py` everywhere,
so the bit is redundant. (The shell scripts under `tests/ci/` and
`scripts/ci/` retain their `100755` mode.)

---

## 3. Acceptance test transcript (clean checkout)

The closure commit was checked out into a clean temporary directory
and the full gate chain was run from a state that contained no
`.factory/gate-summary.json`:

```text
$ git init --quiet --initial-branch=clean-test /tmp/circus-clean
$ git -C /tmp/circus-clean fetch <repo> act/circus-container-harbor-publish01-correction07
$ git -C /tmp/circus-clean checkout --quiet FETCH_HEAD
$ git -C /tmp/circus-clean rev-parse HEAD^{tree}
6d0b6f32333d44289102d541b8d516c4330f9591
$ ls /tmp/circus-clean/.factory/gate-summary.json
ls: cannot access '...': No such file or directory   # detached evidence

$ python3 .factory/regenerate_gate_summary.py
gate summary written to .factory/gate-summary.json: pass (3/3 pass) tree=6d0b6f32333d

$ cat .factory/gate-summary.json
{
  "schema_version": 1,
  "generated_at": "2026-07-16T14:06:51.202388+00:00",
  "tool": "circus-regenerate-gate-summary",
  "overall_status": "pass",
  "checks_total": 3,
  "checks_passed": 3,
  "checks_failed": 0,
  "checks_unavailable": 0,
  "checks": [
    {
      "name": "container-publication-policy",
      "status": "pass",
      "exit_code": 0,
      "command": "/opt/homebrew/opt/python@3.14/bin/python3.14 scripts/verify_container_policy.py"
    },
    {
      "name": "executable-shell-tests",
      "status": "pass",
      "exit_code": 0,
      "command": "bash tests/ci/test_build_publish_shell.sh"
    },
    {
      "name": "action-pin-mutation-test",
      "status": "pass",
      "exit_code": 0,
      "command": "bash tests/ci/test_action_pin_mutation.sh"
    }
  ],
  "tested_tree_oid": "6d0b6f32333d44289102d541b8d516c4330f9591"
}

# tested_tree_oid == HEAD^{tree}
# 6d0b6f32333d44289102d541b8d516c4330f9591 == 6d0b6f32333d44289102d541b8d516c4330f9591
# BINDING HOLDS
```

The new acceptance test then runs `leamas factory digest` against the
regenerated artefact and parses the `GATE_SUMMARY` section:

```text
$ bash tests/ci/test_gate_summary_acceptance.sh
----- regenerating .factory/gate-summary.json -----
gate summary written to .factory/gate-summary.json: pass (3/3 pass) tree=6d0b6f32333d
----- validating canonical vocabulary -----
----- validating tree-OID binding -----
OK: tested_tree_oid 6d0b6f32333d... matches HEAD^{tree}
----- running leamas factory digest -----
digest: mode=auto output=/tmp/...digest.txt time=0.11s OK
OK: leamas digest GATE_SUMMARY.overall_status = pass
OK: leamas digest GATE_SUMMARY.checks_total = 3
OK: leamas digest GATE_SUMMARY.checks_passed = 3
OK: leamas digest GATE_SUMMARY.checks_failed = 0
OK: leamas digest GATE_SUMMARY.checks_unavailable = 0
OK: leamas digest GATE_SUMMARY check container-publication-policy = pass
OK: leamas digest GATE_SUMMARY check executable-shell-tests = pass
OK: leamas digest GATE_SUMMARY check action-pin-mutation-test = pass
gate-summary acceptance test passed
```

This is the reviewer-requested transcript:

```text
overall_status=pass
checks_passed=3
checks_unavailable=0
```

---

## 4. Policy script changes

`scripts/verify_container_policy.py` was previously the validator for
the gate-summary schema and tree-OID binding, but it is itself one of
the three gates the artefact records. That created a chicken-and-egg
where the regenerate script could not invoke the policy until after
it had generated the artefact, and the artefact could not be
generated until the policy had passed.

CORRECTION07 moves all gate-summary validation into the new
acceptance test, which runs **after** the regenerate script. The
policy script now:

* still requires `.factory/gate-summary.json` to be excluded by
  `.gitignore` (delegated to the acceptance test);
* still requires `tests/ci/test_gate_summary_acceptance.sh` to exist
  and to be executable;
* still requires the new test to reference the canonical vocabulary
  markers (`pass`, `fail`, `skip`, `unavailable`, `leamas factory
  digest`, `overall_status=pass`, `checks_passed`, `checks_unavailable`)
  so a future edit to the test cannot silently regress the
  consumer-side contract.

The gate-summary itself is no longer required at policy time.

---

## 5. Why this model is coherent

| Concern | Resolution |
| ------- | ---------- |
| `pass`/`fail`/`skip`/`unavailable` vocabulary | Generator and validator use the canonical sets; the generator refuses to write non-canonical values. |
| `overall_status ∈ {pass, fail, unavailable}` | Generator emits only `pass`/`fail`/`unavailable`. |
| Targeted digest recognises every check | `leamas factory digest` parses `pass` and reports each check as `pass` (transcript above). |
| Tracked-file / `HEAD^{tree}` self-reference | The artefact is **untracked**; the recorded OID is always `HEAD^{tree}`, which does not contain the artefact. |
| Exit-code preservation | `run_git()` returns a `GitResult`; `has_staged_changes()` reads the exit code directly. |
| Acceptance test exercises the real consumer | `tests/ci/test_gate_summary_acceptance.sh` runs `leamas factory digest` and asserts `overall_status=pass`, `checks_passed=3`, `checks_unavailable=0`. |
| Clean-checkout execution | The transcript above is taken from a fresh checkout of `c46cca6` in `/tmp/circus-clean`. |

---

## 6. Branch and push readiness

The branch is ready to push and the PR image-build jobs (`ubuntu-latest`)
are ready to run on top of this revision.

| Step | Status |
| ---- | ------ |
| `git rm --cached .factory/gate-summary.json` | done |
| Add `.factory/gate-summary.json` to `.gitignore` | done |
| Generator rewritten with canonical vocabulary | done |
| Generator rewritten with `GitResult` and exit-code preservation | done |
| Validator no longer required at policy time | done |
| New `test_gate_summary_acceptance.sh` wired into `harbor.yml` | done |
| Action-pin mutation test adapted to detached evidence | done |
| Clean-checkout execution | passes (transcript above) |
| Closure commit `c46cca6` | landed on `act/circus-container-harbor-publish01-correction07` |

The branch can be pushed and the PR image builds can start.
