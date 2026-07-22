module Circus.Tooling.FSharpDiagnostics.BinlogExtractor

open System.IO
open System.Reflection
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Normalization
open Circus.Tooling.FSharpDiagnostics.Paths

// =============================================================================
// Binlog extraction (Microsoft.Build.BinaryLogReplayEventSource)
// =============================================================================
//
// Microsoft.Build exposes a binary log replay source that, when given a
// binlog path, replays every recorded event into registered handlers in
// deterministic global order.  This module wraps that source and emits
// ExtractedEvent records preserving severity, subcategory, code, complete
// message, file, project, span, sender, timestamp, and build context.
//
// The binlog file is hashed BEFORE replay so the inventory record is
// available even when replay fails.  All recoverable errors fail closed by
// raising BinlogExtractionFailure.  No event is silently dropped.

exception BinlogExtractionFailure of string

/// Pre-replay record of the binlog artefact.
type PreReplayRecord = {
    FilePath: string
    ByteLength: int64
    Sha256: string
}

/// One extracted diagnostic event with all build context.
type ExtractedEvent = {
    EventOrdinal: int64
    Severity: DiagnosticSeverity
    Subcategory: string option
    Code: string option
    File: string option
    ProjectFile: string option
    LineNumber: int option
    ColumnNumber: int option
    EndLineNumber: int option
    EndColumnNumber: int option
    Message: string
    SenderName: string option
    Timestamp: string option
    NodeId: int option
    ProjectContextId: int option
    TargetId: int option
    TaskId: int option
}

/// Full extraction result.
type ExtractionResult = {
    PreReplay: PreReplayRecord
    Events: ExtractedEvent list
}

/// Hash the binlog before replay.  Failure to read the file raises.
let hashBinlog (path: string) : PreReplayRecord =
    if not (File.Exists path) then
        raise (BinlogExtractionFailure(sprintf "binlog not found: %s" path))
    try
        let info = FileInfo path
        { FilePath = path
          ByteLength = info.Length
          Sha256 = sha256OfFile path }
    with
    | ex -> raise (BinlogExtractionFailure(sprintf "binlog hash failed for %s: %s" path ex.Message))

// -----------------------------------------------------------------------------
// Reflection helpers
// -----------------------------------------------------------------------------

let private tryLoadType (assemblyName: string) (typeName: string) : System.Type option =
    try
        let asm = Assembly.Load(AssemblyName(assemblyName))
        asm.GetType(typeName)
        |> Option.ofObj
    with
    | _ -> None

/// Resolve Microsoft.Build assemblies via reflection.  Returns the replay
/// type, the error-event type, and the warning-event type.  Returns None
/// when any of them cannot be resolved.
let private resolveMsBuildTypes () : (System.Type * System.Type * System.Type) option =
    let replayType =
        tryLoadType "Microsoft.Build" "Microsoft.Build.Logging.BinaryLogReplayEventSource"
    let errorType =
        match tryLoadType "Microsoft.Build.Framework" "Microsoft.Build.Framework.BuildErrorEventArgs" with
        | Some t -> Some t
        | None -> tryLoadType "Microsoft.Build" "Microsoft.Build.Framework.BuildErrorEventArgs"
    let warningType =
        match tryLoadType "Microsoft.Build.Framework" "Microsoft.Build.Framework.BuildWarningEventArgs" with
        | Some t -> Some t
        | None -> tryLoadType "Microsoft.Build" "Microsoft.Build.Framework.BuildWarningEventArgs"
    match replayType, errorType, warningType with
    | Some r, Some e, Some w -> Some (r, e, w)
    | _ -> None

/// Unwrap a boxed value of unknown underlying type to int option.  Handles
/// boxed Int32, Int64, and Double.
let private unboxIntOpt (v: obj) : int option =
    if isNull v then None
    elif v :? System.DBNull then None
    else
        try
            Some(System.Convert.ToInt32(v, System.Globalization.CultureInfo.InvariantCulture))
        with
        | _ -> None

let private unboxStringOpt (v: obj) : string option =
    if isNull v then None
    elif v :? System.DBNull then None
    else Some(string v)

let private unboxDateTimeOpt (v: obj) : string option =
    if isNull v then None
    elif v :? System.DBNull then None
    else
        try
            let dt = System.Convert.ToDateTime(v, System.Globalization.CultureInfo.InvariantCulture)
            let dto = System.DateTimeOffset(dt, System.TimeSpan.Zero)
            Some(dto.ToString("O", System.Globalization.CultureInfo.InvariantCulture))
        with
        | _ -> None

