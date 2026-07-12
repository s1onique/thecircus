namespace Circus.Contracts

open System
open System.Globalization
open System.Text.Json
open Circus.Domain

/// Canonical CloudEvents spec version this contract targets. The contract
/// pins to the stable `1.0` text. The repository's `main` branch is a
/// future `1.0.3-wip`, so we do not claim conformance to unreleased text.
module CloudEventSpec =
    let Version = "1.0"

    /// CloudEvents content type for the structured JSON binding that this
    /// contract defines as its wire representation.
    let StructuredJsonContentType = "application/cloudevents+json"

    /// Required `datacontenttype` value inside the structured envelope.
    let DataContentType = "application/json"

/// Subset of contract errors that originate inside a known event payload.
/// Kept distinct from envelope-level violations so the caller can tell
/// whether to drop the envelope or to investigate the producer.
type PayloadViolation =
    | PayloadMissingField of FieldName: string
    | PayloadInvalidFieldType of FieldName: string * Expected: string
    | PayloadInvalidFieldValue of FieldName: string * Reason: string

/// Closed violation type covering all contract decoding failures.
type ContractViolation =
    | BodyTooLarge of Maximum: int * Actual: int
    | MalformedJson of BoundedMessage: string
    | RootMustBeObject
    | MissingField of FieldName: string
    | InvalidFieldType of FieldName: string * Expected: string
    | InvalidFieldValue of FieldName: string * Reason: string
    | UnsupportedSpecVersion of string
    | SubjectRunIdMismatch
    | DuplicateField of FieldName: string
    | InvalidExtensionName of Name: string
    | InvalidKnownPayload of EventType: string * Violations: NonEmptyList<PayloadViolation>

/// Documented numerical limits for the Circus contract. Exposed publicly
/// so producers and tests can bound-check their inputs against the same
/// values the decoder uses.
module Limits =
    let MalformedJsonMessageLimit = 200
    let SummaryMaxLength = 4096
    let DurationMaxMilliseconds = 604_800_000L
    let ChecksMaxCount = 1_000_000

/// Result type returned by the contract decoder.
type ValidationResult<'value> =
    Result<'value, NonEmptyList<ContractViolation>>

/// Result type for an in-flight payload-field read. The error channel
/// carries only `PayloadViolation`s; the top-level decoder wraps the
/// collected errors in `ContractViolation.InvalidKnownPayload`.
type PayloadResult<'value> =
    Result<'value, NonEmptyList<PayloadViolation>>

