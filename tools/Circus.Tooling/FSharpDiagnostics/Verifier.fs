module Circus.Tooling.FSharpDiagnostics.Verifier

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

// =============================================================================
// Verifier
// =============================================================================

let ExtractorVersion = "fsharp-diagnostics-v1"

/// One discovered capture with its loaded manifest.
type LoadedCapture = {
    CaptureRelativeDir: string
    CaptureManifest: CaptureManifest
}

/// Load every capture manifest from the canonical corpus raw root.
let loadCaptures (repoRoot: string) : LoadedCapture list =
    discoverCaptures repoRoot
    |> List.map (fun rel ->
        let manifestPath = repoRelative repoRoot (rel + "/capture.json")
        { CaptureRelativeDir = rel
          CaptureManifest = readCaptureManifest manifestPath })

/// Run the legacy text parser for each capture whose capture_kind is
/// "legacy_text" or "mixed".  Returns per-capture diagnostic occurrences
/// plus the per-capture accounting totals.
let private extractLegacyFromCaptures (captures: LoadedCapture list)
                                      (repoRoot: string)
                                      : (string * LegacyParseResult) list =
    captures
    |> List.filter (fun c ->
        c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.LegacyText
        || c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.Mixed)
    |> List.map (fun c ->
        let captureId = c.CaptureManifest.CaptureId
        let aliases = c.CaptureManifest.SourceRootAliases
        let mutable textPathOpt : string option = None
        let mutable textBody = ""
        // Pick the first raw artefact that looks like a text log.
        for raw in c.CaptureManifest.RawArtifacts do
            let ext = extensionOf raw
            let fullPath = repoRelative repoRoot (c.CaptureRelativeDir + "/" + raw)
            if textPathOpt.IsNone && (ext = ".log" || ext = ".txt") && File.Exists fullPath then
                textPathOpt <- Some fullPath
                textBody <- File.ReadAllText fullPath
        let fullText = textPathOpt |> Option.defaultValue ""
        let parseResult = parseText captureId aliases ExtractorVersion fullText
        c.CaptureRelativeDir, parseResult)

/// Attempt binlog extraction for each capture whose capture_kind is
/// "binlog" or "mixed".  Returns a Result per capture so verification can
/// report which captures failed and continue inspecting the rest.
let private extractBinlogFromCaptures (captures: LoadedCapture list)
                                       (repoRoot: string)
                                       : (string * Result<DiagnosticOccurrence list, string>) list =
    captures
    |> List.filter (fun c ->
        c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.Binlog
        || c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.Mixed)
    |> List.map (fun c ->
        let captureId = c.CaptureManifest.CaptureId
        let aliases = c.CaptureManifest.SourceRootAliases
        let mutable binlogPath : string option = None
        for raw in c.CaptureManifest.RawArtifacts do
            let name = filenameOf raw
            if binlogPath.IsNone
               && (extensionOf raw = ".binlog" || name = "build.binlog") then
                let p = repoRelative repoRoot (c.CaptureRelativeDir + "/" + raw)
                if File.Exists p then binlogPath <- Some p
        match binlogPath with
        | None ->
            c.CaptureRelativeDir, Result.Ok []
        | Some p ->
            try
                let _, occs = extractBinlog captureId aliases ExtractorVersion p
                c.CaptureRelativeDir, Result.Ok occs
            with
            | BinlogExtractionFailure msg ->
                c.CaptureRelativeDir, Result.Error msg)

/// Aggregate raw occurrences from every capture.  Binlog occurrences come
/// first (sorted by capture_id, event_ordinal), followed by legacy-text
/// occurrences (sorted by capture_id, event_ordinal).  Within the merged
/// stream, event_ordinals are rewritten to be globally unique by
/// prepending the capture_id slot index.
let mergeOccurrences
    (binlogResults: (string * Result<DiagnosticOccurrence list, string>) list)
    (legacyResults: (string * LegacyParseResult) list)
    : DiagnosticOccurrence list =
    let binlogOccs =
        binlogResults
        |> List.choose (fun (_, r) ->
            match r with
            | Result.Ok occs -> Some occs
            | Result.Error _ -> None)
        |> List.concat
        |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal)
    let legacyOccs =
        legacyResults
        |> List.map (fun (_, p) -> p.Occurrences)
        |> List.concat
        |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal)
    // Concatenate in deterministic capture-id order.
    binlogOccs @ legacyOccs