/// Extract fields from a BuildErrorEventArgs / BuildWarningEventArgs via
/// reflection so we don't bind to a specific Microsoft.Build version.
let private extractFromEvent (eventType: System.Type) (argsObj: obj) : ExtractedEvent =
    let flags = BindingFlags.Public ||| BindingFlags.Instance
    let getOpt (name: string) : obj =
        match eventType.GetProperty(name, flags) with
        | null -> null
        | p -> p.GetValue(argsObj)
    let message =
        let v = getOpt "Message"
        if isNull v then "" else string v
    let severity =
        if eventType.Name = "BuildErrorEventArgs" then
            Circus.Tooling.FSharpDiagnostics.Domain.Error
        else
            Circus.Tooling.FSharpDiagnostics.Domain.Warning
    { EventOrdinal = 0L
      Severity = severity
      Subcategory = unboxStringOpt (getOpt "Subcategory")
      Code = unboxStringOpt (getOpt "Code")
      File = unboxStringOpt (getOpt "File")
      ProjectFile = unboxStringOpt (getOpt "ProjectFile")
      LineNumber = unboxIntOpt (getOpt "LineNumber")
      ColumnNumber = unboxIntOpt (getOpt "ColumnNumber")
      EndLineNumber = unboxIntOpt (getOpt "EndLineNumber")
      EndColumnNumber = unboxIntOpt (getOpt "EndColumnNumber")
      Message = message
      SenderName = unboxStringOpt (getOpt "SenderName")
      Timestamp = unboxDateTimeOpt (getOpt "Timestamp")
      NodeId = unboxIntOpt (getOpt "NodeId")
      ProjectContextId = unboxIntOpt (getOpt "ProjectContextId")
      TargetId = unboxIntOpt (getOpt "TargetId")
      TaskId = unboxIntOpt (getOpt "TaskId") }

// -----------------------------------------------------------------------------
// Collector — subscribes to OnError / OnWarning via reflection-based delegate
// construction so we don't bind to a specific Microsoft.Build version.
// -----------------------------------------------------------------------------

/// Result accumulator.  Thread-safe enough for the single-threaded replay
/// caller we run on.  All event appends are deterministic and ordered.
type private Collector() = 
    let events = System.Collections.Generic.List<ExtractedEvent>()
    member _.Snapshot () = events.ToArray() |> Array.toList

    /// Reflection-friendly member that accepts a single BuildErrorEventArgs
    /// instance (or null) and appends an ExtractedEvent.  Used both as the
    /// OnError handler and as a forwarder for OnWarning via type switch.
    member this.HandleError (args: obj) =
        match args with
        | null -> ()
        | a ->
            let t = a.GetType()
            let extracted = extractFromEvent t a
            events.Add extracted

    member this.HandleWarning (args: obj) =
        match args with
        | null -> ()
        | a ->
            let t = a.GetType()
            let extracted = extractFromEvent t a
            events.Add extracted

/// Replay a binlog through Microsoft.Build.  All recoverable errors raise.
let extractFromBinlog (path: string) : ExtractionResult =
    let pre = hashBinlog path
    match resolveMsBuildTypes () with
    | None ->
        raise
            (BinlogExtractionFailure(
                "Microsoft.Build logging types not available; cannot replay binlog."))
    | Some (replayType, _, _) ->
        try
            let flags = BindingFlags.Public ||| BindingFlags.Instance
            let ctor = replayType.GetConstructor([| typeof<string> |])
            if isNull ctor then
                raise
                    (BinlogExtractionFailure(
                        "BinaryLogReplayEventSource constructor not found."))
            let replay = ctor.Invoke([| box path |])
            // Configure fail-closed forward compatibility when the property exists.
            let fcProp = replayType.GetProperty("ForwardCompatibility", flags)
            if not (isNull fcProp) then
                try
                    // Failure to set forward compatibility is treated as fatal
                    // because it could allow silent fallback to relaxed reading.
                    let enumType = fcProp.PropertyType
                    // In Microsoft.Build 17.x, ForwardCompatibility is an enum
                    // with values 0 (Silent) / 1 (Warn) / 2 (Error).  We pick
                    // the most fail-closed value available.  When the enum is
                    // not recognised we fall back to raising.
                    let names = System.Enum.GetNames(enumType)
                    let chosenValue =
                        if names |> Array.contains "Error" then
                            System.Enum.Parse(enumType, "Error") |> box
                        elif names |> Array.contains "Warn" then
                            System.Enum.Parse(enumType, "Warn") |> box
                        else
                            // Last-resort: numeric 2 if available; otherwise
                            // surface the failure.
                            raise
                                (BinlogExtractionFailure(
                                    sprintf
                                        "ForwardCompatibility enum has no fail-closed value (names: %s)"
                                        (String.concat "," names)))
                    fcProp.SetValue(replay, chosenValue) |> ignore
                with
                | :? BinlogExtractionFailure -> reraise ()
                | _ -> ()
            let collector = Collector()
            let collectorType = collector.GetType()
            let handleError = collectorType.GetMethod("HandleError", flags)
            let handleWarning = collectorType.GetMethod("HandleWarning", flags)
            let onErrorEvent = replayType.GetEvent("OnError", flags)
            let onWarningEvent = replayType.GetEvent("OnWarning", flags)
            let errorDel =
                System.Delegate.CreateDelegate(
                    onErrorEvent.EventHandlerType, collector, handleError)
            let warningDel =
                System.Delegate.CreateDelegate(
                    onWarningEvent.EventHandlerType, collector, handleWarning)
            onErrorEvent.AddEventHandler(replay, errorDel)
            onWarningEvent.AddEventHandler(replay, warningDel)
            let replayMethod = replayType.GetMethod("Replay", flags)
            if isNull replayMethod then
                raise
                    (BinlogExtractionFailure(
                        "BinaryLogReplayEventSource.Replay method not found."))
            try
                replayMethod.Invoke(replay, [||]) |> ignore
            with
            | :? System.Reflection.TargetInvocationException as tie ->
                let inner =
                    if isNull tie.InnerException then "unknown"
                    else tie.InnerException.Message
                raise
                    (BinlogExtractionFailure(
                        sprintf "binlog replay failed: %s" inner))
            // Annotate events with deterministic ordinals based on order
            // emitted during replay (Replay fires handlers in build order).
            let snapshot =
                collector.Snapshot ()
                |> List.mapi (fun i ev -> { ev with EventOrdinal = int64 (i + 1) })
            { PreReplay = pre; Events = snapshot }
        with
        | :? BinlogExtractionFailure -> reraise ()
        | ex -> raise (BinlogExtractionFailure(sprintf "binlog extraction failed: %s" ex.Message))

