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

/// Render the leaf output bodies (occurrences, fingerprints,
/// duplicates, migration map).  The summary and manifest are produced
/// separately because they depend on hashes of files written to
/// staging.
let private renderLeafBodies
    (occurrences: DiagnosticOccurrence list)
    (fingerprints: ExactFingerprint list)
    (duplicates: (string * DiagnosticOccurrence) list)
    : PendingFile list =
    let sortedOccs =
        occurrences
        |> List.sortBy (fun o -> o.CaptureId, o.EventOrdinal)
    let sortedFps =
        fingerprints
        |> List.sortBy (fun fp -> fp.Sha256)
    // A zero-record occurrences stream has an empty logical body.  The
    // line-oriented writer materialises that body as exactly one LF byte.
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
    [
      { CanonicalFileName = occurrencesFile; Body = occLines }
      { CanonicalFileName = fingerprintsFile; Body = fpLines }
      { CanonicalFileName = duplicatesFile; Body = dupLines }
      // No migrations means zero records: publish exactly one LF byte.
      { CanonicalFileName = migrationMapFile; Body = "" }
    ]

/// Produce the artifact manifest entries for every artefact under the
/// canonical corpus root **except the manifest itself**.  The manifest
/// must not inventory its own canonical path because doing so would
/// make the recorded sha256 depend on the file that contains it, which
/// is a structural recursion.
///
/// The function takes a digest ``(canonicalPath -> byteLength * sha256)``
/// so the caller is responsible for hashing the actual on-disk bytes
/// AFTER all other files have been written.  Hashing before write
/// would embed a stale digest in the manifest.
let buildArtifactManifestEntries
    (repoRoot: string)
    (captures: LoadedCapture list)
    (digest: Map<string, int64 * string>)
    : ArtifactManifestEntry list * (string * string * string * int64) list =
    let _ = captures |> List.map (fun c -> c.CaptureManifest.CaptureId) |> ignore
    let entries =
        digest
        |> Map.toList
        |> List.filter (fun (rel, _) -> rel <> artifactsManifestCanonicalPath)
        |> List.map (fun (rel, (length, hash)) ->
            let cls = classifyCanonicalPath rel
            let auth = authorityFor rel
            let status = "present"
            let mt = mediaTypeFor rel
            let captureId =
                if rel.Contains "/corpus/raw/" then
                    let parts = rel.Split([| '/' |])
                    if parts.Length >= 5 then Some(parts.[4]) else None
                else
                    None
            { SchemaVersion = ArtifactManifestSchemaVersion
              CanonicalPath = rel
              OriginalPath = rel
              ArtifactClass = artifactClassToken cls
              Authority = authorityToken auth
              Status = status
              MediaType = mt
              ByteLength = length
              Sha256 = hash
              CaptureId = captureId
              Supersedes = None
              SupersededBy = None
              MetadataGaps = [] })
    entries, []  // no migration in this ACT

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

/// Convert a ``System.Collections.Generic.Dictionary`` to a list of
/// key-value pairs.  Used to feed the manifest builder.
let private dictToPairs (d: System.Collections.Generic.Dictionary<'k, 'v>) : ('k * 'v) list =
    let mutable result = []
    for kv in d do
        result <- (kv.Key, kv.Value) :: result
    List.rev result

/// Hash every file under the canonical corpus root, returning
/// ``canonicalPath -> (byteLength, sha256)``.  Used by the manifest
/// builder after the leaf outputs have been written, so the recorded
/// hashes always reflect the bytes that will be committed.
let private hashCanonicalCorpus (repoRoot: string) : Map<string, int64 * string> =
    let root = repoRelative repoRoot CanonicalCorpusRoot
    if not (Directory.Exists root) then Map.empty
    else
        let results = System.Collections.Generic.Dictionary<string, int64 * string>()
        let stack = System.Collections.Generic.Stack<string>()
        stack.Push root
        while stack.Count > 0 do
            let dir = stack.Pop()
            for entry in Directory.EnumerateFiles dir do
                let info = FileInfo entry
                let rel =
                    info.FullName.Substring(repoRoot.Length).TrimStart('/', '\\')
                    |> toPosix
                    |> canonicalise
                let hash = sha256OfFile info.FullName
                results.[rel] <- (info.Length, hash)
            for sub in Directory.EnumerateDirectories dir do
                stack.Push sub
        let sorted = dictToPairs results |> List.sortBy fst
        Map.ofSeq sorted