/// Build the per-fingerprint duplicate occurrence list.
let duplicateOccurrences (occs: DiagnosticOccurrence list) : (string * DiagnosticOccurrence) list =
    let mutable counts = Map.empty<string, int>
    let mutable emitted = ResizeArray<string * DiagnosticOccurrence>()
    for o in occs do
        let fp = fingerprintFor o
        let prev = Map.tryFind fp counts |> Option.defaultValue 0
        if prev >= 1 then
            emitted.Add(fp, o)
        counts <- Map.add fp (prev + 1) counts
    emitted
    |> Seq.toList
    |> List.sortBy (fun (fp, o) -> fp, o.CaptureId, o.EventOrdinal)

/// Render all canonical outputs to text bodies.
let private renderNormalized
    (occurrences: DiagnosticOccurrence list)
    (fingerprints: ExactFingerprint list)
    (duplicates: (string * DiagnosticOccurrence) list)
    (artifactEntries: ArtifactManifestEntry list)
    (migrationMap: (string * string * string * int64) list)
    (captures: LoadedCapture list)
    (captureManifestStatus: Map<string, LegacyParseResult>)
    (corpusSummary: CorpusSummary)
    : PendingFile list =
    let sortedOccs =
        occurrences
        |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal)
    let sortedFps =
        fingerprints
        |> List.sortBy (fun fp -> fp.Sha256)
    let occLines =
        sortedOccs
        |> List.map renderOccurrence
        |> String.concat "\n"
    let fpLines =
        let header = fingerprintsHeader
        header + "\n"
        + (sortedFps
           |> List.map renderFingerprintTsv
           |> String.concat "\n")
    let dupLines =
        let header = duplicatesHeader
        let rows =
            duplicates
            |> List.map (fun (fp, o) ->
                let spanText =
                    let s = o.Span
                    sprintf "%s:%s-%s:%s"
                        (match s.StartLine with | Some n -> string n | None -> "")
                        (match s.StartColumn with | Some n -> string n | None -> "")
                        (match s.EndLine with | Some n -> string n | None -> "")
                        (match s.EndColumn with | Some n -> string n | None -> "")
                renderDuplicateRow fp o.CaptureId o.EventOrdinal o.MessageRaw o.SourcePath o.ProjectPath spanText)
        header + "\n" + (rows |> String.concat "\n")
    let artifactLines =
        artifactEntries
        |> List.map renderArtifactManifestEntry
        |> String.concat "\n"
    let migrationLines =
        let header = migrationMapHeader
        let rows =
            migrationMap
            |> List.map (fun (orig, canon, sha, len) ->
                renderMigrationRow orig canon sha len)
        header + "\n" + (rows |> String.concat "\n")
    let summaryJson = renderCorpusSummary corpusSummary
    [
      { CanonicalFileName = occurrencesFile; Body = occLines }
      { CanonicalFileName = fingerprintsFile; Body = fpLines }
      { CanonicalFileName = duplicatesFile; Body = dupLines }
      { CanonicalFileName = artifactsManifestFile; Body = artifactLines }
      { CanonicalFileName = migrationMapFile; Body = migrationLines }
      { CanonicalFileName = summaryFile; Body = summaryJson }
    ]

/// Produce the artifact manifest entries (every artefact under the
/// canonical corpus root).  The canonical_path is the relative path under
/// the repo root; the original_path is the same value (no migration
/// occurred in this ACT).
let buildArtifactManifestEntries (repoRoot: string)
                                 (captures: LoadedCapture list)
                                 : ArtifactManifestEntry list * (string * string * string * int64) list =
    let discovered = enumerateCanonical repoRoot
    let captureIds = captures |> List.map (fun c -> c.CaptureManifest.CaptureId)
    let entries =
        discovered
        |> List.map (fun d ->
            let rel = d.RelativePath
            let cls = classifyCanonicalPath rel
            let auth = authorityFor rel
            let status = "present"
            let mt = mediaTypeFor rel
            let captureId =
                if d.RelativePath.Contains "/corpus/raw/" then
                    let parts = d.RelativePath.Split([| '/' |])
                    // /factory/evidence/fsharp-diagnostics/corpus/raw/<capture-id>/<file>
                    if parts.Length >= 5 then
                        Some(parts.[4])
                    else
                        None
                else
                    None
            let gaps = []
            { SchemaVersion = ArtifactManifestSchemaVersion
              CanonicalPath = rel
              OriginalPath = rel
              ArtifactClass = artifactClassToken cls
              Authority = authorityToken auth
              Status = status
              MediaType = mt
              ByteLength = d.ByteLength
              Sha256 = d.Sha256
              CaptureId = captureId
              Supersedes = None
              SupersededBy = None
              MetadataGaps = gaps })
    let migration = []  // no migration in this ACT
    entries, migration

