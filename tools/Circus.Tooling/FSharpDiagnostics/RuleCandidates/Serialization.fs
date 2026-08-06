module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Serialization

// =============================================================================
// Rule candidate serialization
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// The published artifacts are:
//   * `rule-candidates-v2.jsonl` - one JSON object per candidate.
//   * `rule-candidate-summary-v2.json` - aggregate counts.
//
// The candidate record is observation only.  No imperative repair text is
// emitted.  Curation flags (`causal_family_curated`,
// `repair_advice_available`, `llm_tip_available`) are always false.

open System
open System.Globalization
open System.IO
open System.Security.Cryptography
open System.Text
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain

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
    | StatusFlagMustBeFalse of string

// -----------------------------------------------------------------------------
// Field lookups
// -----------------------------------------------------------------------------

let private lookupString (fields: (string * JsonValue) list) (name: string) : Result<string, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Error(MissingField name)
    | Some(_, JsonString s) -> Ok s
    | Some(_, v) -> Error(WrongFieldType(name, "string", sprintf "%A" v))

let private lookupStringList (fields: (string * JsonValue) list) (name: string) : Result<string list, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Error(MissingField name)
    | Some(_, JsonArray items) ->
        let strs =
            items
            |> List.choose (function
                | JsonString s -> Some s
                | _ -> None)

        if strs.Length <> items.Length then
            Error(WrongFieldType(name, "string[]", "mixed"))
        else
            Ok strs
    | Some(_, v) -> Error(WrongFieldType(name, "array", sprintf "%A" v))

let private lookupInt (fields: (string * JsonValue) list) (name: string) : Result<int, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Error(MissingField name)
    | Some(_, JsonNumber n) ->
        let d = decimal n

        if d <> Math.Floor(d) || d < decimal Int32.MinValue || d > decimal Int32.MaxValue then
            Error(WrongFieldType(name, "int", "range"))
        else
            Ok(int d)
    | Some(_, v) -> Error(WrongFieldType(name, "int", sprintf "%A" v))

let private lookupIntOption (fields: (string * JsonValue) list) (name: string) : Result<int option, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Ok None
    | Some(_, JsonNull) -> Ok None
    | Some(_, JsonNumber n) ->
        let d = decimal n

        if d <> Math.Floor(d) || d < decimal Int32.MinValue || d > decimal Int32.MaxValue then
            Error(WrongFieldType(name, "int", "range"))
        else
            Ok(Some(int d))
    | Some(_, v) -> Error(WrongFieldType(name, "int", sprintf "%A" v))

let private lookupBool (fields: (string * JsonValue) list) (name: string) : Result<bool, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Error(MissingField name)
    | Some(_, JsonBool b) -> Ok b
    | Some(_, v) -> Error(WrongFieldType(name, "bool", sprintf "%A" v))

let private lookupObject (fields: (string * JsonValue) list) (name: string) : Result<(string * JsonValue) list, ParseError> =
    match List.tryFind (fun (k, _) -> k = name) fields with
    | None -> Error(MissingField name)
    | Some(_, JsonObject items) -> Ok items
    | Some(_, v) -> Error(WrongFieldType(name, "object", sprintf "%A" v))

// -----------------------------------------------------------------------------
// Result builder for clean parsing pipelines
// -----------------------------------------------------------------------------

type ParseResultBuilder() =
    member _.Bind(r, f) = Result.bind f r
    member _.Return(x) = Ok x
    member _.ReturnFrom(r) = r
    member _.Zero() = Ok ()
    member _.Combine(a, b) =
        match a with
        | Ok () -> b
        | Error e -> Error e
    member _.Delay(f) = f ()

let parse = ParseResultBuilder()

// -----------------------------------------------------------------------------
// Helpers
// -----------------------------------------------------------------------------

let sha256OfBytes (bytes: byte array) : string =
    use h = SHA256.Create()

    h.ComputeHash(bytes)
    |> Array.map (fun b -> b.ToString("x2", CultureInfo.InvariantCulture))
    |> String.concat ""

// -----------------------------------------------------------------------------
// Deterministic candidate identity
// -----------------------------------------------------------------------------

