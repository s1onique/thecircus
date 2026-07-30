module Circus.Tooling.EvidenceValidator.Validation

// =============================================================================
// Exact committed-evidence validation
//
// Every Git operation uses the bounded raw-byte adapter.  The explicit subject
// S and evidence commit E are resolved as commits, E:path is resolved as a
// blob, working bytes are compared byte-for-byte with that blob, S's tree is
// checked against the payload, and S must be a strict ancestor of E.  Any Git
// operational failure is represented separately and can never yield PASS.
// =============================================================================

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Text
open System.Text.Json
open System.Text.RegularExpressions

open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.ScopeAuthority.Domain
open Circus.Tooling.EvidenceValidator.Domain
open Circus.Tooling.CanonicalEvidence.Serialization
open Circus.Tooling.CanonicalEvidence.Validation

type EvidenceDependencies =
    { RunGit: string -> string list -> Result<int * byte array * byte array, string>
      ReadWorkingBytes: string -> Result<byte array, string> }

let productionDependencies () =
    { RunGit =
        fun repoRoot arguments ->
            match runGitBytesTyped repoRoot defaultGitRunOptions arguments with
            | Error error -> Error(sprintf "%A" error)
            | Ok result -> Ok(result.ExitCode, result.Stdout, result.Stderr)
      ReadWorkingBytes =
        fun path ->
            try
                if File.Exists path then
                    Ok(File.ReadAllBytes path)
                else
                    Error("file not found: " + path)
            with error ->
                Error(sprintf "%s: %s" (error.GetType().Name) error.Message) }

exception private PayloadException of Issue
exception private OperationalException of string * string

let private payloadFail issue = raise (PayloadException issue)

let private operationalFail operation detail =
    raise (OperationalException(operation, detail))

let private isAsciiSha256 (value: string) =
    not (isNull value)
    && value.Length = 64
    && value
       |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F'))

let private equalOid left right =
    String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

let private strictUtf8 (label: string) (bytes: byte array) =
    try
        UTF8Encoding(false, true).GetString(bytes)
    with :? DecoderFallbackException as error ->
        operationalFail label ("invalid UTF-8: " + error.Message)

let rec private rejectDuplicates context (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        let seen = HashSet<string>(StringComparer.Ordinal)

        for property in element.EnumerateObject() do
            if not (seen.Add property.Name) then
                payloadFail (DuplicateJsonProperty(context, property.Name))

            rejectDuplicates (context + "." + property.Name) property.Value
    | JsonValueKind.Array ->
        let mutable index = 0

        for item in element.EnumerateArray() do
            rejectDuplicates (sprintf "%s[%d]" context index) item
            index <- index + 1
    | _ -> ()

let private tryProperty (element: JsonElement) (name: string) =
    let mutable value = Unchecked.defaultof<JsonElement>

    if element.TryGetProperty(name, &value) then
        Some value
    else
        None

let private requiredProperty (context: string) (name: string) (element: JsonElement) =
    match tryProperty element name with
    | Some value -> value
    | None -> payloadFail (MissingField(context + "." + name))

let private requiredString context name element =
    let value = requiredProperty context name element

    if value.ValueKind <> JsonValueKind.String then
        payloadFail (WrongFieldType(context + "." + name, "a string"))

    let text = value.GetString()

    if String.IsNullOrWhiteSpace text then
        payloadFail (WrongFieldType(context + "." + name, "a non-empty string"))

    text

let private requiredInt context name element =
    let value = requiredProperty context name element
    let mutable parsed = 0

    if value.ValueKind <> JsonValueKind.Number || not (value.TryGetInt32(&parsed)) then
        payloadFail (WrongFieldType(context + "." + name, "an integer"))

    parsed

let private requiredBool context name element =
    let value = requiredProperty context name element

    match value.ValueKind with
    | JsonValueKind.True -> true
    | JsonValueKind.False -> false
    | _ -> payloadFail (WrongFieldType(context + "." + name, "a Boolean"))

let private escapeJsonString (value: string) =
    let builder = StringBuilder()
    builder.Append('"') |> ignore

    for character in value do
        match character with
        | '\\' -> builder.Append("\\\\") |> ignore
        | '"' -> builder.Append("\\\"") |> ignore
        | '\n' -> builder.Append("\\n") |> ignore
        | '\r' -> builder.Append("\\r") |> ignore
        | '\t' -> builder.Append("\\t") |> ignore
        | value when int value < 0x20 ->
            builder.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int value)
            |> ignore
        | value -> builder.Append(value) |> ignore

    builder.Append('"') |> ignore
    builder.ToString()