/// Construct the corpus summary from the loaded captures and occurrence
/// list.
let buildCorpusSummary
    (captures: LoadedCapture list)
    (occurrences: DiagnosticOccurrence list)
    (fingerprints: ExactFingerprint list)
    (legacy: (string * LegacyParseResult) list)
    (artifactEntries: ArtifactManifestEntry list)
    : CorpusSummary =
    let legacyUnparsed =
        legacy
        |> List.sumBy (fun (_, p) -> p.DiagnosticLookingUnparsedLines)
    let capturesTotal = List.length captures
    let binlogCaptures =
        captures
        |> List.filter (fun c -> c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.Binlog)
        |> List.length
    let legacyCaptures =
        captures
        |> List.filter (fun c -> c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.LegacyText)
        |> List.length
    let mixedCaptures =
        captures
        |> List.filter (fun c -> c.CaptureManifest.CaptureKind = captureKindToken CaptureKind.Mixed)
        |> List.length
    let rawCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "raw")
        |> List.length
    let normalizedCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "normalized")
        |> List.length
    let derivedCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "derived")
        |> List.length
    let correctionCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "correction")
        |> List.length
    let sourceSnapshotCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "source_snapshot")
        |> List.length
    let obsoleteCount =
        artifactEntries
        |> List.filter (fun e -> e.ArtifactClass = "obsolete_retained")
        |> List.length
    let unclassifiedCount =
        artifactEntries
        |> List.filter (fun e -> e.Authority = "unclassified")
        |> List.length
    let metadataGaps =
        artifactEntries
        |> List.collect (fun e -> e.MetadataGaps)
        |> List.distinct
    let occurrenceCount = List.length occurrences
    let fpCount = List.length fingerprints
    { SchemaVersion = CorpusSummarySchemaVersion
      ExtractorVersion = ExtractorVersion
      ArtifactsTotal = List.length artifactEntries
      RawArtifacts = rawCount
      NormalizedArtifacts = normalizedCount
      DerivedArtifacts = derivedCount
      CorrectionArtifacts = correctionCount
      SourceSnapshotArtifacts = sourceSnapshotCount
      ObsoleteRetainedArtifacts = obsoleteCount
      UnclassifiedArtifacts = unclassifiedCount
      CapturesTotal = capturesTotal
      BinlogCaptures = binlogCaptures
      LegacyTextCaptures = legacyCaptures
      MixedCaptures = mixedCaptures
      OccurrenceCount = occurrenceCount
      UniqueExactFingerprintCount = fpCount
      DuplicateOccurrenceCount = occurrenceCount - fpCount
      DiagnosticLookingUnparsedLines = legacyUnparsed
      MetadataGaps = metadataGaps }

/// Run the full pipeline.  Returns the corpus summary plus the
/// publication outcome.  Used by both the regenerate and verify commands.
let runPipeline
    (repoRoot: string)
    (normalizedDirOverride: string option)
    : CorpusSummary * PublishOutcome * (string * LegacyParseResult) list
      * (string * Result<DiagnosticOccurrence list, string>) list =
    let captures = loadCaptures repoRoot
    let binlogResults = extractBinlogFromCaptures captures repoRoot
    let legacyResults = extractLegacyFromCaptures captures repoRoot
    let binlogOccs =
        binlogResults
        |> List.choose (fun (_, r) ->
            match r with
            | Result.Ok occs -> Some occs
            | Result.Error _ -> None)
        |> List.concat
    let legacyOccs =
        legacyResults
        |> List.collect (fun (_, p) -> p.Occurrences)
    let merged =
        (binlogOccs |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal))
        @ (legacyOccs |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal))
    let fps = aggregateFingerprints merged
    let dupes = duplicateOccurrences merged
    let artifactEntries, migrationMap = buildArtifactManifestEntries repoRoot captures
    let summary =
        buildCorpusSummary captures merged fps legacyResults artifactEntries
    let targetDir =
        match normalizedDirOverride with
        | Some d -> d
        | None -> repoRelative repoRoot normalizedSubdir
    if not (Directory.Exists targetDir) then
        Directory.CreateDirectory targetDir |> ignore
    let files = renderNormalized merged fps dupes artifactEntries migrationMap captures Map.empty summary
    let outcome = publish targetDir true false files
    summary, outcome, legacyResults, binlogResults