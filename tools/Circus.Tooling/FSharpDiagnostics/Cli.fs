module Circus.Tooling.FSharpDiagnostics.Cli

open System
open System.IO
open Circus.Tooling.FSharpDiagnostics.AtomicPublish
open Circus.Tooling.FSharpDiagnostics.BinlogExtractor
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Inventory
open Circus.Tooling.FSharpDiagnostics.LegacyTextExtractor
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.Serialization
open Circus.Tooling.FSharpDiagnostics.Verifier

/// Exit codes for the fsharp-diagnostics subsystem.
module ExitCode =
    let pass = 0
    let policyFailure = 1
    let operationalError = 2

/// Subcommands.
type Command =
    | InventoryCmd of outputJson: bool
    | ExtractBinlogCmd of captureId: string
    | ExtractLegacyTextCmd of captureId: string
    | RegenerateCmd
    | VerifyCmd of outputJson: bool
    | HelpCmd

let helpText () : string =
    "fsharp-diagnostics — F# compiler diagnostic corpus foundation\n"
    + "\n"
    + "Usage:\n"
    + "  circus-tooling fsharp-diagnostics inventory [--json]\n"
    + "  circus-tooling fsharp-diagnostics extract-binlog <capture-id>\n"
    + "  circus-tooling fsharp-diagnostics extract-legacy-text <capture-id>\n"
    + "  circus-tooling fsharp-diagnostics regenerate\n"
    + "  circus-tooling fsharp-diagnostics verify [--json]\n"
    + "  circus-tooling fsharp-diagnostics help\n"

let private parseBoolFlag (flag: string) (args: string list) : Result<bool, string> =
    match args with
    | [] -> Ok false
    | [ "--json" ] -> Ok true
    | [ "--human" ] -> Ok false
    | _ -> Result.Error(sprintf "unrecognised argument after '%s'" flag)

/// Parse fsharp-diagnostics subcommand arguments.
let parse (argv: string list) : Result<Command, string> =
    match argv with
    | [] | [ "help" ] | [ "-h" ] | [ "--help" ] -> Ok HelpCmd
    | "inventory" :: rest ->
        match parseBoolFlag "inventory" rest with
        | Ok b -> Ok(InventoryCmd b)
        | Result.Error e -> Result.Error e
    | "extract-binlog" :: [ captureId ] -> Ok(ExtractBinlogCmd captureId)
    | "extract-binlog" :: _ ->
        Result.Error "extract-binlog requires a capture-id argument"
    | "extract-legacy-text" :: [ captureId ] -> Ok(ExtractLegacyTextCmd captureId)
    | "extract-legacy-text" :: _ ->
        Result.Error "extract-legacy-text requires a capture-id argument"
    | "regenerate" :: [] -> Ok RegenerateCmd
    | "verify" :: rest ->
        match parseBoolFlag "verify" rest with
        | Ok b -> Ok(VerifyCmd b)
        | Result.Error e -> Result.Error e
    | _ ->
        Result.Error
            "usage: circus-tooling fsharp-diagnostics {inventory|extract-binlog|extract-legacy-text|regenerate|verify|help}"

/// Render inventory JSON.
let private renderInventoryJson (repoRoot: string) : string =
    let canon = enumerateCanonical repoRoot
    let scratch = enumerateFactoryScratch repoRoot
    let canonicalEntries =
        canon
        |> List.map (fun d ->
            let safePath = d.RelativePath.Replace('\\', '/')
            sprintf "{\"canonical_path\":\"%s\",\"byte_length\":%d,\"sha256\":\"%s\"}"
                safePath d.ByteLength d.Sha256)
        |> String.concat ","
    let scratchEntries =
        scratch
        |> List.map (fun d ->
            let safePath = d.RelativePath.Replace('\\', '/')
            sprintf "{\"path\":\"%s\",\"byte_length\":%d,\"sha256\":\"%s\"}"
                safePath d.ByteLength d.Sha256)
        |> String.concat ","
    let captures = discoverCaptures repoRoot
    sprintf
        "{\"schema_version\":\"fsharp-diagnostics-inventory-v1\",\"canonical_root\":\"%s\",\"canonical_artefacts\":[%s],\"non_authoritative_scratch\":[%s],\"captures\":[%s]}"
        CanonicalCorpusRoot
        canonicalEntries
        scratchEntries
        (captures |> List.map (sprintf "\"%s\"") |> String.concat ",")