let rec private renderValue (builder: StringBuilder) (element: JsonElement) =
    match element.ValueKind with
    | JsonValueKind.Object ->
        builder.Append('{') |> ignore

        element.EnumerateObject()
        |> Seq.sortBy (fun property -> property.Name)
        |> Seq.iteri (fun index property ->
            if index > 0 then
                builder.Append(',') |> ignore

            builder.Append(escapeJsonString property.Name).Append(':') |> ignore
            renderValue builder property.Value)

        builder.Append('}') |> ignore
    | JsonValueKind.Array ->
        builder.Append('[') |> ignore

        element.EnumerateArray()
        |> Seq.iteri (fun index item ->
            if index > 0 then
                builder.Append(',') |> ignore

            renderValue builder item)

        builder.Append(']') |> ignore
    | JsonValueKind.String -> builder.Append(escapeJsonString (element.GetString())) |> ignore
    | JsonValueKind.Number -> builder.Append(element.GetRawText()) |> ignore
    | JsonValueKind.True -> builder.Append("true") |> ignore
    | JsonValueKind.False -> builder.Append("false") |> ignore
    | JsonValueKind.Null -> builder.Append("null") |> ignore
    | _ -> payloadFail (MalformedJson "unsupported JSON token")

let private renderActHashInput (root: JsonElement) =
    let builder = StringBuilder()
    builder.Append('{') |> ignore

    root.EnumerateObject()
    |> Seq.sortBy (fun property -> property.Name)
    |> Seq.iteri (fun index property ->
        if index > 0 then
            builder.Append(',') |> ignore

        builder.Append(escapeJsonString property.Name).Append(':') |> ignore

        if property.Name = "evidence_payload_sha256" then
            builder.Append(escapeJsonString Sha256Placeholder) |> ignore
        else
            renderValue builder property.Value)

    builder.Append('}') |> ignore
    builder.ToString()

let computeActPayloadHash raw =
    try
        let mutable options = JsonDocumentOptions()
        options.AllowTrailingCommas <- false
        options.CommentHandling <- JsonCommentHandling.Disallow
        options.MaxDepth <- 64
        use document = JsonDocument.Parse((raw: string), options)

        if document.RootElement.ValueKind <> JsonValueKind.Object then
            Error(MalformedJson "root is not an object")
        else
            rejectDuplicates "evidence" document.RootElement
            Ok(sha256OfUtf8 (renderActHashInput document.RootElement))
    with
    | PayloadException issue -> Error issue
    | :? JsonException as error -> Error(MalformedJson error.Message)

let private parseSummary context element exitCode =
    { Tests = requiredInt context "tests" element
      Passed = requiredInt context "passed" element
      Failed = requiredInt context "failed" element
      Errored = requiredInt context "errored" element
      ExitCode = exitCode }

let private parseSmoke root =
    match tryProperty root "direct" with
    | None -> None
    | Some direct when direct.ValueKind <> JsonValueKind.Object ->
        payloadFail (WrongFieldType("evidence.direct", "an object"))
    | Some direct ->
        match tryProperty direct "hermetic" with
        | None -> None
        | Some hermetic when hermetic.ValueKind <> JsonValueKind.Object ->
            payloadFail (WrongFieldType("evidence.direct.hermetic", "an object"))
        | Some hermetic ->
            let context = "evidence.direct.hermetic"
            let exitCode = requiredInt context "exit_code" hermetic
            let summaryElement = requiredProperty context "expecto_summary" hermetic

            if summaryElement.ValueKind <> JsonValueKind.Object then
                payloadFail (WrongFieldType(context + ".expecto_summary", "an object"))

            Some
                { TranscriptPath = requiredString context "transcript_path" hermetic
                  TranscriptBlobOid = requiredString context "transcript_blob_oid" hermetic
                  TranscriptSha256 = requiredString context "output_sha256" hermetic
                  ScanPath = requiredString context "scan_path" hermetic
                  ScanBlobOid = requiredString context "scan_blob_oid" hermetic
                  ScanSha256 = requiredString context "scan_sha256" hermetic
                  DeclaredSummary = parseSummary (context + ".expecto_summary") summaryElement exitCode }