/// Compute the deterministic SHA-256 candidate identity from canonical
/// identity-bearing fields.  Presentation text that is not declared identity
/// bearing is excluded; transitions, evidence IDs, and OIDs are sorted before
/// encoding so order does not affect the ID.
///
/// Identity-bearing fields (in encoding order):
///   schema_version, kind, evidence_strength, title, symptom,
///   applicability_conditions, observation, candidate_hypothesis,
///   sorted(limitations), primary_path, sorted(diagnostic_codes),
///   diagnostic_count, earliest_line (or empty), sorted(changed_paths),
///   episode_id, episode_key, change_set_id,
///   sorted(verification_evidence_ids), sorted(supporting_transition_ids),
///   sorted(context_transition_ids), sorted(counterevidence_transition_ids),
///   before_commit_oid, before_tree_oid, after_commit_oid, after_tree_oid
let computeCandidateId
    (schemaVersion: string)
    (kind: RuleCandidateKind)
    (evidenceStrength: EvidenceStrength)
    (title: string)
    (symptom: string)
    (applicability: string)
    (observation: string)
    (candidateHypothesis: string)
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
    (supportingTransitionIds: string list)
    (contextTransitionIds: string list)
    (counterevidenceTransitionIds: string list)
    (beforeCommitOid: string)
    (beforeTreeOid: string)
    (afterCommitOid: string)
    (afterTreeOid: string)
    : string =
    let buffer = ResizeArray<byte>()

    let add (x: string) =
        let bytes = Encoding.UTF8.GetBytes x
        // Big-endian length prefix
        buffer.Add(byte (bytes.Length >>> 24 &&& 0xFF))
        buffer.Add(byte (bytes.Length >>> 16 &&& 0xFF))
        buffer.Add(byte (bytes.Length >>> 8 &&& 0xFF))
        buffer.Add(byte (bytes.Length &&& 0xFF))
        buffer.AddRange(bytes)

    add schemaVersion
    add (ruleCandidateKindToken kind)
    add (evidenceStrengthToken evidenceStrength)
    add title
    add symptom
    add applicability
    add observation
    add candidateHypothesis

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

    for tid in supportingTransitionIds |> List.sort do
        add tid

    for tid in contextTransitionIds |> List.sort do
        add tid

    for tid in counterevidenceTransitionIds |> List.sort do
        add tid

    add beforeCommitOid
    add beforeTreeOid
    add afterCommitOid
    add afterTreeOid
    sha256OfBytes (buffer.ToArray())

// -----------------------------------------------------------------------------
// JSON rendering
// -----------------------------------------------------------------------------

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

let private jb (v: bool) = if v then "true" else "false"

let renderRuleCandidateEvidence (e: RuleCandidateEvidence) : string =
    "{\"episode_id\":"
    + js e.EpisodeId
    + ",\"episode_key\":"
    + js e.EpisodeKey
    + ",\"change_set_id\":"
    + js e.ChangeSetId
    + ",\"verification_evidence_ids\":"
    + ja e.VerificationEvidenceIds
    + ",\"before_commit_oid\":"
    + js e.BeforeCommitOid
    + ",\"before_tree_oid\":"
    + js e.BeforeTreeOid
    + ",\"after_commit_oid\":"
    + js e.AfterCommitOid
    + ",\"after_tree_oid\":"
    + js e.AfterTreeOid
    + "}"

let renderTransitionPartition (p: RuleCandidateTransitionPartition) : string =
    "{\"supporting_transition_ids\":"
    + ja p.SupportingTransitionIds
    + ",\"context_transition_ids\":"
    + ja p.ContextTransitionIds
    + ",\"counterevidence_transition_ids\":"
    + ja p.CounterevidenceTransitionIds
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
    + ",\"applicability_conditions\":"
    + js c.ApplicabilityConditions
    + ",\"observation\":"
    + js c.Observation
    + ",\"candidate_hypothesis\":"
    + js c.CandidateHypothesis
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
    + ",\"causal_family_curated\":" + jb c.StatusFlags.CausalFamilyCurated
    + ",\"repair_advice_available\":" + jb c.StatusFlags.RepairAdviceAvailable
    + ",\"llm_tip_available\":" + jb c.StatusFlags.LlmTipAvailable
    + ",\"transition_partition\":"
    + renderTransitionPartition c.TransitionPartition
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

// -----------------------------------------------------------------------------
// Strict parsing
// -----------------------------------------------------------------------------

let private parseEvidence (fields: (string * JsonValue) list) : Result<RuleCandidateEvidence, ParseError> =
    parse {
        let! episodeId = lookupString fields "episode_id"
        let! episodeKey = lookupString fields "episode_key"
        let! changeSetId = lookupString fields "change_set_id"
        let! verificationEvidenceIds = lookupStringList fields "verification_evidence_ids"
        let! beforeCommitOid = lookupString fields "before_commit_oid"
        let! beforeTreeOid = lookupString fields "before_tree_oid"
        let! afterCommitOid = lookupString fields "after_commit_oid"
        let! afterTreeOid = lookupString fields "after_tree_oid"

        return
            { EpisodeId = episodeId
              EpisodeKey = episodeKey
              ChangeSetId = changeSetId
              VerificationEvidenceIds = verificationEvidenceIds
              BeforeCommitOid = beforeCommitOid
              BeforeTreeOid = beforeTreeOid
              AfterCommitOid = afterCommitOid
              AfterTreeOid = afterTreeOid }
    }

let private parseTransitionPartition (fields: (string * JsonValue) list) : Result<RuleCandidateTransitionPartition, ParseError> =
    parse {
        let! supporting = lookupStringList fields "supporting_transition_ids"
        let! context = lookupStringList fields "context_transition_ids"
        let! counterevidence = lookupStringList fields "counterevidence_transition_ids"

        return
            { SupportingTransitionIds = supporting
              ContextTransitionIds = context
              CounterevidenceTransitionIds = counterevidence }
    }

