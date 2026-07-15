namespace Circus.Contracts

open System
open System.Text.Json
open Circus.Domain

module internal StartedPayload =
    open Primitives

    /// Lower-case canonical names and the recognised event-type string for
    /// the `io.leamas.execution.started.v1` payload.
    module internal FieldNames =
        [<Literal>]
        let EventType = "io.leamas.execution.started.v1"

        [<Literal>]
        let RepositoryRef = "repository_ref"

        [<Literal>]
        let ActId = "act_id"

        [<Literal>]
        let LeamasVersion = "leamas_version"

        [<Literal>]
        let GitRevision = "git_revision"

        [<Literal>]
        let StartedBy = "started_by"

    /// Wire-format limits for the optional payload fields.
    module internal Limits =
        let GitRevisionMaxLength = 128
        let StartedByMaxLength = 128

    /// Read an optional string payload field. Returns `Ok None` when the
    /// field is absent or explicitly null; returns an `Error` containing
    /// `PayloadViolation`s when the field is present but empty or too long.
    let private readOptionalText (name: string) (maxLength: int) (root: JsonElement) : PayloadResult<string option> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(name, &el) with
        | false -> Ok None
        | true ->
            match el.ValueKind with
            | JsonValueKind.Null -> Ok None
            | JsonValueKind.String ->
                let text = el.GetString()

                if String.IsNullOrEmpty text then
                    Error(NonEmptyList.singleton (PayloadInvalidFieldValue(name, "must not be empty when present")))
                elif text.Length > maxLength then
                    Error(
                        NonEmptyList.singleton (
                            PayloadInvalidFieldValue(name, sprintf "must not exceed %d characters" maxLength)
                        )
                    )
                else
                    Ok(Some text)
            | _ -> Error(NonEmptyList.singleton (PayloadInvalidFieldType(name, "string or null")))

    /// Read a required string payload field within the documented length
    /// bounds. Empty or missing values yield `PayloadMissingField` /
    /// `PayloadInvalidFieldValue` violations.
    let private readRequiredText
        (name: string)
        (minLength: int)
        (maxLength: int)
        (root: JsonElement)
        : PayloadResult<string> =
        let mutable el = Unchecked.defaultof<JsonElement>

        match root.TryGetProperty(name, &el) with
        | false -> Error(NonEmptyList.singleton (PayloadMissingField name))
        | true ->
            match el.ValueKind with
            | JsonValueKind.String ->
                let text = el.GetString()

                if String.IsNullOrEmpty text then
                    Error(NonEmptyList.singleton (PayloadInvalidFieldValue(name, "must not be empty")))
                elif text.Length < minLength || text.Length > maxLength then
                    Error(
                        NonEmptyList.singleton (
                            PayloadInvalidFieldValue(name, sprintf "length must be %d..%d" minLength maxLength)
                        )
                    )
                else
                    Ok text
            | _ -> Error(NonEmptyList.singleton (PayloadInvalidFieldType(name, "string")))

    /// Decode an `ExecutionStarted` payload from the envelope's `data`
    /// object. All five fields are read independently; any
    /// `PayloadViolation`s are accumulated under
    /// `InvalidKnownPayload(FieldNames.EventType, _)`.
    let decode (data: JsonElement) (runId: RunId) : ValidationResult<ExecutionStarted> =
        let repoRes =
            readRequiredText FieldNames.RepositoryRef RepositoryRef.minLength RepositoryRef.maxLength data

        let actIdRes = readOptionalText FieldNames.ActId ActId.maxLength data

        let leamasRes =
            readRequiredText FieldNames.LeamasVersion LeamasVersion.minLength LeamasVersion.maxLength data

        let gitRes =
            readOptionalText FieldNames.GitRevision Limits.GitRevisionMaxLength data

        let byRes = readOptionalText FieldNames.StartedBy Limits.StartedByMaxLength data

        let mutable errors: PayloadViolation list = []

        let collect (r: PayloadResult<'v>) =
            match r with
            | Ok _ -> ()
            | Error n -> errors <- NonEmptyList.toList n @ errors

        collect repoRes
        collect actIdRes
        collect leamasRes
        collect gitRes
        collect byRes

        match errors with
        | first :: rest ->
            Error(NonEmptyList.singleton (InvalidKnownPayload(FieldNames.EventType, NonEmptyList.cons first rest)))
        | [] ->
            let unwrap (label: string) (r: PayloadResult<'v>) : 'v =
                match r with
                | Ok v -> v
                | Error _ -> failwithf "invariant violation: %s succeeded above" label

            let make =
                { RunId = runId
                  Repository =
                    match RepositoryRef.tryCreate (unwrap FieldNames.RepositoryRef repoRes) with
                    | Some v -> v
                    | None -> failwithf "invariant: repository_ref validated"
                  ActId = unwrap FieldNames.ActId actIdRes |> Option.bind ActId.tryCreate
                  LeamasVersion =
                    match LeamasVersion.tryCreate (unwrap FieldNames.LeamasVersion leamasRes) with
                    | Some v -> v
                    | None -> failwithf "invariant: leamas_version validated"
                  GitRevision = unwrap FieldNames.GitRevision gitRes
                  StartedBy = unwrap FieldNames.StartedBy byRes }

            Ok make