let private parseActEvidence (raw: string) (root: JsonElement) =
    let subject = requiredString "evidence" "tested_subject_commit_oid" root
    let tree = requiredString "evidence" "tested_subject_tree_oid" root
    let generatedAfter = requiredBool "evidence" "evidence_generated_after_subject" root
    let payloadHash = requiredString "evidence" "evidence_payload_sha256" root

    let placeholder =
        requiredString "evidence" "evidence_payload_sha256_input_placeholder" root

    let issues = ResizeArray<Issue>()

    if not (isAsciiHexOid subject) then
        issues.Add(InvalidOid("tested_subject_commit_oid", subject))

    if not (isAsciiHexOid tree) then
        issues.Add(InvalidOid("tested_subject_tree_oid", tree))

    if not (isAsciiSha256 payloadHash) then
        issues.Add(InvalidSha256("evidence_payload_sha256", payloadHash))

    if placeholder <> Sha256Placeholder then
        issues.Add(InvalidSha256("evidence_payload_sha256_input_placeholder", placeholder))

    if not generatedAfter then
        issues.Add(MandatoryBooleanFalse "evidence_generated_after_subject")

    let computed = sha256OfUtf8 (renderActHashInput root)

    if not (String.Equals(payloadHash, computed, StringComparison.OrdinalIgnoreCase)) then
        issues.Add(PayloadHashMismatch(payloadHash, computed))

    { Kind = ActEvidencePayload
      SubjectCommitOid = subject
      SubjectTreeOid = tree
      EvidenceGeneratedAfterSubject = Some generatedAfter
      PayloadHash = payloadHash
      Placeholder = Some placeholder
      Smoke = parseSmoke root },
    computed,
    List.ofSeq issues

let private parseCanonicalEvidence (raw: string) (root: JsonElement) =
    match parseWireJson raw with
    | Error detail -> payloadFail (MalformedJson detail)
    | Ok canonical ->
        let rawKeys = [ for property in root.EnumerateObject() -> property.Name ]
        let validation = validate rawKeys canonical

        let issues =
            if isValid validation then
                []
            else
                [ CanonicalPayloadInvalid(validation.Issues |> List.map issueToString) ]

        { Kind = CanonicalEvidencePayload
          SubjectCommitOid = canonical.TestedCommitOid
          SubjectTreeOid = canonical.TestedTreeOid
          EvidenceGeneratedAfterSubject = None
          PayloadHash = canonical.SemanticSha256
          Placeholder = None
          Smoke = None },
        canonical.SemanticSha256,
        issues

let parseEvidence (raw: string) =
    try
        let mutable options = JsonDocumentOptions()
        options.AllowTrailingCommas <- false
        options.CommentHandling <- JsonCommentHandling.Disallow
        options.MaxDepth <- 64
        use document = JsonDocument.Parse((raw: string), options)
        let root = document.RootElement

        if root.ValueKind <> JsonValueKind.Object then
            Error(MalformedJson "root is not an object")
        else
            rejectDuplicates "evidence" root

            if (tryProperty root "tested_subject_commit_oid").IsSome then
                Ok(parseActEvidence raw root)
            elif (tryProperty root "tested_commit_oid").IsSome then
                Ok(parseCanonicalEvidence raw root)
            else
                Error(MissingField "tested_subject_commit_oid or tested_commit_oid")
    with
    | PayloadException issue -> Error issue
    | :? JsonException as error -> Error(MalformedJson error.Message)

let private runGit (deps: EvidenceDependencies) (repoRoot: string) (operation: string) (arguments: string list) =
    match deps.RunGit repoRoot arguments with
    | Error detail -> operationalFail operation detail
    | Ok(exitCode, stdout, stderr) -> exitCode, stdout, stderr