let private parseRuleCandidateFromObject (fields: (string * JsonValue) list) : Result<RuleCandidate, ParseError> =
    parse {
        let! sv = lookupString fields "schema_version"
        do!
            if sv <> RuleCandidateSchemaVersion then
                Error(UnknownSchemaVersion sv)
            else
                Ok()

        let! cid = lookupString fields "candidate_id"
        do!
            if cid.Length <> 64 then
                Error(InvalidCandidateId cid)
            else
                Ok()

        let! statusToken = lookupString fields "status"
        let! status =
            match tryParseRuleCandidateStatus statusToken with
            | None -> Error(UnknownCandidateStatus statusToken)
            | Some s -> Ok s

        let! kindToken = lookupString fields "kind"
        let! kind =
            match tryParseRuleCandidateKind kindToken with
            | None -> Error(UnknownCandidateKind kindToken)
            | Some k -> Ok k

        let! esToken = lookupString fields "evidence_strength"
        let! strength =
            match tryParseEvidenceStrength esToken with
            | None -> Error(UnknownEvidenceStrength esToken)
            | Some e -> Ok e

        let! limitations = lookupStringList fields "limitations"
        let! diagnosticCodes = lookupStringList fields "diagnostic_codes"
        let! changedPaths = lookupStringList fields "changed_paths"
        let! title = lookupString fields "title"
        let! symptom = lookupString fields "symptom"
        let! applicability = lookupString fields "applicability_conditions"
        let! observation = lookupString fields "observation"
        let! candidateHypothesis = lookupString fields "candidate_hypothesis"
        let! primaryPath = lookupString fields "primary_path"
        let! diagnosticCount = lookupInt fields "diagnostic_count"
        let! earliestLine = lookupIntOption fields "earliest_line"

        let! causalFamilyCurated = lookupBool fields "causal_family_curated"
        do!
            if causalFamilyCurated then
                Error(StatusFlagMustBeFalse "causal_family_curated")
            else
                Ok()

        let! repairAdviceAvailable = lookupBool fields "repair_advice_available"
        do!
            if repairAdviceAvailable then
                Error(StatusFlagMustBeFalse "repair_advice_available")
            else
                Ok()

        let! llmTipAvailable = lookupBool fields "llm_tip_available"
        do!
            if llmTipAvailable then
                Error(StatusFlagMustBeFalse "llm_tip_available")
            else
                Ok()

        let! partitionFields = lookupObject fields "transition_partition"
        let! partition = parseTransitionPartition partitionFields

        let! evidenceFields = lookupObject fields "evidence"
        let! evidence = parseEvidence evidenceFields

        return
            { SchemaVersion = RuleCandidateSchemaVersion
              CandidateId = cid
              Status = status
              Kind = kind
              EvidenceStrength = strength
              Title = title
              Symptom = symptom
              ApplicabilityConditions = applicability
              Observation = observation
              CandidateHypothesis = candidateHypothesis
              Limitations = limitations
              PrimaryPath = primaryPath
              DiagnosticCodes = diagnosticCodes
              DiagnosticCount = diagnosticCount
              EarliestLine = earliestLine
              ChangedPaths = changedPaths
              StatusFlags = defaultCandidateStatusFlags
              TransitionPartition = partition
              Evidence = evidence }
    }

let parseRuleCandidateStrict (json: string) : Result<RuleCandidate, ParseError> =
    try
        match parseJson json with
        | JsonObject fields -> parseRuleCandidateFromObject fields
        | _ -> Error(MalformedJson "Expected JSON object")
    with ex ->
        Error(MalformedJson ex.Message)

let parseRuleCandidateSummaryStrict (json: string) : Result<RuleCandidateSummary, ParseError> =
    try
        match parseJson json with
        | JsonObject fields ->
            parse {
                let! sv = lookupString fields "schema_version"
                do!
                    if sv <> RuleCandidateSummarySchemaVersion then
                        Error(UnknownSchemaVersion sv)
                    else
                        Ok()

                let! a = lookupInt fields "eligible_episodes"
                let! b = lookupInt fields "episodes_with_candidates"
                let! c = lookupInt fields "candidates_total"
                let! d = lookupInt fields "parser_cascade_candidates"
                let! e = lookupInt fields "single_episode_candidates"
                let! ids = lookupStringList fields "candidate_ids"

                do!
                    if ids <> List.sort ids then
                        Error(UnsortedList "candidate_ids")
                    elif ids <> List.distinct ids then
                        Error(DuplicateInList "candidate_ids")
                    else
                        Ok()

                return
                    { SchemaVersion = RuleCandidateSummarySchemaVersion
                      EligibleEpisodes = a
                      EpisodesWithCandidates = b
                      CandidatesTotal = c
                      ParserCascadeCandidates = d
                      SingleEpisodeCandidates = e
                      CandidateIds = ids }
            }
        | _ -> Error(MalformedJson "Expected JSON object")
    with ex ->
        Error(MalformedJson ex.Message)