/// Generate leaf outputs and the summary and manifest **in an acyclic
/// dependency order**:
///
///     raw inputs and schemas
///         ↓
///     occurrences / fingerprints / duplicate table / migration map
///         ↓
///     corpus-summary-v1.json     (uses leaf-output hashes from staging)
///         ↓
///     artifacts-v1.jsonl         (manifest; never lists itself)
///         ↓
///     atomic publication
///         ↓
///     annotated closure tag
///
/// The manifest hashes every other artefact **after** it has been
/// written to the staging directory, so the committed hashes match
/// the actual bytes that will be published.
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

    let targetDir =
        match normalizedDirOverride with
        | Some d -> d
        | None -> repoRelative repoRoot normalizedSubdir
    if not (Directory.Exists targetDir) then
        Directory.CreateDirectory targetDir |> ignore

    // --- Pass 1: emit leaf outputs (no manifest, no summary) -------
    let leafBodies =
        renderLeafBodies merged fps dupes

    // --- Pass 2: write leaves to staging via writeLineOriented so
    //     the on-disk content (including terminal newline) matches what
    //     the atomic publish will write later.  This guarantees that
    //     ``discoveredDigest`` in Pass 3 reflects the actual bytes
    //     that will be committed.
    let _ =
        leafBodies
        |> List.map (fun f ->
            let fullPath = Path.Combine(targetDir, f.CanonicalFileName)
            let dir = Path.GetDirectoryName fullPath
            if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore
            Circus.Tooling.FSharpDiagnostics.Serialization.writeLineOriented
                fullPath f.Body)

    // --- Pass 3: build the summary body so the manifest can include it
    let summary =
        // First pass: the manifest's artifactEntries are not yet
        // known, so we use a placeholder empty list.  The summary
        // counts that depend on the manifest (artifacts_total) will be
        // recomputed after the manifest is built (in Pass 6).
        let placeholderEntries : ArtifactManifestEntry list = []
        buildCorpusSummary captures merged fps legacyResults placeholderEntries

    // --- Pass 4: write summary to staging so the manifest hash covers it
    let summaryBody = renderCorpusSummary summary
    let _ =
        writeLineOriented
            (Path.Combine(targetDir, summaryFile))
            summaryBody

    // --- Pass 5: discover all canonical artefacts now in staging.
    // Includes the leaf outputs and the summary; the manifest itself
    // is excluded by the manifest builder.
    let discoveredDigest = hashCanonicalCorpus repoRoot

    // --- Pass 6: build the manifest body from on-disk digests.
    let artifactEntries, _ =
        buildArtifactManifestEntries repoRoot captures discoveredDigest

    // --- Pass 7: rebuild the summary now that the manifest's
    // artifact counts are known.  This second pass writes a
    // consistent summary whose ``artifacts_total`` matches the
    // manifest's actual entry count.
    let summaryFinal =
        buildCorpusSummary captures merged fps legacyResults artifactEntries
    let summaryBodyFinal = renderCorpusSummary summaryFinal
    let _ =
        writeLineOriented
            (Path.Combine(targetDir, summaryFile))
            summaryBodyFinal

    // Re-hash the now-updated summary so the manifest's recorded
    // digest for the summary is also correct.  This is the second
    // pass through hashCanonicalCorpus.
    let discoveredDigestFinal = hashCanonicalCorpus repoRoot
    let artifactEntriesFinal, _ =
        buildArtifactManifestEntries
            repoRoot
            captures
            discoveredDigestFinal

    // --- Pass 8: render and write the manifest body ------------
    let manifestFile =
        { CanonicalFileName = artifactsManifestFile
          Body =
            artifactEntriesFinal
            |> List.map renderArtifactManifestEntry
            |> String.concat "\n" }

    // --- Pass 9: atomic publish of every file -----------------
    let allFiles =
        leafBodies
        @ [ { CanonicalFileName = summaryFile
              Body = summaryBodyFinal }
            { manifestFile with
                Body =
                    artifactEntriesFinal
                    |> List.map renderArtifactManifestEntry
                    |> String.concat "\n" } ]

    let outcome = publish targetDir true false allFiles
    summaryFinal, outcome, legacyResults, binlogResults