let private requiredGit
    (deps: EvidenceDependencies)
    (repoRoot: string)
    (operation: string)
    (missingDescription: string)
    (arguments: string list)
    =
    let exitCode, stdout, stderr = runGit deps repoRoot operation arguments

    if exitCode <> 0 then
        operationalFail
            operation
            (sprintf "%s; exit=%d stderr=%s" missingDescription exitCode (Encoding.UTF8.GetString(stderr).Trim()))

    stdout

let private oidOutput (operation: string) (bytes: byte array) =
    let oid = strictUtf8 operation bytes |> fun value -> value.Trim()

    if not (isAsciiHexOid oid) then
        operationalFail operation ("Git returned malformed OID: " + oid)

    oid

let private resolveCommit deps repoRoot oid =
    requiredGit
        deps
        repoRoot
        "resolve-commit"
        ("commit does not exist: " + oid)
        [ "rev-parse"; "--verify"; "--end-of-options"; oid + "^{commit}" ]
    |> oidOutput "resolve-commit"

let private resolveTree deps repoRoot oid =
    requiredGit
        deps
        repoRoot
        "resolve-tree"
        ("tree does not exist for commit: " + oid)
        [ "rev-parse"; "--verify"; "--end-of-options"; oid + "^{tree}" ]
    |> oidOutput "resolve-tree"

let private resolvePath deps repoRoot commitOid path =
    requiredGit
        deps
        repoRoot
        ("resolve-path:" + path)
        (sprintf "path %s does not exist in %s" path commitOid)
        [ "rev-parse"; "--verify"; "--end-of-options"; commitOid + ":" + path ]
    |> oidOutput ("resolve-path:" + path)

let private catBlob deps repoRoot path blobOid =
    requiredGit deps repoRoot ("cat-blob:" + path) ("blob does not exist: " + blobOid) [ "cat-file"; "blob"; blobOid ]

let private stripAnsi text =
    Regex.Replace(text, "\\x1B\\[[0-?]*[ -/]*[@-~]", "")

let parseTranscriptSummary (bytes: byte array) =
    try
        let text = UTF8Encoding(false, true).GetString bytes |> stripAnsi

        let summary =
            Regex.Match(
                text,
                "EXPECTO!\\s*(\\d+)\\s+tests run.*?(\\d+)\\s+passed,\\s*(\\d+)\\s+ignored,\\s*(\\d+)\\s+failed,\\s*(\\d+)\\s+errored",
                RegexOptions.Singleline ||| RegexOptions.CultureInvariant
            )

        if not summary.Success then
            Error(TranscriptSummaryMalformed "Expecto aggregate line not found")
        else
            let exitMatch =
                Regex.Match(text, "process exit code:\\s*(-?\\d+)", RegexOptions.CultureInvariant)

            if not exitMatch.Success then
                Error(TranscriptSummaryMalformed "process exit code marker not found")
            elif Int32.Parse(summary.Groups.[3].Value, CultureInfo.InvariantCulture) <> 0 then
                Error(TranscriptSummaryMalformed "ignored tests are not allowed in smoke evidence")
            else
                let requiredNames =
                    [ "passing runner returns 0"
                      "failed runner returns 1"
                      "errored runner returns 2"
                      "arbitrary non-zero runner returns its exact value (37)"
                      "exactly one production runWith definition exists" ]

                match
                    requiredNames
                    |> List.tryFind (fun name -> not (text.Contains(name, StringComparison.Ordinal)))
                with
                | Some missing -> Error(TranscriptSummaryMalformed("missing smoke test name: " + missing))
                | None ->
                    Ok
                        { Tests = Int32.Parse(summary.Groups.[1].Value, CultureInfo.InvariantCulture)
                          Passed = Int32.Parse(summary.Groups.[2].Value, CultureInfo.InvariantCulture)
                          Failed = Int32.Parse(summary.Groups.[4].Value, CultureInfo.InvariantCulture)
                          Errored = Int32.Parse(summary.Groups.[5].Value, CultureInfo.InvariantCulture)
                          ExitCode = Int32.Parse(exitMatch.Groups.[1].Value, CultureInfo.InvariantCulture) }
    with error ->
        Error(TranscriptSummaryMalformed error.Message)