/// Render inventory human output.
let private renderInventoryHuman (repoRoot: string) : string =
    let canon = enumerateCanonical repoRoot
    let scratch = enumerateFactoryScratch repoRoot
    let captures = discoverCaptures repoRoot
    let sb = System.Text.StringBuilder()
    let append (line: string) = sb.AppendLine line |> ignore
    append "fsharp-diagnostics inventory"
    append(sprintf "  canonical_root: %s" CanonicalCorpusRoot)
    append(sprintf "  canonical_artefacts: %d" (List.length canon))
    append(sprintf "  non_authoritative_scratch: %d" (List.length scratch))
    append(sprintf "  captures: %d" (List.length captures))
    append "  artefact_class_breakdown:"
    let byClass =
        canon
        |> List.groupBy (fun d -> artifactClassToken (classifyCanonicalPath d.RelativePath))
        |> List.sortBy fst
    for cls, ds in byClass do
        append(sprintf "    %s: %d" cls (List.length ds))
    sb.ToString()

let runInventory (repoRoot: string) (outputJson: bool) : int =
    let text =
        if outputJson then renderInventoryJson repoRoot
        else renderInventoryHuman repoRoot
    stdout.WriteLine text
    ExitCode.pass

let runExtractBinlog (repoRoot: string) (captureId: string) : int =
    let captureDir = rawSubdir + "/" + captureId
    let manifestPath = repoRelative repoRoot (captureDir + "/capture.json")
    if not (File.Exists manifestPath) then
        stderr.WriteLine(sprintf "error: capture manifest not found: %s" manifestPath)
        ExitCode.operationalError
    else
        let manifest = readCaptureManifest manifestPath
        let mutable binlogPath : string option = None
        for raw in manifest.RawArtifacts do
            let name = filenameOf raw
            if binlogPath.IsNone
               && (extensionOf raw = ".binlog" || name = "build.binlog") then
                let p = repoRelative repoRoot (captureDir + "/" + raw)
                if File.Exists p then binlogPath <- Some p
        match binlogPath with
        | None ->
            stderr.WriteLine(sprintf "error: capture %s has no binlog artefact" captureId)
            ExitCode.operationalError
        | Some p ->
            try
                let _, occs =
                    extractBinlog
                        manifest.CaptureId
                        manifest.SourceRootAliases
                        ExtractorVersion
                        p
                let lines = occs |> List.map renderOccurrence |> String.concat "\n"
                let outPath =
                    repoRelative repoRoot (captureDir + "/binlog-occurrences.jsonl")
                writeLineOriented outPath lines
                stdout.WriteLine
                    (sprintf
                        "fsharp-diagnostics extract-binlog: wrote %d occurrences to %s"
                        (List.length occs)
                        outPath)
                ExitCode.pass
            with
            | BinlogExtractionFailure msg ->
                stderr.WriteLine(sprintf "error: binlog extraction failed: %s" msg)
                ExitCode.operationalError

let runExtractLegacyText (repoRoot: string) (captureId: string) : int =
    let captureDir = rawSubdir + "/" + captureId
    let manifestPath = repoRelative repoRoot (captureDir + "/capture.json")
    if not (File.Exists manifestPath) then
        stderr.WriteLine(sprintf "error: capture manifest not found: %s" manifestPath)
        ExitCode.operationalError
    else
        let manifest = readCaptureManifest manifestPath
        let mutable textPathOpt : string option = None
        let mutable textBody = ""
        for raw in manifest.RawArtifacts do
            let ext = extensionOf raw
            let fullPath = repoRelative repoRoot (captureDir + "/" + raw)
            if textPathOpt.IsNone && (ext = ".log" || ext = ".txt") && File.Exists fullPath then
                textPathOpt <- Some fullPath
                textBody <- File.ReadAllText fullPath
        let result =
            parseText manifest.CaptureId manifest.SourceRootAliases ExtractorVersion textBody
        if result.DiagnosticLookingUnparsedLines > 0 then
            stderr.WriteLine
                (sprintf
                    "error: %d diagnostic-looking unparsed line(s)"
                    result.DiagnosticLookingUnparsedLines)
            ExitCode.policyFailure
        elif not (List.isEmpty result.UndeclaredAbsolutePaths) then
            stderr.WriteLine
                (sprintf
                    "error: undeclared absolute paths: %s"
                    (String.concat ", " result.UndeclaredAbsolutePaths))
            ExitCode.policyFailure
        else
            let lines = result.Occurrences |> List.map renderOccurrence |> String.concat "\n"
            let outPath =
                repoRelative repoRoot (captureDir + "/legacy-occurrences.jsonl")
            writeLineOriented outPath lines
            stdout.WriteLine
                (sprintf
                    "fsharp-diagnostics extract-legacy-text: wrote %d occurrences to %s (input_lines=%d, continuation_lines=%d, ignored_non_diagnostic=%d)"
                    (List.length result.Occurrences)
                    outPath
                    result.InputLines
                    result.ContinuationLines
                    result.IgnoredNonDiagnosticLines)
            ExitCode.pass