module ValidationResult =
    /// Combine a list of independent `ValidationResult<unit>` values. If
    /// every element is `Ok ()` the result is `Ok ()`; otherwise the
    /// errors are concatenated into a single non-empty list.
    let sequenceUnits (results: ValidationResult<unit> list) : ValidationResult<unit> =
        let mutable errorList : ContractViolation list = []

        for r in results do
            match r with
            | Ok () -> ()
            | Error e -> errorList <- NonEmptyList.toList e @ errorList

        match errorList with
        | [] -> Ok()
        | first :: rest -> Error(NonEmptyList.cons first rest)

    /// Map a function over the success value, preserving errors.
    let map (f: 'a -> 'b) (result: ValidationResult<'a>) : ValidationResult<'b> =
        match result with
        | Ok v -> Ok(f v)
        | Error e -> Error e

/// Lower-case canonical names of all envelope attributes the Circus
/// contract understands. Extension preservation compares property names
/// against this set.
module EnvelopeFieldNames =
    let [<Literal>] SpecVersion = "specversion"
    let [<Literal>] Id = "id"
    let [<Literal>] Source = "source"
    let [<Literal>] Type = "type"
    let [<Literal>] Subject = "subject"
    let [<Literal>] Time = "time"
    let [<Literal>] DataContentType = "datacontenttype"
    let [<Literal>] Data = "data"
    let [<Literal>] CircusInstance = "circusinstance"
    let [<Literal>] CircusEpoch = "circusepoch"
    let [<Literal>] CircusSeq = "circusseq"
    let [<Literal>] RunId = "runid"

    let Reserved =
        set
            [ SpecVersion
              Id
              Source
              Type
              Subject
              Time
              DataContentType
              Data
              CircusInstance
              CircusEpoch
              CircusSeq
              RunId ]

/// A validated CloudEvents envelope decoded by the Circus contract.
type ValidatedEvent =
    {
        EventId: EventId
        Source: EventSource
        EventType: EventType
        Subject: string
        ObservedAt: DateTimeOffset
        InstanceId: InstanceId
        EpochId: EpochId
        Sequence: EventSequence
        RunId: RunId
        Extensions: Map<string, RawJson>
        Event: ExecutionEvent
    }

module ValidatedEvent =
    /// Subject prefix that all Circus envelopes must use. The remainder of
    /// the subject is the textual form of the run identifier.
    let SubjectPrefix = "run/"

/// Internal result envelope used while decoding individual envelope
/// attributes. Separates "missing", "wrong type", and "success" so the
/// decoder can produce precise violations instead of generic strings.
type internal AttributeDecode<'value> =
    | AttributeMissing
    | AttributeWrongType of Expected: string
    | AttributeInvalid of Reason: string
    | AttributeOk of 'value

module internal AttributeDecode =
    /// Convert a per-attribute decode outcome into a contract result.
    let toResult (name: string) (value: AttributeDecode<'value>) : ValidationResult<'value> =
        match value with
        | AttributeOk v -> Ok v
        | AttributeMissing -> Error(NonEmptyList.singleton(MissingField name))
        | AttributeWrongType expected -> Error(NonEmptyList.singleton(InvalidFieldType(name, expected)))
        | AttributeInvalid reason -> Error(NonEmptyList.singleton(InvalidFieldValue(name, reason)))

    /// Convert a per-attribute decode outcome into a `ValidationResult<unit>`
    /// for use with `ValidationResult.sequenceUnits`.
    let toUnitResult (name: string) (value: AttributeDecode<'value>) : ValidationResult<unit> =
        toResult name value |> Result.map ignore

/// Internal primitives used by the envelope and payload decoders.
/// Constants and helpers that are not part of the public API live here.
module internal Primitives =
    /// Maximum number of UTF-8 bytes a malformed-JSON diagnostic may echo back
    /// to the caller. The full body is never returned.
    let MalformedJsonMessageLimit = 200

    /// Maximum length of an optional `summary` payload field.
    let SummaryMaxLength = 4096

    /// Maximum value of a `duration_ms` payload field. Seven days in
    /// milliseconds; chosen as a generous upper bound that still rejects
    /// nonsense values.
    let DurationMaxMilliseconds = 604_800_000L

    /// Truncate `text` to at most `limit` characters, returning the original
    /// value when it is already short enough.
    let bounded (limit: int) (text: string) : string =
        if String.IsNullOrEmpty text then
            text
        elif text.Length <= limit then
            text
        else
            text.Substring(0, limit)

    /// Read a UTF-8 string property, rejecting unexpected JSON kinds.
    let readString (element: JsonElement) : AttributeDecode<string> =
        match element.ValueKind with
        | JsonValueKind.String ->
            let text = element.GetString()

            if String.IsNullOrEmpty text then
                AttributeInvalid "must not be empty"
            else
                AttributeOk text
        | JsonValueKind.Null -> AttributeInvalid "must not be null"
        | _ -> AttributeWrongType "string"

    /// Read a 64-bit integer property, rejecting unexpected JSON kinds and
    /// values that would overflow the Int64 range.
    let readInt64 (element: JsonElement) : AttributeDecode<int64> =
        match element.ValueKind with
        | JsonValueKind.Number ->
            let mutable value = 0L

            if element.TryGetInt64(&value) then
                AttributeOk value
            else
                AttributeInvalid "must fit in Int64"
        | _ -> AttributeWrongType "integer"

    /// Read a `string` element and parse it as a `Guid`. Accepts the canonical
    /// dashed form as well as the digit-only and braced forms accepted by
    /// `Guid.TryParse`.
    let readGuid (element: JsonElement) : AttributeDecode<Guid> =
        match readString element with
        | AttributeOk text ->
            let mutable parsed = Unchecked.defaultof<Guid>

            if Guid.TryParse(text, &parsed) then
                AttributeOk parsed
            else
                AttributeInvalid "must be a UUID"
        | AttributeMissing -> AttributeMissing
        | AttributeWrongType expected -> AttributeWrongType expected
        | AttributeInvalid reason -> AttributeInvalid reason

    /// Parse an ISO-8601 timestamp with an explicit offset. Rejects timestamps
    /// that omit an offset (such as `2026-07-12T20:00:00`) because the wire
    /// contract requires one.
    let readTimestamp (element: JsonElement) : AttributeDecode<DateTimeOffset> =
        match readString element with
        | AttributeOk text ->
            let style = DateTimeStyles.AssumeUniversal ||| DateTimeStyles.AdjustToUniversal
            let mutable parsed = Unchecked.defaultof<DateTimeOffset>

            if DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, style, &parsed) then
                // Require the original text to contain an explicit offset
                // marker so we do not accept naive timestamps.
                let hasOffset =
                    text.Contains "Z"
                    || text.Contains "+"
                    || (text.LastIndexOf '-' > text.IndexOf 'T')

                if hasOffset then
                    AttributeOk parsed
                else
                    AttributeInvalid "timestamp must include an explicit offset"
            else
                AttributeInvalid "must be an ISO-8601 timestamp with offset"
        | AttributeMissing -> AttributeMissing
        | AttributeWrongType expected -> AttributeWrongType expected
        | AttributeInvalid reason -> AttributeInvalid reason

    /// Look up a JSON property by name. Returns `AttributeMissing` when the
    /// property is not present.
    let readAttribute (name: string) (reader: JsonElement -> AttributeDecode<'value>) (root: JsonElement) : AttributeDecode<'value> =
        let mutable element = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(name, &element) then
            reader element
        else
            AttributeMissing

    /// Read the `data` property of the envelope as a raw JSON object.
    let readDataObject (root: JsonElement) : AttributeDecode<JsonElement> =
        let mutable element = Unchecked.defaultof<JsonElement>

        if root.TryGetProperty(EnvelopeFieldNames.Data, &element) then
            match element.ValueKind with
            | JsonValueKind.Object -> AttributeOk element
            | _ -> AttributeInvalid "must be a JSON object"
        else
            AttributeMissing

    /// Decide whether a property name is acceptable as a CloudEvents
    /// extension. The CloudEvents naming convention restricts extension
    /// names to lowercase letters, digits, and the underscore separator.
    let isValidExtensionName (name: string) : bool =
        if String.IsNullOrEmpty name then
            false
        else
            name
            |> Seq.forall (fun c -> (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') || c = '_')

/// Typed values extracted from the envelope but before event payload
/// dispatch. Carries both the typed `EventType` and the original string so
/// dispatch can match on the wire form while the typed envelope records
/// the canonical domain value.
type internal EnvelopeFields =
    {
        EventId: EventId
        Source: EventSource
        EventTypeText: string
        EventType: EventType
        Subject: string
        ObservedAt: DateTimeOffset
        InstanceId: InstanceId
        EpochId: EpochId
        Sequence: EventSequence
        RunId: RunId
        Extensions: Map<string, RawJson>
    }

module internal EnvelopeDecoder =
    open Primitives

    /// Decode `subject` and confirm it is exactly `run/<runid>` where
    /// `<runid>` is the textual form of the supplied `RunId`.
    let validateSubject (runId: RunId) (subject: string) : ValidationResult<string> =
        let expected = ValidatedEvent.SubjectPrefix + (RunId.value runId).ToString()

        if subject = expected then
            Ok subject
        else
            Error(NonEmptyList.singleton SubjectRunIdMismatch)

    /// Extract the textual form of an extension value.
    let readExtensionValue (element: JsonElement) : AttributeDecode<RawJson> =
        let raw = element.GetRawText()

        match element.ValueKind with
        | JsonValueKind.Null -> AttributeInvalid "must not be null"
        | JsonValueKind.Undefined -> AttributeInvalid "must not be undefined"
        | _ when String.IsNullOrWhiteSpace raw -> AttributeInvalid "must not be empty"
        | _ -> AttributeOk(RawJson.unsafeOfString raw)

    /// Build the extension map for a JSON object, preserving unknown
    /// properties in document order while rejecting duplicate or
    /// ill-formed entries.
    let collectExtensions (root: JsonElement) : ValidationResult<Map<string, RawJson>> =
        let mutable entries : (string * RawJson) list = []
        let mutable errorList : ContractViolation list = []

        let recordError err = errorList <- err :: errorList

        for prop in root.EnumerateObject() do
            if not (EnvelopeFieldNames.Reserved.Contains prop.Name) then
                if not (Primitives.isValidExtensionName prop.Name) then
                    recordError(InvalidExtensionName prop.Name)

                match entries |> List.tryFind (fun (n, _) -> n = prop.Name) with
                | Some _ -> recordError(DuplicateField prop.Name)
                | None ->
                    match readExtensionValue prop.Value with
                    | AttributeOk raw -> entries <- (prop.Name, raw) :: entries
                    | AttributeInvalid reason -> recordError(InvalidFieldValue(prop.Name, reason))
                    | AttributeWrongType expected -> recordError(InvalidFieldType(prop.Name, expected))
                    | AttributeMissing -> recordError(MissingField prop.Name)

        match errorList with
        | first :: rest -> Error(NonEmptyList.cons first rest)
        | [] -> Ok(Map.ofList entries)

    /// Attempt to construct a typed `T` value from the result of the
    /// `T.tryCreate` factory. Records a single domain error when the
    /// factory returns `None`.
    let private tryProjectTyped
        (factory: string -> 'typed option)
        (fieldName: string)
        (reason: string)
        (text: string)
        (errors: byref<ContractViolation list>)
        : 'typed option =
        match factory text with
        | Some v -> Some v
        | None ->
            errors <- InvalidFieldValue(fieldName, reason) :: errors
            None

    let private tryProjectGuid
        (factory: Guid -> 'typed option)
        (fieldName: string)
        (reason: string)
        (guid: Guid)
        (errors: byref<ContractViolation list>)
        : 'typed option =
        match factory guid with
        | Some v -> Some v
        | None ->
            errors <- InvalidFieldValue(fieldName, reason) :: errors
            None

    let private tryProjectInt64
        (factory: int64 -> 'typed option)
        (fieldName: string)
        (reason: string)
        (value: int64)
        (errors: byref<ContractViolation list>)
        : 'typed option =
        match factory value with
        | Some v -> Some v
        | None ->
            errors <- InvalidFieldValue(fieldName, reason) :: errors
            None

    /// Decode the CloudEvents common attributes plus Circus extensions.
    let decodeEnvelope (root: JsonElement) : ValidationResult<EnvelopeFields> =
        let extensionsResult = collectExtensions root

        let unitResults : ValidationResult<unit> list =
            [
                extensionsResult |> Result.map ignore
                Primitives.readAttribute EnvelopeFieldNames.SpecVersion Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.SpecVersion
                Primitives.readAttribute EnvelopeFieldNames.Id Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.Id
                Primitives.readAttribute EnvelopeFieldNames.Source Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.Source
                Primitives.readAttribute EnvelopeFieldNames.Type Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.Type
                Primitives.readAttribute EnvelopeFieldNames.Subject Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.Subject
                Primitives.readAttribute EnvelopeFieldNames.Time Primitives.readTimestamp root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.Time
                Primitives.readAttribute EnvelopeFieldNames.DataContentType Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.DataContentType
                Primitives.readAttribute EnvelopeFieldNames.CircusInstance Primitives.readString root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.CircusInstance
                Primitives.readAttribute EnvelopeFieldNames.CircusEpoch Primitives.readGuid root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.CircusEpoch
                Primitives.readAttribute EnvelopeFieldNames.CircusSeq Primitives.readInt64 root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.CircusSeq
                Primitives.readAttribute EnvelopeFieldNames.RunId Primitives.readGuid root
                |> AttributeDecode.toUnitResult EnvelopeFieldNames.RunId
            ]

        match ValidationResult.sequenceUnits unitResults with
        | Error e -> Error e
        | Ok () ->
            // All attributes are present and well-typed; project to typed domain values.
            let specVersionText =
                match Primitives.readAttribute EnvelopeFieldNames.SpecVersion Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: specversion decoded"

            let idText =
                match Primitives.readAttribute EnvelopeFieldNames.Id Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: id decoded"

            let sourceText =
                match Primitives.readAttribute EnvelopeFieldNames.Source Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: source decoded"

            let typeText =
                match Primitives.readAttribute EnvelopeFieldNames.Type Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: type decoded"

            let subjectText =
                match Primitives.readAttribute EnvelopeFieldNames.Subject Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: subject decoded"

            let timeValue =
                match Primitives.readAttribute EnvelopeFieldNames.Time Primitives.readTimestamp root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: time decoded"

            let dataContentTypeText =
                match Primitives.readAttribute EnvelopeFieldNames.DataContentType Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: datacontenttype decoded"

            let instanceText =
                match Primitives.readAttribute EnvelopeFieldNames.CircusInstance Primitives.readString root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: circusinstance decoded"

            let epochGuid =
                match Primitives.readAttribute EnvelopeFieldNames.CircusEpoch Primitives.readGuid root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: circusepoch decoded"

            let seqValue =
                match Primitives.readAttribute EnvelopeFieldNames.CircusSeq Primitives.readInt64 root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: circusseq decoded"

            let runIdGuid =
                match Primitives.readAttribute EnvelopeFieldNames.RunId Primitives.readGuid root with
                | AttributeOk v -> v
                | _ -> failwith "invariant violation: runid decoded"

            if specVersionText <> CloudEventSpec.Version then
                Error(NonEmptyList.singleton(UnsupportedSpecVersion specVersionText))
            elif dataContentTypeText <> CloudEventSpec.DataContentType then
                Error(
                    NonEmptyList.singleton(
                        InvalidFieldValue(
                            EnvelopeFieldNames.DataContentType,
                            sprintf "must be '%s'" CloudEventSpec.DataContentType
                        )
                    )
                )
            else
                let mutable domainErrors : ContractViolation list = []

                let eventIdOpt =
                    tryProjectTyped
                        EventId.tryCreate
                        EnvelopeFieldNames.Id
                        (sprintf "must be 1..%d characters" EventId.maxLength)
                        idText
                        &domainErrors

                let sourceOpt =
                    tryProjectTyped
                        EventSource.tryCreate
                        EnvelopeFieldNames.Source
                        (sprintf "must be 1..%d characters" EventSource.maxLength)
                        sourceText
                        &domainErrors

                let typeOpt =
                    tryProjectTyped
                        EventType.tryCreate
                        EnvelopeFieldNames.Type
                        (sprintf "must be 1..%d characters" EventType.maxLength)
                        typeText
                        &domainErrors

                let instanceOpt =
                    tryProjectTyped
                        InstanceId.tryCreate
                        EnvelopeFieldNames.CircusInstance
                        (sprintf "must be 1..%d characters" InstanceId.maxLength)
                        instanceText
                        &domainErrors

                let epochOpt =
                    tryProjectGuid
                        EpochId.tryCreate
                        EnvelopeFieldNames.CircusEpoch
                        "must be a non-empty UUID"
                        epochGuid
                        &domainErrors

                let seqOpt =
                    tryProjectInt64
                        EventSequence.tryCreate
                        EnvelopeFieldNames.CircusSeq
                        "must be 0..Int64.MaxValue"
                        seqValue
                        &domainErrors

                let runIdOpt =
                    tryProjectGuid
                        RunId.tryCreate
                        EnvelopeFieldNames.RunId
                        "must be a non-empty UUID"
                        runIdGuid
                        &domainErrors

                match domainErrors with
                | first :: rest -> Error(NonEmptyList.cons first rest)
                | [] ->
                    let runId =
                        match runIdOpt with
                        | Some v -> v
                        | None -> failwith "invariant violation: runid validated"

                    match validateSubject runId subjectText with
                    | Error e -> Error e
                    | Ok subject ->
                        let extensions =
                            match extensionsResult with
                            | Ok m -> m
                            | Error _ -> Map.empty

                        Ok
                            {
                                EventId = Option.get eventIdOpt
                                Source = Option.get sourceOpt
                                EventTypeText = typeText
                                EventType = Option.get typeOpt
                                Subject = subject
                                ObservedAt = timeValue
                                InstanceId = Option.get instanceOpt
                                EpochId = Option.get epochOpt
                                Sequence = Option.get seqOpt
                                RunId = runId
                                Extensions = extensions
                            }

module internal EnvelopeFields =
    let toValidated (fields: EnvelopeFields) (event: ExecutionEvent) : ValidatedEvent =
        {
            EventId = fields.EventId
            Source = fields.Source
            EventType = fields.EventType
            Subject = fields.Subject
            ObservedAt = fields.ObservedAt
            InstanceId = fields.InstanceId
            EpochId = fields.EpochId
            Sequence = fields.Sequence
            RunId = fields.RunId
            Extensions = fields.Extensions
            Event = event
        }