let private parseScanSummary (bytes: byte array) =
    try
        let raw = UTF8Encoding(false, true).GetString bytes
        use document = JsonDocument.Parse raw
        rejectDuplicates "smoke-scan" document.RootElement
        let root = document.RootElement

        Ok
            { Tests = requiredInt "smoke-scan" "tests" root
              Passed = requiredInt "smoke-scan" "passed" root
              Failed = requiredInt "smoke-scan" "failed" root
              Errored = requiredInt "smoke-scan" "errored" root
              ExitCode = requiredInt "smoke-scan" "exit_code" root }
    with
    | PayloadException issue -> Error issue
    | :? JsonException as error -> Error(MalformedJson("smoke scan: " + error.Message))

let private validateReferencedBlob
    (deps: EvidenceDependencies)
    (repoRoot: string)
    (evidenceCommit: string)
    (path: string)
    (expectedBlob: string)
    (expectedHash: string)
    (issues: ResizeArray<Issue>)
    =
    match validateRepositoryPath false path with
    | Error detail -> payloadFail (MalformedJson(sprintf "referenced path %s invalid: %s" path detail))
    | Ok() -> ()

    if not (isAsciiHexOid expectedBlob) then
        issues.Add(InvalidOid(path + ".blob_oid", expectedBlob))

    if not (isAsciiSha256 expectedHash) then
        issues.Add(InvalidSha256(path + ".sha256", expectedHash))

    let actualBlob = resolvePath deps repoRoot evidenceCommit path

    if not (equalOid actualBlob expectedBlob) then
        issues.Add(CommittedBlobMismatch(path, expectedBlob, actualBlob))

    let bytes = catBlob deps repoRoot path actualBlob
    let actualHash = sha256Hex bytes

    if not (String.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)) then
        issues.Add(ReferencedHashMismatch(path, expectedHash, actualHash))

    bytes