let runRegenerate (repoRoot: string) : int =
    let summary, outcome, _legacy, binlogResults =
        runPipeline repoRoot None
    if not outcome.Success then
        stderr.WriteLine "error: atomic publication failed"
        ExitCode.operationalError
    else
        let binlogFailures =
            binlogResults
            |> List.choose (fun (_, r) ->
                match r with
                | Result.Error msg -> Some msg
                | Ok _ -> None)
        if not (List.isEmpty binlogFailures) then
            stderr.WriteLine
                (sprintf "error: %d binlog capture(s) failed extraction"
                    (List.length binlogFailures))
            stderr.WriteLine(String.concat "\n" binlogFailures)
            ExitCode.policyFailure
        else
            stdout.WriteLine
                (sprintf
                    "fsharp-diagnostics regenerate: occurrences=%d unique_fingerprints=%d duplicates=%d captures=%d"
                    summary.OccurrenceCount
                    summary.UniqueExactFingerprintCount
                    summary.DuplicateOccurrenceCount
                    summary.CapturesTotal)
            ExitCode.pass

let runVerify (repoRoot: string) (outputJson: bool) : int =
    let summary, outcome, legacyResults, binlogResults =
        runPipeline repoRoot None
    let binlogFailures =
        binlogResults
        |> List.choose (fun (_, r) ->
            match r with
            | Result.Error msg -> Some msg
            | Ok _ -> None)
    let undeclared =
        legacyResults
        |> List.collect (fun (_, p) -> p.UndeclaredAbsolutePaths)
    let unparsed =
        legacyResults
        |> List.sumBy (fun (_, p) -> p.DiagnosticLookingUnparsedLines)
    let fail =
        not outcome.Success
        || not (List.isEmpty binlogFailures)
        || not (List.isEmpty undeclared)
        || unparsed > 0
        || summary.UnclassifiedArtifacts > 0
    let resultText =
        if outputJson then
            sprintf
                "{\"verdict\":\"%s\",\"occurrence_count\":%d,\"unique_exact_fingerprint_count\":%d,\"duplicate_occurrence_count\":%d,\"captures_total\":%d,\"binlog_failures\":%d,\"undeclared_absolute_paths\":%d,\"diagnostic_looking_unparsed_lines\":%d,\"unclassified_artefacts\":%d,\"canonical_byte_identical\":%s}"
                (if fail then "fail" else "pass")
                summary.OccurrenceCount
                summary.UniqueExactFingerprintCount
                summary.DuplicateOccurrenceCount
                summary.CapturesTotal
                (List.length binlogFailures)
                (List.length undeclared)
                unparsed
                summary.UnclassifiedArtifacts
                (if outcome.CanonicalByteIdenticalAfterFailure then "true" else "false")
        else
            sprintf
                "verdict: %s\noccurrences: %d\nunique_fingerprints: %d\nduplicates: %d\ncaptures: %d\nbinlog_failures: %d\nundeclared_absolute_paths: %d\ndiagnostic_looking_unparsed_lines: %d\nunclassified_artefacts: %d\ncanonical_byte_identical_after_failure: %s"
                (if fail then "FAIL" else "PASS")
                summary.OccurrenceCount
                summary.UniqueExactFingerprintCount
                summary.DuplicateOccurrenceCount
                summary.CapturesTotal
                (List.length binlogFailures)
                (List.length undeclared)
                unparsed
                summary.UnclassifiedArtifacts
                (if outcome.CanonicalByteIdenticalAfterFailure then "true" else "false")
    stdout.WriteLine resultText
    if fail then ExitCode.policyFailure else ExitCode.pass

let run (argv: string list) : int =
    match parse argv with
    | Ok HelpCmd ->
        stdout.WriteLine(helpText())
        ExitCode.pass
    | Ok(InventoryCmd outputJson) ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runInventory root outputJson
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok(ExtractBinlogCmd captureId) ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runExtractBinlog root captureId
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok(ExtractLegacyTextCmd captureId) ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runExtractLegacyText root captureId
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok RegenerateCmd ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runRegenerate root
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Ok(VerifyCmd outputJson) ->
        match Circus.Tooling.SourcePolicy.Cli.resolveRepoRoot () with
        | Ok root -> runVerify root outputJson
        | Result.Error msg ->
            stderr.WriteLine(sprintf "error: %s" msg)
            ExitCode.operationalError
    | Result.Error msg ->
        stderr.WriteLine(sprintf "error: %s" msg)
        stderr.WriteLine(helpText())
        ExitCode.operationalError