module Circus.Tooling.EvidenceValidator.Domain

// Exact committed-evidence authority domain.
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03

[<Literal>]
let Sha256Placeholder =
    "0000000000000000000000000000000000000000000000000000000000000000"

type EvidencePayloadKind =
    | ActEvidencePayload
    | CanonicalEvidencePayload

type CountSummary = {
    Tests: int
    Passed: int
    Failed: int
    Errored: int
    ExitCode: int
}

type SmokeBinding = {
    TranscriptPath: string
    TranscriptBlobOid: string
    TranscriptSha256: string
    ScanPath: string
    ScanBlobOid: string
    ScanSha256: string
    DeclaredSummary: CountSummary
}

type EvidenceSnapshot = {
    Kind: EvidencePayloadKind
    SubjectCommitOid: string
    SubjectTreeOid: string
    EvidenceGeneratedAfterSubject: bool option
    PayloadHash: string
    Placeholder: string option
    Smoke: SmokeBinding option
}

type BindingProof = {
    EvidenceCommitExists: bool
    EvidencePathExists: bool
    WorkingBytesEqualEvidenceBlob: bool
    SubjectCommitExists: bool
    SubjectTreeMatches: bool
    SubjectIsAncestorOfEvidence: bool
    SubjectDiffersFromEvidence: bool
    PayloadHashMatches: bool
    TranscriptSummaryMatches: bool option
    TranscriptAndScanMatch: bool option
}

let emptyProof =
    { EvidenceCommitExists = false
      EvidencePathExists = false
      WorkingBytesEqualEvidenceBlob = false
      SubjectCommitExists = false
      SubjectTreeMatches = false
      SubjectIsAncestorOfEvidence = false
      SubjectDiffersFromEvidence = false
      PayloadHashMatches = false
      TranscriptSummaryMatches = None
      TranscriptAndScanMatch = None }

type Issue =
    | MalformedJson of detail: string
    | DuplicateJsonProperty of context: string * property: string
    | MissingField of field: string
    | WrongFieldType of field: string * expected: string
    | InvalidOid of field: string * value: string
    | InvalidSha256 of field: string * value: string
    | MandatoryBooleanFalse of field: string
    | SubjectArgumentMismatch of payload: string * argument: string
    | SubjectTreeMismatch of payload: string * resolved: string
    | SubjectEqualsEvidenceCommit of oid: string
    | SubjectNotAncestor of subject: string * evidence: string
    | WorkingBytesMismatch of path: string
    | PayloadHashMismatch of expected: string * actual: string
    | CommittedBlobMismatch of path: string * expected: string * actual: string
    | ReferencedHashMismatch of path: string * expected: string * actual: string
    | TranscriptSummaryMalformed of detail: string
    | TranscriptSummaryMismatch of declared: CountSummary * actual: CountSummary
    | TranscriptScanMismatch of transcript: CountSummary * scan: CountSummary
    | CanonicalPayloadInvalid of issues: string list

type OperationalFailure = {
    Operation: string
    Detail: string
}

type ValidationOutcome = {
    Path: string
    SubjectCommitOid: string
    EvidenceCommitOid: string
    EvidenceBlobOid: string option
    Proof: BindingProof
    Snapshot: EvidenceSnapshot option
    Issues: Issue list
    OperationalFailure: OperationalFailure option
}

let issueToString issue =
    match issue with
    | MalformedJson detail -> "malformed JSON: " + detail
    | DuplicateJsonProperty (context, property) -> sprintf "%s contains duplicate property %s" context property
    | MissingField field -> "missing required field: " + field
    | WrongFieldType (field, expected) -> sprintf "%s must be %s" field expected
    | InvalidOid (field, value) -> sprintf "%s is not a full ASCII hexadecimal Git OID: %s" field value
    | InvalidSha256 (field, value) -> sprintf "%s is not a 64-character ASCII hexadecimal SHA-256: %s" field value
    | MandatoryBooleanFalse field -> sprintf "%s must be true" field
    | SubjectArgumentMismatch (payload, argument) -> sprintf "payload subject %s disagrees with --subject-commit %s" payload argument
    | SubjectTreeMismatch (payload, resolved) -> sprintf "payload subject tree %s disagrees with resolved tree %s" payload resolved
    | SubjectEqualsEvidenceCommit oid -> sprintf "subject commit equals evidence commit: %s" oid
    | SubjectNotAncestor (subject, evidence) -> sprintf "subject %s is not an ancestor of evidence %s" subject evidence
    | WorkingBytesMismatch path -> sprintf "working bytes differ from committed evidence blob: %s" path
    | PayloadHashMismatch (expected, actual) -> sprintf "payload hash mismatch: declared=%s computed=%s" expected actual
    | CommittedBlobMismatch (path, expected, actual) -> sprintf "committed blob mismatch for %s: expected=%s actual=%s" path expected actual
    | ReferencedHashMismatch (path, expected, actual) -> sprintf "SHA-256 mismatch for %s: expected=%s actual=%s" path expected actual
    | TranscriptSummaryMalformed detail -> "smoke transcript summary malformed: " + detail
    | TranscriptSummaryMismatch (declared, actual) -> sprintf "smoke payload/transcript summary mismatch: declared=%A actual=%A" declared actual
    | TranscriptScanMismatch (transcript, scan) -> sprintf "smoke transcript/scan mismatch: transcript=%A scan=%A" transcript scan
    | CanonicalPayloadInvalid issues -> "canonical payload invalid: " + String.concat "; " issues

let isPass outcome =
    outcome.OperationalFailure.IsNone && List.isEmpty outcome.Issues