let validateWithDependencies deps repoRoot path subjectCommitOid evidenceCommitOid =
    let mutable proof = emptyProof
    let issues = ResizeArray<Issue>()
    let mutable snapshot = None
    let mutable evidenceBlob = None

    let finish operational =
        { Path = path
          SubjectCommitOid = subjectCommitOid
          EvidenceCommitOid = evidenceCommitOid
          EvidenceBlobOid = evidenceBlob
          Proof = proof
          Snapshot = snapshot
          Issues = List.ofSeq issues
          OperationalFailure = operational }

    match validateRepositoryPath false path with
    | Error detail ->
        issues.Add(MalformedJson("evidence path is invalid: " + detail))
        finish None
    | Ok() when not (isAsciiHexOid subjectCommitOid) ->
        issues.Add(InvalidOid("--subject-commit", subjectCommitOid))
        finish None
    | Ok() when not (isAsciiHexOid evidenceCommitOid) ->
        issues.Add(InvalidOid("--evidence-commit", evidenceCommitOid))
        finish None
    | Ok() ->
        try
            let evidenceCommit = resolveCommit deps repoRoot evidenceCommitOid

            if not (equalOid evidenceCommit evidenceCommitOid) then
                operationalFail
                    "resolve-evidence-commit"
                    (sprintf "expected=%s actual=%s" evidenceCommitOid evidenceCommit)

            proof <-
                { proof with
                    EvidenceCommitExists = true }

            let blobOid = resolvePath deps repoRoot evidenceCommit path
            evidenceBlob <- Some blobOid
            proof <- { proof with EvidencePathExists = true }
            let committedBytes = catBlob deps repoRoot path blobOid
            let absolutePath = Path.GetFullPath(Path.Combine(repoRoot, path))

            let workingBytes =
                match deps.ReadWorkingBytes absolutePath with
                | Ok bytes -> bytes
                | Error detail -> operationalFail "read-working-evidence" detail

            let bytesEqual = workingBytes = committedBytes

            proof <-
                { proof with
                    WorkingBytesEqualEvidenceBlob = bytesEqual }

            if not bytesEqual then
                issues.Add(WorkingBytesMismatch path)

            let raw = strictUtf8 path workingBytes

            let parsed, computedPayloadHash, parseIssues =
                match parseEvidence raw with
                | Ok value -> value
                | Error issue -> payloadFail issue

            snapshot <- Some parsed

            for issue in parseIssues do
                issues.Add issue

            let hashMatches =
                String.Equals(parsed.PayloadHash, computedPayloadHash, StringComparison.OrdinalIgnoreCase)
                && not (
                    parseIssues
                    |> List.exists (function
                        | PayloadHashMismatch _
                        | CanonicalPayloadInvalid _ -> true
                        | _ -> false)
                )

            proof <-
                { proof with
                    PayloadHashMatches = hashMatches }

            if not (equalOid parsed.SubjectCommitOid subjectCommitOid) then
                issues.Add(SubjectArgumentMismatch(parsed.SubjectCommitOid, subjectCommitOid))

            let subjectCommit = resolveCommit deps repoRoot subjectCommitOid

            if not (equalOid subjectCommit subjectCommitOid) then
                operationalFail
                    "resolve-subject-commit"
                    (sprintf "expected=%s actual=%s" subjectCommitOid subjectCommit)

            proof <-
                { proof with
                    SubjectCommitExists = true }

            let subjectTree = resolveTree deps repoRoot subjectCommit
            let treeMatches = equalOid parsed.SubjectTreeOid subjectTree

            proof <-
                { proof with
                    SubjectTreeMatches = treeMatches }

            if not treeMatches then
                issues.Add(SubjectTreeMismatch(parsed.SubjectTreeOid, subjectTree))

            let differs = not (equalOid subjectCommit evidenceCommit)

            proof <-
                { proof with
                    SubjectDiffersFromEvidence = differs }

            if not differs then
                issues.Add(SubjectEqualsEvidenceCommit subjectCommit)

            let ancestryExit, _, ancestryStderr =
                runGit deps repoRoot "subject-ancestry" [ "merge-base"; "--is-ancestor"; subjectCommit; evidenceCommit ]

            match ancestryExit with
            | 0 ->
                proof <-
                    { proof with
                        SubjectIsAncestorOfEvidence = true }
            | 1 -> issues.Add(SubjectNotAncestor(subjectCommit, evidenceCommit))
            | code ->
                operationalFail
                    "subject-ancestry"
                    (sprintf "exit=%d stderr=%s" code (Encoding.UTF8.GetString(ancestryStderr).Trim()))

            match parsed.Smoke with
            | None -> ()
            | Some smoke ->
                let transcript =
                    validateReferencedBlob
                        deps
                        repoRoot
                        evidenceCommit
                        smoke.TranscriptPath
                        smoke.TranscriptBlobOid
                        smoke.TranscriptSha256
                        issues

                let scan =
                    validateReferencedBlob
                        deps
                        repoRoot
                        evidenceCommit
                        smoke.ScanPath
                        smoke.ScanBlobOid
                        smoke.ScanSha256
                        issues

                let transcriptSummary =
                    match parseTranscriptSummary transcript with
                    | Ok value -> Some value
                    | Error issue ->
                        issues.Add issue
                        None

                let scanSummary =
                    match parseScanSummary scan with
                    | Ok value -> Some value
                    | Error issue ->
                        issues.Add issue
                        None

                match transcriptSummary with
                | Some actual when actual = smoke.DeclaredSummary ->
                    proof <-
                        { proof with
                            TranscriptSummaryMatches = Some true }
                | Some actual ->
                    proof <-
                        { proof with
                            TranscriptSummaryMatches = Some false }

                    issues.Add(TranscriptSummaryMismatch(smoke.DeclaredSummary, actual))
                | None ->
                    proof <-
                        { proof with
                            TranscriptSummaryMatches = Some false }

                match transcriptSummary, scanSummary with
                | Some transcriptValue, Some scanValue when transcriptValue = scanValue ->
                    proof <-
                        { proof with
                            TranscriptAndScanMatch = Some true }
                | Some transcriptValue, Some scanValue ->
                    proof <-
                        { proof with
                            TranscriptAndScanMatch = Some false }

                    issues.Add(TranscriptScanMismatch(transcriptValue, scanValue))
                | _ ->
                    proof <-
                        { proof with
                            TranscriptAndScanMatch = Some false }

            finish None
        with
        | PayloadException issue ->
            issues.Add issue
            finish None
        | OperationalException(operation, detail) ->
            finish (
                Some
                    { Operation = operation
                      Detail = detail }
            )

let validate repoRoot path subjectCommitOid evidenceCommitOid =
    validateWithDependencies (productionDependencies ()) repoRoot path subjectCommitOid evidenceCommitOid
