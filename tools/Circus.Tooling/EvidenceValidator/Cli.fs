module Circus.Tooling.EvidenceValidator.Cli

// Explicit S/E committed-evidence validator CLI.

open System
open System.IO

open Circus.Tooling.EvidenceValidator.Domain
open Circus.Tooling.EvidenceValidator.Validation

module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

type Command =
    | ValidateCmd of repoRoot: string * path: string * subjectCommit: string * evidenceCommit: string
    | HashCmd of path: string
    | HelpCmd

let helpText () =
    "evidence-validate -- exact committed evidence authority\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling evidence-validate validate \\\n"
    + "    --repo-root <path> --path <repo-relative-evidence-path> \\\n"
    + "    --subject-commit <full-oid> --evidence-commit <full-oid>\n"
    + "  circus-tooling evidence-validate hash --path <ACT-evidence-json>\n"

let private consume flag args =
    match args with
    | value :: rest -> Ok(value, rest)
    | [] -> Error(sprintf "missing value for %s" flag)

let private parseFlags tail accepted =
    let values = Collections.Generic.Dictionary<string, string>(StringComparer.Ordinal)
    let mutable rest = tail
    let mutable failure = None

    while failure.IsNone && not (List.isEmpty rest) do
        match rest with
        | flag :: following when Set.contains flag accepted ->
            if values.ContainsKey flag then
                failure <- Some("duplicate argument: " + flag)
            else
                match consume flag following with
                | Ok(value, remaining) ->
                    values.Add(flag, value)
                    rest <- remaining
                | Error detail -> failure <- Some detail
        | unknown :: _ -> failure <- Some("unrecognised argument: " + unknown)
        | [] -> ()

    match failure with
    | Some detail -> Error detail
    | None -> Ok values

let private requireFlag (values: Collections.Generic.Dictionary<string, string>) flag =
    match values.TryGetValue flag with
    | true, value -> Ok value
    | false, _ -> Error("missing required argument: " + flag)

let private parse argv =
    match argv with
    | []
    | [ "help" ]
    | [ "-h" ]
    | [ "--help" ] -> Ok HelpCmd
    | "hash" :: tail ->
        match parseFlags tail (Set.ofList [ "--path" ]) with
        | Error detail -> Error detail
        | Ok values -> requireFlag values "--path" |> Result.map HashCmd
    | "validate" :: tail ->
        match
            parseFlags
                tail
                (Set.ofList
                    [ "--repo-root"
                      "--path"
                      "--subject-commit"
                      "--evidence-commit" ])
        with
        | Error detail -> Error detail
        | Ok values ->
            match
                requireFlag values "--repo-root",
                requireFlag values "--path",
                requireFlag values "--subject-commit",
                requireFlag values "--evidence-commit"
            with
            | Ok repoRoot, Ok path, Ok subject, Ok evidence ->
                Ok(ValidateCmd(repoRoot, path, subject, evidence))
            | results ->
                results
                |> fun (a, b, c, d) -> [ a; b; c; d ]
                |> List.choose (function | Error detail -> Some detail | Ok _ -> None)
                |> String.concat "; "
                |> Error
    | _ -> Error "usage: evidence-validate {validate|hash|help}"

let private boolToken value =
    if value then "true" else "false"

let private optionalBoolToken value =
    match value with
    | None -> "not_applicable"
    | Some actual -> boolToken actual

let private renderProof outcome =
    let proof = outcome.Proof

    sprintf
        "evidence_commit_exists=%s evidence_path_exists=%s working_bytes_equal_evidence_blob=%s subject_commit_exists=%s subject_tree_matches=%s subject_is_ancestor_of_evidence=%s subject_differs_from_evidence=%s payload_hash_matches=%s transcript_summary_matches=%s transcript_and_scan_match=%s"
        (boolToken proof.EvidenceCommitExists)
        (boolToken proof.EvidencePathExists)
        (boolToken proof.WorkingBytesEqualEvidenceBlob)
        (boolToken proof.SubjectCommitExists)
        (boolToken proof.SubjectTreeMatches)
        (boolToken proof.SubjectIsAncestorOfEvidence)
        (boolToken proof.SubjectDiffersFromEvidence)
        (boolToken proof.PayloadHashMatches)
        (optionalBoolToken proof.TranscriptSummaryMatches)
        (optionalBoolToken proof.TranscriptAndScanMatch)

let runValidate repoRoot path subject evidence =
    let outcome = validate repoRoot path subject evidence

    match outcome.OperationalFailure with
    | Some failure ->
        stderr.WriteLine(
            sprintf
                "evidence-validate: FAIL operational operation=%s detail=%s path=%s %s"
                failure.Operation
                failure.Detail
                path
                (renderProof outcome)
        )

        for issue in outcome.Issues do
            stderr.WriteLine("  " + Domain.issueToString issue)

        ExitCode.operationalError
    | None when not (List.isEmpty outcome.Issues) ->
        stderr.WriteLine(sprintf "evidence-validate: FAIL path=%s %s" path (renderProof outcome))

        for issue in outcome.Issues do
            stderr.WriteLine("  " + Domain.issueToString issue)

        ExitCode.policyFailure
    | None ->
        stdout.WriteLine(
            sprintf
                "evidence-validate: PASS path=%s subject=%s evidence=%s evidence_blob=%s %s"
                path
                subject
                evidence
                (defaultArg outcome.EvidenceBlobOid "<none>")
                (renderProof outcome)
        )

        ExitCode.pass

let runHash path =
    if not (File.Exists path) then
        stderr.WriteLine("evidence-validate hash: FAIL (file not found: " + path + ")")
        ExitCode.operationalError
    else
        try
            match computeActPayloadHash (File.ReadAllText path) with
            | Ok hash ->
                stdout.WriteLine hash
                ExitCode.pass
            | Error issue ->
                stderr.WriteLine("evidence-validate hash: FAIL (" + Domain.issueToString issue + ")")
                ExitCode.policyFailure
        with error ->
            stderr.WriteLine("evidence-validate hash: FAIL (" + error.Message + ")")
            ExitCode.operationalError

let run argv =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText ())
        ExitCode.pass
    | Ok(HashCmd path) -> runHash path
    | Ok(ValidateCmd(repoRoot, path, subject, evidence)) ->
        runValidate repoRoot path subject evidence
    | Error detail ->
        stderr.WriteLine("error: " + detail)
        stderr.WriteLine(helpText ())
        ExitCode.operationalError
