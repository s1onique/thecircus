# Close Report — ACT-CIRCUS-CANONICAL-EVIDENCE-PROVIDER-FOUNDATION01-CORRECTION02

## Verdict

**PARTIAL_CHECKPOINT (corrected by CORRECTION03)**

CORRECTION02 successfully introduced explicit provider dependencies and made
the CanonicalEvidence CLI suite hermetic. Its original `CLOSED_PASS` claim was
too broad: the installed Leamas reader still consumed the incompatible native
artifact, the ordinary repository gate did not require provider verification,
the tracked schema contained a provider-name typo, and its targeted digest did
not cover later correction commits. CORRECTION03 preserves the successful
implementation work while correcting the closure verdict.

## Baseline and bounded implementation identities

```yaml
baseline_commit_oid: 7fb558ba273e9f9ca2d1b39c6bd6dd7a771ca490
baseline_tree_oid: 8095e50d319619a4ec2af1f5ff100295d6c93121
implementation_commit_oid: 272a9223025e14b93b120568ac9cc56f4b896061
implementation_tree_oid: f682decb9564300c1ce329a7defdadea57d26342
tested_commit_oid: 48e671894dc8f11f675f533f22c6e07cb1b7954f
tested_tree_oid: ce4b909b20f8a0afe0df8dbadc7cd4efeab791db
evidence_artifact_sha256: bfb186817f0848a9846fbb04f2a4d805a661704619f0d50c98ae29542b63e767
expected_closure_tag_name: act-canonical-evidence-foundation-correction02-v1
verdict: PARTIAL_CHECKPOINT
```

The evidence hash binds the historical tracked
`factory/evidence/digest-correction02.json`. The report intentionally does not
claim its own commit/tree/blob, a future tag object, or any remote identity.

## What CORRECTION02 actually passed

- `CanonicalEvidenceDependencies` made identity, working-tree, check,
  artifact-read/write, and clock dependencies explicit.
- Production dependency construction continued to compose the bounded process
  and bounded Git authorities.
- Hermetic CLI tests exercised the production parser and orchestration without
  mutating Git executable setters.
- Concurrent and failure-isolation tests passed.
- CanonicalEvidence, BoundedProcess, GitAdapter, RepairEpisodes, and
  FSharpDiagnostics suites were reported green for the bounded tested commit.
- A generated compatibility projection was introduced and cryptographically
  bound to the native semantic hash.

## Findings that prevented closure

1. Leamas had no source-selection flag and continued reading
   `.factory/gate-summary.json`, while the generated projection lived at
   `.factory/gate-summary.json.leamas`.
2. The actual digest therefore reported `source_status=invalid`, zero checks,
   and empty tested identities.
3. `gate` omitted `verify-canonical-evidence` from its prerequisites.
4. `.gitattributes` weakened ordinary whitespace policy for digest files.
5. The schema said `circuit-canonical-evidence` while registry, artifact, and
   compiled provider said `circus-canonical-evidence`.
6. The digest used a moving `HEAD` endpoint and predated later correction
   commits, so it was not immutable full-range evidence.
7. The seam report claimed observation of a private executable cell that the
   tests did not actually observe.

## Corrected seam criterion

The truthful criterion, adopted by CORRECTION03, is:

> CanonicalEvidence tests do not invoke the Git executable mutators.

A static production-source inventory rejects `setGitExecutable` and
`resetGitExecutable`; isolated concurrent CLI tests prove dependency-state
isolation. No mutable setter visibility is broadened.

## Non-recursive publication model

This committed report records only prior implementation/test identities, the
SHA-256 of a prior evidence artifact, and an expected tag name. Final branch,
remote, and tag identities are valid only in a detached post-publication
transcript produced after publication.