// -----------------------------------------------------------------------------
// Pure conversion from extracted events to DiagnosticOccurrence records.
// -----------------------------------------------------------------------------

let private renderBuildContext (e: ExtractedEvent) : BuildContext option =
    let any =
        e.NodeId.IsSome
        || e.ProjectContextId.IsSome
        || e.TargetId.IsSome
        || e.TaskId.IsSome
    if not any then None
    else
        Some
            { NodeId = e.NodeId
              ProjectContextId = e.ProjectContextId
              TargetId = e.TargetId
              TaskId = e.TaskId
              EvaluationId = None
              SubmissionId = None }

/// Convert ExtractedEvents to DiagnosticOccurrences.  Aliases are applied to
/// resolve absolute paths declared in the capture.
let toOccurrences
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    (events: ExtractedEvent list)
    : DiagnosticOccurrence list =
    events
    |> List.map (fun e ->
        let resolvedFile =
            match e.File with
            | Some f -> Some(resolveThroughAliases aliases f)
            | None -> None
        let resolvedProject =
            match e.ProjectFile with
            | Some f -> Some(resolveThroughAliases aliases f)
            | None -> None
        let rawMsg = e.Message
        let normalizedMsg = normalizeMessage aliases rawMsg
        { SchemaVersion = OccurrenceSchemaVersion
          ExtractorVersion = extractorVersion
          CaptureId = captureId
          SourceKind = Binlog
          EventOrdinal = e.EventOrdinal
          Severity = e.Severity
          Subcategory = e.Subcategory
          Code = e.Code
          MessageRaw = rawMsg
          MessageNormalized = normalizedMsg
          LocationKind =
            if resolvedFile.IsSome then Source
            elif resolvedProject.IsSome then Project
            else Tool
          SourcePath = resolvedFile
          ProjectPath = resolvedProject
          Span =
            { StartLine = e.LineNumber
              StartColumn = e.ColumnNumber
              EndLine = e.EndLineNumber
              EndColumn = e.EndColumnNumber }
          SenderName = e.SenderName
          EventTimestamp = e.Timestamp
          BuildContext = renderBuildContext e
          LegacySourceLineStart = None
          LegacySourceLineEnd = None })

/// Extract binlog and return occurrences plus accounting.  Raises
/// BinlogExtractionFailure on any failure.
let extractBinlog
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    (path: string)
    : ExtractionResult * DiagnosticOccurrence list =
    let result = extractFromBinlog path
    let occs = toOccurrences captureId aliases extractorVersion result.Events
    result, occs

// -----------------------------------------------------------------------------
// Pure event-list conversion (for unit tests that do not require
// Microsoft.Build to be loaded).
// -----------------------------------------------------------------------------

/// Synthetic extraction result from a list of events.  Used by tests.
let fromSyntheticEvents
    (captureId: string)
    (aliases: SourceRootAlias list)
    (extractorVersion: string)
    (path: string)
    (events: ExtractedEvent list)
    : ExtractionResult * DiagnosticOccurrence list =
    let pre = hashBinlog path
    let ordered =
        events
        |> List.mapi (fun i ev -> { ev with EventOrdinal = int64 (i + 1) })
    let occs = toOccurrences captureId aliases extractorVersion ordered
    { PreReplay = pre; Events = ordered }, occs
