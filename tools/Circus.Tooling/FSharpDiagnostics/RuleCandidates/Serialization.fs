module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Selection

type JsonValue =
    | JsonString of string
    | JsonNumber of decimal
    | JsonBool of bool
    | JsonNull
    | JsonArray of JsonValue list
    | JsonObject of (string * JsonValue) list

let private parseJsonString (s: string) (startIdx: int) : string * int =
    let sb = StringBuilder()
    let mutable i = startIdx + 1

    while i < s.Length && s.[i] <> '"' do
        if s.[i] = '\\' && i + 1 < s.Length then
            i <- i + 1

        sb.Append(s.[i]) |> ignore
        i <- i + 1

    sb.ToString(), i + 1

let private parseJsonNumber (s: string) (startIdx: int) : decimal * int =
    let mutable endIdx = startIdx

    while endIdx < s.Length
          && (Char.IsDigit(s.[endIdx])
              || s.[endIdx] = '.'
              || s.[endIdx] = '-'
              || s.[endIdx] = '+'
              || s.[endIdx] = 'e'
              || s.[endIdx] = 'E') do
        endIdx <- endIdx + 1

    System.Decimal.Parse(s.Substring(startIdx, endIdx - startIdx), CultureInfo.InvariantCulture), endIdx

let rec private parseJsonValue (s: string) (startIdx: int) : JsonValue * int =
    let mutable i = startIdx

    while i < s.Length && Char.IsWhiteSpace(s.[i]) do
        i <- i + 1

    if i >= s.Length then
        failwith "Unexpected end"

    match s.[i] with
    | '"' -> let str, ni = parseJsonString s i in JsonString str, ni
    | '[' ->
        let mutable vals = []
        let mutable j = i + 1

        while j < s.Length && s.[j] <> ']' do
            let v, nj = parseJsonValue s j
            vals <- v :: vals
            j <- nj

            if j < s.Length && s.[j] = ',' then
                j <- j + 1

        JsonArray(List.rev vals), j + 1
    | '{' ->
        let mutable pairs = []
        let mutable j = i + 1

        while j < s.Length && s.[j] <> '}' do
            if s.[j] = '"' then
                let key, ke = parseJsonString s j
                j <- ke

                if j < s.Length && s.[j] = ':' then
                    j <- j + 1

                let v, ve = parseJsonValue s j
                pairs <- (key, v) :: pairs
                j <- ve

                if j < s.Length && s.[j] = ',' then
                    j <- j + 1

        JsonObject(List.rev pairs), j + 1
    | 't' when i + 4 <= s.Length && s.Substring(i, 4) = "true" -> JsonBool true, i + 4
    | 'f' when i + 5 <= s.Length && s.Substring(i, 5) = "false" -> JsonBool false, i + 5
    | 'n' when i + 4 <= s.Length && s.Substring(i, 4) = "null" -> JsonNull, i + 4
    | _ -> let n, ni = parseJsonNumber s i in JsonNumber n, ni

let parseJson (s: string) : JsonValue = let v, _ = parseJsonValue s 0 in v

type FieldLookup<'v> =
    | Missing
    | WrongType of string * string
    | Present of 'v

let private lookupString (fields: (string * JsonValue) list) (name: string) : FieldLookup<string> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some(_, JsonString s) -> Present s
    | Some(_, v) -> WrongType("string", sprintf "%A" v)

let private lookupStringList (fields: (string * JsonValue) list) (name: string) : FieldLookup<string list> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some(_, JsonArray items) ->
        let strs =
            items
            |> List.choose (function
                | JsonString s -> Some s
                | _ -> None)

        if strs.Length <> items.Length then
            WrongType("string[]", "mixed")
        else
            Present strs
    | Some(_, v) -> WrongType("array", sprintf "%A" v)

let private lookupInt (fields: (string * JsonValue) list) (name: string) : FieldLookup<int> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Missing
    | Some(_, JsonNumber n) ->
        let d = decimal n

        if d <> Math.Floor(d) || d < decimal Int32.MinValue || d > decimal Int32.MaxValue then
            WrongType("int", "range")
        else
            Present(int d)
    | Some(_, v) -> WrongType("int", sprintf "%A" v)

type ParseError =
    | MalformedJson of string
    | MissingField of string
    | WrongFieldType of string * string * string
    | UnknownSchemaVersion of string
    | UnknownCandidateStatus of string
    | UnknownCandidateKind of string
    | UnknownEvidenceStrength of string
    | InvalidCandidateId of string
    | DuplicateInList of string
    | UnsortedList of string

