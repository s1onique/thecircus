namespace Circus.Contracts

open System
open System.Text.Json
open Circus.Domain

module EventDecoder =
    open Primitives

    /// Production-default maximum input size for `decode`, expressed in bytes.
    /// Matches the value documented in the Circus wire contract. 256 KiB is
    /// generous enough for any plausible Leamas event while still bounding the
    /// memory and parsing work the decoder may perform per request.
    [<Literal>]
    let DefaultMaximumBytes = 262144

    /// Internal core of the decoder. Operates on an already-parsed `JsonElement`
    /// so the byte-bound and JSON-parse stages can be exercised independently.
    /// All conversion of `JsonElement` references to plain F# values happens
    /// inside this function so the resulting `ValidatedEvent` does not borrow
    /// from a disposed `JsonDocument`.
    let rec internal decodeFromRoot (root: JsonElement) : ValidationResult<ValidatedEvent> =
        if root.ValueKind <> JsonValueKind.Object then
            Error(NonEmptyList.singleton RootMustBeObject)
        else
            match EnvelopeDecoder.decodeEnvelope root with
            | Error e -> Error e
            | Ok fields ->
                match Primitives.readDataObject root with
                | AttributeMissing ->
                    Error(NonEmptyList.singleton(MissingField EnvelopeFieldNames.Data))
                | AttributeInvalid r ->
                    Error(NonEmptyList.singleton(InvalidFieldValue(EnvelopeFieldNames.Data, r)))
                | AttributeWrongType _ ->
                    Error(NonEmptyList.singleton(InvalidFieldType(EnvelopeFieldNames.Data, "object")))
                | AttributeOk data -> dispatchAndValidate fields data

    /// Internal dispatch onto payload decoders. Unknown event types are
    /// preserved via `UnrecognizedEvent` rather than being rejected.
    and internal dispatchAndValidate
        (fields: EnvelopeFields)
        (data: JsonElement)
        : ValidationResult<ValidatedEvent> =
        match fields.EventTypeText with
        | StartedPayload.FieldNames.EventType ->
            StartedPayload.decode data fields.RunId
            |> Result.map ExecutionStartedEvent
        | FinishedPayload.FieldNames.EventType ->
            FinishedPayload.decode data fields.RunId
            |> Result.map ExecutionFinishedEvent
        | other ->
            let rawDataOpt =
                if data.ValueKind = JsonValueKind.Null then
                    None
                else
                    Some(RawJson.unsafeOfString(data.GetRawText()))

            Ok(
                UnrecognizedEvent
                    { EventType = other
                      Data = rawDataOpt }
            )
        |> function
            | Error e -> Error e
            | Ok event -> Ok(EnvelopeFields.toValidated fields event)

    /// Decode a Circus-bound Leamas execution event from raw bytes. The
    /// decoder is pure and deterministic; expected malformed input yields a
    /// typed `NonEmptyList<ContractViolation>` rather than throwing. The
    /// byte bound is enforced before any parsing work runs.
    ///
    /// Behaviour:
    ///   1. enforce the byte bound before parsing;
    ///   2. reject malformed JSON with a bounded diagnostic;
    ///   3. require a JSON object at the root;
    ///   4. decode generic CloudEvents attributes;
    ///   5. decode Circus extension attributes;
    ///   6. validate envelope relationships (subject / runid);
    ///   7. dispatch recognized event types (`started`, `finished`);
    ///   8. preserve unknown event types and their `data` JSON;
    ///   9. accumulate independent violations where possible.
    let decode (maximumBytes: int) (payload: ReadOnlyMemory<byte>) : ValidationResult<ValidatedEvent> =
        if maximumBytes < 0 then
            invalidArg "maximumBytes" "must be non-negative"
        elif payload.Length > maximumBytes then
            Error(NonEmptyList.singleton(BodyTooLarge(maximumBytes, payload.Length)))
        else
            try
                use doc = JsonDocument.Parse(payload, JsonDocumentOptions())
                decodeFromRoot doc.RootElement
            with
            | :? JsonException as ex ->
                Error(NonEmptyList.singleton(MalformedJson(Primitives.bounded Primitives.MalformedJsonMessageLimit ex.Message)))
            | :? ArgumentException as ex ->
                Error(NonEmptyList.singleton(MalformedJson(Primitives.bounded Primitives.MalformedJsonMessageLimit ex.Message)))