let sha256OfBytes (bytes: byte array) : string =
    use h = SHA256.Create()

    h.ComputeHash(bytes)
    |> Array.map (fun b -> b.ToString("x2", CultureInfo.InvariantCulture))
    |> String.concat ""

let computeCandidateId
    (schemaVersion: string)
    (kind: RuleCandidateKind)
    (evidenceStrength: EvidenceStrength)
    (title: string)
    (symptom: string)
    (applicability: string)
    (observation: string)
    (proposedRepair: string)
    (limitations: string list)
    (primaryPath: string)
    (diagnosticCodes: string list)
    (diagnosticCount: int)
    (earliestLine: int option)
    (changedPaths: string list)
    (episodeId: string)
    (episodeKey: string)
    (changeSetId: string)
    (verificationEvidenceIds: string list)
    (transitionIds: string list)
    (beforeCommitOid: string)
    (beforeTreeOid: string)
    (afterCommitOid: string)
    (afterTreeOid: string)
    : string =
    let sb = StringBuilder()

    let add (x: string) =
        let bytes = Encoding.UTF8.GetBytes x
        sb.Append(BitConverter.GetBytes(bytes.Length)) |> ignore
        sb.Append(bytes) |> ignore

    add schemaVersion
    add (ruleCandidateKindToken kind)
    add (evidenceStrengthToken evidenceStrength)
    add title
    add symptom
    add applicability
    add observation
    add proposedRepair

    for lim in limitations |> List.sort do
        add lim

    add primaryPath

    for code in diagnosticCodes |> List.sort do
        add code

    add (string diagnosticCount)

    match earliestLine with
    | Some l -> add (string l)
    | None -> add ""

    for path in changedPaths |> List.sort do
        add path

    add episodeId
    add episodeKey
    add changeSetId

    for evid in verificationEvidenceIds |> List.sort do
        add evid

    for tid in transitionIds |> List.sort do
        add tid

    add beforeCommitOid
    add beforeTreeOid
    add afterCommitOid
    add afterTreeOid
    sha256OfBytes (sb.ToString() |> Encoding.UTF8.GetBytes)

let private utf8NoBom = UTF8Encoding(false)

let private esc (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore

    for c in s do
        match c with
        | '\\' -> sb.Append "\\\\" |> ignore
        | '"' -> sb.Append "\\\"" |> ignore
        | '\n' -> sb.Append "\\n" |> ignore
        | '\r' -> sb.Append "\\r" |> ignore
        | '\t' -> sb.Append "\\t" |> ignore
        | c when int c < 0x20 -> sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        | _ -> sb.Append c |> ignore

    sb.Append '"' |> ignore
    sb.ToString()

let private js (v: string) = esc v

let private jn (v: int) =
    v.ToString(CultureInfo.InvariantCulture)

let private ja (xs: string list) =
    "[" + (xs |> List.map esc |> String.concat ",") + "]"

let renderRuleCandidateEvidence (e: RuleCandidateEvidence) : string =
    "{\"episode_id\":"
    + js e.EpisodeId
    + ",\"episode_key\":"
    + js e.EpisodeKey
    + ",\"change_set_id\":"
    + js e.ChangeSetId
    + ",\"verification_evidence_ids\":"
    + ja e.VerificationEvidenceIds
    + ",\"transition_ids\":"
    + ja e.TransitionIds
    + ",\"before_commit_oid\":"
    + js e.BeforeCommitOid
    + ",\"before_tree_oid\":"
    + js e.BeforeTreeOid
    + ",\"after_commit_oid\":"
    + js e.AfterCommitOid
    + ",\"after_tree_oid\":"
    + js e.AfterTreeOid
    + "}"

let renderRuleCandidate (c: RuleCandidate) : string =
    "{\"schema_version\":"
    + js c.SchemaVersion
    + ",\"candidate_id\":"
    + js c.CandidateId
    + ",\"status\":"
    + js (ruleCandidateStatusToken c.Status)
    + ",\"kind\":"
    + js (ruleCandidateKindToken c.Kind)
    + ",\"evidence_strength\":"
    + js (evidenceStrengthToken c.EvidenceStrength)
    + ",\"title\":"
    + js c.Title
    + ",\"symptom\":"
    + js c.Symptom
    + ",\"applicability\":"
    + js c.Applicability
    + ",\"observation\":"
    + js c.Observation
    + ",\"proposed_repair\":"
    + js c.ProposedRepair
    + ",\"limitations\":"
    + ja c.Limitations
    + ",\"primary_path\":"
    + js c.PrimaryPath
    + ",\"diagnostic_codes\":"
    + ja c.DiagnosticCodes
    + ",\"diagnostic_count\":"
    + jn c.DiagnosticCount
    + ",\"earliest_line\":"
    + (match c.EarliestLine with
       | Some l -> jn l
       | None -> "null")
    + ",\"changed_paths\":"
    + ja c.ChangedPaths
    + ",\"evidence\":"
    + renderRuleCandidateEvidence c.Evidence
    + "}"

let renderRuleCandidateSummary (s: RuleCandidateSummary) : string =
    "{\"schema_version\":"
    + js s.SchemaVersion
    + ",\"eligible_episodes\":"
    + jn s.EligibleEpisodes
    + ",\"episodes_with_candidates\":"
    + jn s.EpisodesWithCandidates
    + ",\"candidates_total\":"
    + jn s.CandidatesTotal
    + ",\"parser_cascade_candidates\":"
    + jn s.ParserCascadeCandidates
    + ",\"single_episode_candidates\":"
    + jn s.SingleEpisodeCandidates
    + ",\"candidate_ids\":"
    + ja s.CandidateIds
    + "}"

let writeLineOriented (path: string) (text: string) : unit =
    let dir = Path.GetDirectoryName path

    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    let body = if text.EndsWith "\n" then text else text + "\n"
    File.WriteAllText(path, body, utf8NoBom)

let writeAllLines (path: string) (lines: string list) : unit =
    let dir = Path.GetDirectoryName path

    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore

    File.WriteAllText(path, (lines |> String.concat "\n") + "\n", utf8NoBom)


let parseRuleCandidateStrict (json: string) : Result<RuleCandidate, ParseError> =
    try
        match parseJson json with
        | JsonObject fields ->
            let req n =
                match lookupString fields n with
                | Present v -> Ok v
                | Missing -> Error(MissingField n)
                | WrongType(e, a) -> Error(WrongFieldType(n, e, a))

            let reqList n =
                match lookupStringList fields n with
                | Present v -> Ok v
                | Missing -> Error(MissingField n)
                | WrongType(e, a) -> Error(WrongFieldType(n, e, a))

            let reqInt n =
                match lookupInt fields n with
                | Present v -> Ok v
                | Missing -> Error(MissingField n)
                | WrongType(e, a) -> Error(WrongFieldType(n, e, a))

            match lookupString fields "schema_version" with
            | Present v when v <> RuleCandidateSchemaVersion -> Error(UnknownSchemaVersion v)
            | Present _ ->
                match req "candidate_id" with
                | Error e -> Error e
                | Ok cid when cid.Length <> 64 -> Error(InvalidCandidateId cid)
                | Ok cid ->
                    match lookupString fields "status" with
                    | Present s ->
                        match tryParseRuleCandidateStatus s with
                        | None -> Error(UnknownCandidateStatus s)
                        | Some status ->
                            match lookupString fields "kind" with
                            | Present k ->
                                match tryParseRuleCandidateKind k with
                                | None -> Error(UnknownCandidateKind k)
                                | Some kind ->
                                    match lookupString fields "evidence_strength" with
                                    | Present es ->
                                        match tryParseEvidenceStrength es with
                                        | None -> Error(UnknownEvidenceStrength es)
                                        | Some strength ->
                                            match reqList "limitations" with
                                            | Error e -> Error e
                                            | Ok limitations ->
                                                match reqList "diagnostic_codes" with
                                                | Error e -> Error e
                                                | Ok codes ->
                                                    match reqList "changed_paths" with
                                                    | Error e -> Error e
                                                    | Ok paths ->
                                                        match reqList "verification_evidence_ids" with
                                                        | Error e -> Error e
                                                        | Ok evidIds ->
                                                            match reqList "transition_ids" with
                                                            | Error e -> Error e
                                                            | Ok transIds ->
                                                                match req "title" with
                                                                | Error e -> Error e
                                                                | Ok title ->
                                                                    match req "symptom" with
                                                                    | Error e -> Error e
                                                                    | Ok symptom ->
                                                                        match req "applicability" with
                                                                        | Error e -> Error e
                                                                        | Ok applicability ->
                                                                            match req "observation" with
                                                                            | Error e -> Error e
                                                                            | Ok observation ->
                                                                                match req "proposed_repair" with
                                                                                | Error e -> Error e
                                                                                | Ok proposedRepair ->
                                                                                    match req "primary_path" with
                                                                                    | Error e -> Error e
                                                                                    | Ok primaryPath ->
                                                                                        match
                                                                                            reqInt "diagnostic_count"
                                                                                        with
                                                                                        | Error e -> Error e
                                                                                        | Ok diagnosticCount ->
                                                                                            let earliestLine =
                                                                                                match
                                                                                                    lookupInt
                                                                                                        fields
                                                                                                        "earliest_line"
                                                                                                with
                                                                                                | Present v -> Some v
                                                                                                | _ -> None

                                                                                            match
                                                                                                List.tryFind
                                                                                                    (fun (k, _) ->
                                                                                                        k = "evidence")
                                                                                                    fields
                                                                                            with
                                                                                            | Some(_,
                                                                                                   JsonObject evidFields) ->
                                                                                                let reqE n =
                                                                                                    match
                                                                                                        lookupString
                                                                                                            evidFields
                                                                                                            n
                                                                                                    with
                                                                                                    | Present v -> Ok v
                                                                                                    | Missing ->
                                                                                                        Error(
                                                                                                            MissingField(
                                                                                                                "evidence."
                                                                                                                + n
                                                                                                            )
                                                                                                        )
                                                                                                    | WrongType(e, a) ->
                                                                                                        Error(
                                                                                                            WrongFieldType(
                                                                                                                "evidence."
                                                                                                                + n,
                                                                                                                e,
                                                                                                                a
                                                                                                            )
                                                                                                        )

                                                                                                let reqEList n =
                                                                                                    match
                                                                                                        lookupStringList
                                                                                                            evidFields
                                                                                                            n
                                                                                                    with
                                                                                                    | Present v -> Ok v
                                                                                                    | Missing ->
                                                                                                        Error(
                                                                                                            MissingField(
                                                                                                                "evidence."
                                                                                                                + n
                                                                                                            )
                                                                                                        )
                                                                                                    | WrongType(e, a) ->
                                                                                                        Error(
                                                                                                            WrongFieldType(
                                                                                                                "evidence."
                                                                                                                + n,
                                                                                                                e,
                                                                                                                a
                                                                                                            )
                                                                                                        )

                                                                                                match
                                                                                                    reqE "episode_id"
                                                                                                with
                                                                                                | Error e -> Error e
                                                                                                | Ok episodeId ->
                                                                                                    match
                                                                                                        reqE
                                                                                                            "episode_key"
                                                                                                    with
                                                                                                    | Error e -> Error e
                                                                                                    | Ok episodeKey ->
                                                                                                        match
                                                                                                            reqE
                                                                                                                "change_set_id"
                                                                                                        with
                                                                                                        | Error e ->
                                                                                                            Error e
                                                                                                        | Ok changeSetId ->
                                                                                                            match
                                                                                                                reqEList
                                                                                                                    "verification_evidence_ids"
                                                                                                            with
                                                                                                            | Error e ->
                                                                                                                Error e
                                                                                                            | Ok evidList ->
                                                                                                                match
                                                                                                                    reqEList
                                                                                                                        "transition_ids"
                                                                                                                with
                                                                                                                | Error e ->
                                                                                                                    Error
                                                                                                                        e
                                                                                                                | Ok transList ->
                                                                                                                    match
                                                                                                                        reqE
                                                                                                                            "before_commit_oid"
                                                                                                                    with
                                                                                                                    | Error e ->
                                                                                                                        Error
                                                                                                                            e
                                                                                                                    | Ok beforeCommitOid ->
                                                                                                                        match
                                                                                                                            reqE
                                                                                                                                "before_tree_oid"
                                                                                                                        with
                                                                                                                        | Error e ->
                                                                                                                            Error
                                                                                                                                e
                                                                                                                        | Ok beforeTreeOid ->
                                                                                                                            match
                                                                                                                                reqE
                                                                                                                                    "after_commit_oid"
                                                                                                                            with
                                                                                                                            | Error e ->
                                                                                                                                Error
                                                                                                                                    e
                                                                                                                            | Ok afterCommitOid ->
                                                                                                                                match
                                                                                                                                    reqE
                                                                                                                                        "after_tree_oid"
                                                                                                                                with
                                                                                                                                | Error e ->
                                                                                                                                    Error
                                                                                                                                        e
                                                                                                                                | Ok afterTreeOid ->
                                                                                                                                    Ok
                                                                                                                                        { SchemaVersion =
                                                                                                                                            RuleCandidateSchemaVersion
                                                                                                                                          CandidateId =
                                                                                                                                            cid
                                                                                                                                          Status =
                                                                                                                                            status
                                                                                                                                          Kind =
                                                                                                                                            kind
                                                                                                                                          EvidenceStrength =
                                                                                                                                            strength
                                                                                                                                          Title =
                                                                                                                                            title
                                                                                                                                          Symptom =
                                                                                                                                            symptom
                                                                                                                                          Applicability =
                                                                                                                                            applicability
                                                                                                                                          Observation =
                                                                                                                                            observation
                                                                                                                                          ProposedRepair =
                                                                                                                                            proposedRepair
                                                                                                                                          Limitations =
                                                                                                                                            limitations
                                                                                                                                          PrimaryPath =
                                                                                                                                            primaryPath
                                                                                                                                          DiagnosticCodes =
                                                                                                                                            codes
                                                                                                                                          DiagnosticCount =
                                                                                                                                            diagnosticCount
                                                                                                                                          EarliestLine =
                                                                                                                                            earliestLine
                                                                                                                                          ChangedPaths =
                                                                                                                                            paths
                                                                                                                                          Evidence =
                                                                                                                                            { EpisodeId =
                                                                                                                                                episodeId
                                                                                                                                              EpisodeKey =
                                                                                                                                                episodeKey
                                                                                                                                              ChangeSetId =
                                                                                                                                                changeSetId
                                                                                                                                              VerificationEvidenceIds =
                                                                                                                                                evidList
                                                                                                                                              TransitionIds =
                                                                                                                                                transList
                                                                                                                                              BeforeCommitOid =
                                                                                                                                                beforeCommitOid
                                                                                                                                              BeforeTreeOid =
                                                                                                                                                beforeTreeOid
                                                                                                                                              AfterCommitOid =
                                                                                                                                                afterCommitOid
                                                                                                                                              AfterTreeOid =
                                                                                                                                                afterTreeOid } }
                                                                                            | _ ->
                                                                                                Error(
                                                                                                    MissingField
                                                                                                        "evidence"
                                                                                                )
                                    | Missing -> Error(MissingField "evidence_strength")
                                    | WrongType(e, a) -> Error(WrongFieldType("evidence_strength", e, a))
                            | Missing -> Error(MissingField "kind")
                            | WrongType(e, a) -> Error(WrongFieldType("kind", e, a))
                    | Missing -> Error(MissingField "status")
                    | WrongType(e, a) -> Error(WrongFieldType("status", e, a))
            | Missing -> Error(MissingField "schema_version")
            | WrongType(e, a) -> Error(WrongFieldType("schema_version", e, a))
        | _ -> Error(MalformedJson "Expected JSON object")
    with ex ->
        Error(MalformedJson ex.Message)

let parseRuleCandidateSummaryStrict (json: string) : Result<RuleCandidateSummary, ParseError> =
    try
        match parseJson json with
        | JsonObject fields ->
            let reqInt n =
                match lookupInt fields n with
                | Present v -> Ok v
                | Missing -> Error(MissingField n)
                | WrongType(e, a) -> Error(WrongFieldType(n, e, a))

            match lookupString fields "schema_version" with
            | Present v when v <> RuleCandidateSummarySchemaVersion -> Error(UnknownSchemaVersion v)
            | Present _ ->
                match reqInt "eligible_episodes" with
                | Error e -> Error e
                | Ok elEp ->
                    match reqInt "episodes_with_candidates" with
                    | Error e -> Error e
                    | Ok epWC ->
                        match reqInt "candidates_total" with
                        | Error e -> Error e
                        | Ok cTot ->
                            match reqInt "parser_cascade_candidates" with
                            | Error e -> Error e
                            | Ok pcCands ->
                                match reqInt "single_episode_candidates" with
                                | Error e -> Error e
                                | Ok seCands ->
                                    match lookupStringList fields "candidate_ids" with
                                    | Present ids ->
                                        if ids <> List.sort ids then
                                            Error(UnsortedList "candidate_ids")
                                        elif ids <> List.distinct ids then
                                            Error(DuplicateInList "candidate_ids")
                                        else
                                            Ok
                                                { SchemaVersion = RuleCandidateSummarySchemaVersion
                                                  EligibleEpisodes = elEp
                                                  EpisodesWithCandidates = epWC
                                                  CandidatesTotal = cTot
                                                  ParserCascadeCandidates = pcCands
                                                  SingleEpisodeCandidates = seCands
                                                  CandidateIds = ids }
                                    | Missing -> Error(MissingField "candidate_ids")
                                    | WrongType(e, a) -> Error(WrongFieldType("candidate_ids", e, a))
            | Missing -> Error(MissingField "schema_version")
            | WrongType(e, a) -> Error(WrongFieldType("schema_version", e, a))
        | _ -> Error(MalformedJson "Expected JSON object")
    with ex ->
        Error(MalformedJson ex.Message)
