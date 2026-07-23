module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Serialization

open System.Globalization
open System.IO
open System.Text
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain

// =============================================================================
// Deterministic JSON rendering
// =============================================================================
//
// Rules (inherited from the foundation contract):
//   * UTF-8 without BOM
//   * LF line endings
//   * Stable JSON property order (manually constructed)
//   * Ordinal string comparison for sort keys
//   * No current timestamps, no random IDs, no machine absolute paths.

let private escapeJsonString (s: string) : string =
    let sb = StringBuilder(s.Length + 2)
    sb.Append '"' |> ignore
    for c in s do
        if c = '\\' then sb.Append("\\\\") |> ignore
        elif c = '"' then sb.Append("\\\"") |> ignore
        elif c = '\n' then sb.Append("\\n") |> ignore
        elif c = '\r' then sb.Append("\\r") |> ignore
        elif c = '\t' then sb.Append("\\t") |> ignore
        elif c = '\b' then sb.Append("\\b") |> ignore
        elif c = '\x0c' then sb.Append("\\f") |> ignore
        elif int c < 0x20 then
            sb.AppendFormat(CultureInfo.InvariantCulture, "\\u{0:x4}", int c) |> ignore
        elif c = '\uFEFF' then
            sb.Append("\\ufeff") |> ignore
        else sb.Append c |> ignore
    sb.Append '"' |> ignore
    sb.ToString()

let private str (v: string) : string = escapeJsonString v
let private optStr (v: string option) : string =
    match v with
    | None -> "null"
    | Some s -> escapeJsonString s
let private intStr (v: int) : string =
    v.ToString(CultureInfo.InvariantCulture)
let private strList (xs: string list) : string =
    "[" + (xs |> List.map escapeJsonString |> String.concat ",") + "]"

let private renderCompatibility (c: Compatibility) : string =
    "{\"status\":"
    + str (compatibilityStatusToken c.Status)
    + ",\"reasons\":"
    + strList c.Reasons
    + ",\"missing_fields\":"
    + strList c.MissingFields
    + "}"

let private renderTransitionCounts (tc: TransitionCounts) : string =
    "{\"persisted_same_count\":"
    + intStr tc.PersistedSameCount
    + ",\"persisted_count_decreased\":"
    + intStr tc.PersistedCountDecreased
    + ",\"persisted_count_increased\":"
    + intStr tc.PersistedCountIncreased
    + ",\"eliminated_after\":"
    + intStr tc.EliminatedAfter
    + ",\"introduced_after\":"
    + intStr tc.IntroducedAfter
    + ",\"resolution_candidates\":"
    + intStr tc.ResolutionCandidates
    + ",\"regression_candidates\":"
    + intStr tc.RegressionCandidates
    + ",\"unassessable\":"
    + intStr tc.Unassessable
    + "}"

let private renderQualification (q: EpisodeQualification) : string =
    "{\"status\":"
    + str (episodeQualificationStatusToken q.Status)
    + ",\"reasons\":"
    + strList q.Reasons
    + "}"

let renderRepairEpisode (e: RepairEpisode) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(str e.SchemaVersion) |> ignore
    sb.Append ",\"episode_id\":" |> ignore
    sb.Append(str e.EpisodeId) |> ignore
    sb.Append ",\"episode_key\":" |> ignore
    sb.Append(str e.EpisodeKey) |> ignore
    sb.Append ",\"before_capture_id\":" |> ignore
    sb.Append(str e.BeforeCaptureId) |> ignore
    sb.Append ",\"after_capture_id\":" |> ignore
    sb.Append(str e.AfterCaptureId) |> ignore
    sb.Append ",\"before_commit_oid\":" |> ignore
    sb.Append(str e.BeforeCommitOid) |> ignore
    sb.Append ",\"before_tree_oid\":" |> ignore
    sb.Append(str e.BeforeTreeOid) |> ignore
    sb.Append ",\"after_commit_oid\":" |> ignore
    sb.Append(str e.AfterCommitOid) |> ignore
    sb.Append ",\"after_tree_oid\":" |> ignore
    sb.Append(str e.AfterTreeOid) |> ignore
    sb.Append ",\"commit_range\":" |> ignore
    sb.Append(strList e.CommitRange) |> ignore
    sb.Append ",\"change_set_id\":" |> ignore
    sb.Append(str e.ChangeSetId) |> ignore
    sb.Append ",\"command_contract_before\":" |> ignore
    sb.Append(str e.CommandContractBefore) |> ignore
    sb.Append ",\"command_contract_after\":" |> ignore
    sb.Append(str e.CommandContractAfter) |> ignore
    sb.Append ",\"compatibility\":" |> ignore
    sb.Append(renderCompatibility e.Compatibility) |> ignore
    sb.Append ",\"transition_counts\":" |> ignore
    sb.Append(renderTransitionCounts e.TransitionCounts) |> ignore
    sb.Append ",\"verification_level\":" |> ignore
    sb.Append(str (verificationLevelToken e.VerificationLevel)) |> ignore
    sb.Append ",\"verification_evidence_ids\":" |> ignore
    sb.Append(strList e.VerificationEvidenceIds) |> ignore
    sb.Append ",\"qualification\":" |> ignore
    sb.Append(renderQualification e.Qualification) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

let private renderSpan (s: SourceSpan) : string =
    "{\"start_line\":"
    + optStr (s.StartLine |> Option.map string)
    + ",\"start_column\":"
    + optStr (s.StartColumn |> Option.map string)
    + ",\"end_line\":"
    + optStr (s.EndLine |> Option.map string)
    + ",\"end_column\":"
    + optStr (s.EndColumn |> Option.map string)
    + "}"

let private renderSourceLink (sl: SourceLink) : string =
    "{\"kind\":"
    + str (sourceLinkKindToken sl.Kind)
    + ",\"paths\":"
    + strList sl.Paths
    + ",\"reasons\":"
    + strList sl.Reasons
    + "}"

let renderDiagnosticTransition (t: DiagnosticTransition) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(str t.SchemaVersion) |> ignore
    sb.Append ",\"episode_id\":" |> ignore
    sb.Append(str t.EpisodeId) |> ignore
    sb.Append ",\"exact_fingerprint\":" |> ignore
    sb.Append(str t.ExactFingerprint) |> ignore
    sb.Append ",\"transition_kind\":" |> ignore
    sb.Append(str (exactTransitionKindToken t.TransitionKind)) |> ignore
    sb.Append ",\"before_occurrence_count\":" |> ignore
    sb.Append(intStr t.BeforeOccurrenceCount) |> ignore
    sb.Append ",\"after_occurrence_count\":" |> ignore
    sb.Append(intStr t.AfterOccurrenceCount) |> ignore
    sb.Append ",\"severity\":" |> ignore
    sb.Append(str (severityToken t.Severity)) |> ignore
    sb.Append ",\"code\":" |> ignore
    sb.Append(optStr t.Code) |> ignore
    sb.Append ",\"message_normalized\":" |> ignore
    sb.Append(str t.MessageNormalized) |> ignore
    sb.Append ",\"source_path\":" |> ignore
    sb.Append(optStr t.SourcePath) |> ignore
    sb.Append ",\"project_path\":" |> ignore
    sb.Append(optStr t.ProjectPath) |> ignore
    sb.Append ",\"span\":" |> ignore
    sb.Append(renderSpan t.Span) |> ignore
    sb.Append ",\"compatibility\":" |> ignore
    sb.Append(renderCompatibility t.Compatibility) |> ignore
    sb.Append ",\"source_link\":" |> ignore
    sb.Append(renderSourceLink t.SourceLink) |> ignore
    sb.Append ",\"assessment\":" |> ignore
    sb.Append(str (transitionAssessmentToken t.Assessment)) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

let renderGitChangeSet (cs: GitChangeSet) : string =
    let entriesBody =
        cs.Entries
        |> List.map (fun e ->
            "{\"before_mode\":"
            + str e.BeforeMode
            + ",\"after_mode\":"
            + str e.AfterMode
            + ",\"before_blob_oid\":"
            + optStr e.BeforeBlobOid
            + ",\"after_blob_oid\":"
            + optStr e.AfterBlobOid
            + ",\"change_kind\":"
            + str (gitChangeKindToken e.ChangeKind)
            + ",\"canonical_path\":"
            + str e.CanonicalPath
            + "}")
        |> String.concat ","
    "{\"schema_version\":"
    + str cs.SchemaVersion
    + ",\"change_set_id\":"
    + str cs.ChangeSetId
    + ",\"change_set_version\":"
    + str cs.ChangeSetVersion
    + ",\"before_tree_oid\":"
    + str cs.BeforeTreeOid
    + ",\"after_tree_oid\":"
    + str cs.AfterTreeOid
    + ",\"object_format\":"
    + str (gitObjectFormatToken cs.ObjectFormat)
    + ",\"entries\":["
    + entriesBody
    + "]}"

let renderVerificationEvidence (v: VerificationEvidence) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(str v.SchemaVersion) |> ignore
    sb.Append ",\"evidence_id\":" |> ignore
    sb.Append(str v.EvidenceId) |> ignore
    sb.Append ",\"episode_id\":" |> ignore
    sb.Append(str v.EpisodeId) |> ignore
    sb.Append ",\"kind\":" |> ignore
    sb.Append(str (verificationKindToken v.Kind)) |> ignore
    sb.Append ",\"command\":" |> ignore
    sb.Append(str v.Command) |> ignore
    sb.Append ",\"working_directory\":" |> ignore
    sb.Append(str v.WorkingDirectory) |> ignore
    sb.Append ",\"tested_commit_oid\":" |> ignore
    sb.Append(str v.TestedCommitOid) |> ignore
    sb.Append ",\"tested_tree_oid\":" |> ignore
    sb.Append(str v.TestedTreeOid) |> ignore
    sb.Append ",\"exit_code\":" |> ignore
    sb.Append(intStr v.ExitCode) |> ignore
    sb.Append ",\"stdout_sha256\":" |> ignore
    sb.Append(optStr v.StdoutSha256) |> ignore
    sb.Append ",\"stderr_sha256\":" |> ignore
    sb.Append(optStr v.StderrSha256) |> ignore
    sb.Append ",\"combined_log_path\":" |> ignore
    sb.Append(optStr v.CombinedLogPath) |> ignore
    sb.Append ",\"status\":" |> ignore
    sb.Append(str (verificationStatusToken v.Status)) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

let renderRepairEpisodeSummary (s: RepairEpisodeSummary) : string =
    let sb = StringBuilder()
    sb.Append "{\"schema_version\":" |> ignore
    sb.Append(str s.SchemaVersion) |> ignore
    sb.Append ",\"declarations_total\":" |> ignore
    sb.Append(intStr s.DeclarationsTotal) |> ignore
    sb.Append ",\"valid_declarations\":" |> ignore
    sb.Append(intStr s.ValidDeclarations) |> ignore
    sb.Append ",\"invalid_declarations\":" |> ignore
    sb.Append(intStr s.InvalidDeclarations) |> ignore
    sb.Append ",\"missing_captures\":" |> ignore
    sb.Append(intStr s.MissingCaptures) |> ignore
    sb.Append ",\"missing_git_objects\":" |> ignore
    sb.Append(intStr s.MissingGitObjects) |> ignore
    sb.Append ",\"duplicate_episode_keys\":" |> ignore
    sb.Append(intStr s.DuplicateEpisodeKeys) |> ignore
    sb.Append ",\"duplicate_episode_ids\":" |> ignore
    sb.Append(intStr s.DuplicateEpisodeIds) |> ignore
    sb.Append ",\"episodes_total\":" |> ignore
    sb.Append(intStr s.EpisodesTotal) |> ignore
    sb.Append ",\"episodes_qualified\":" |> ignore
    sb.Append(intStr s.EpisodesQualified) |> ignore
    sb.Append ",\"episodes_qualified_with_limitations\":" |> ignore
    sb.Append(intStr s.EpisodesQualifiedWithLimitations) |> ignore
    sb.Append ",\"episodes_ambiguous\":" |> ignore
    sb.Append(intStr s.EpisodesAmbiguous) |> ignore
    sb.Append ",\"episodes_rejected\":" |> ignore
    sb.Append(intStr s.EpisodesRejected) |> ignore
    sb.Append ",\"change_sets_total\":" |> ignore
    sb.Append(intStr s.ChangeSetsTotal) |> ignore
    sb.Append ",\"transitions_total\":" |> ignore
    sb.Append(intStr s.TransitionsTotal) |> ignore
    sb.Append ",\"persisted_same_count\":" |> ignore
    sb.Append(intStr s.PersistedSameCount) |> ignore
    sb.Append ",\"persisted_count_decreased\":" |> ignore
    sb.Append(intStr s.PersistedCountDecreased) |> ignore
    sb.Append ",\"persisted_count_increased\":" |> ignore
    sb.Append(intStr s.PersistedCountIncreased) |> ignore
    sb.Append ",\"eliminated_after\":" |> ignore
    sb.Append(intStr s.EliminatedAfter) |> ignore
    sb.Append ",\"introduced_after\":" |> ignore
    sb.Append(intStr s.IntroducedAfter) |> ignore
    sb.Append ",\"resolution_candidates\":" |> ignore
    sb.Append(intStr s.ResolutionCandidates) |> ignore
    sb.Append ",\"regression_candidates\":" |> ignore
    sb.Append(intStr s.RegressionCandidates) |> ignore
    sb.Append ",\"unassessable_transitions\":" |> ignore
    sb.Append(intStr s.UnassessableTransitions) |> ignore
    sb.Append ",\"verification_evidence_total\":" |> ignore
    sb.Append(intStr s.VerificationEvidenceTotal) |> ignore
    sb.Append "}" |> ignore
    sb.ToString()

// -----------------------------------------------------------------------------
// File writers (UTF-8 without BOM, exactly one terminal LF)
// -----------------------------------------------------------------------------

let private utf8NoBom : Encoding = new UTF8Encoding(false)

let writeLineOriented (path: string) (text: string) : unit =
    let dir = Path.GetDirectoryName path
    if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    let body =
        if text.EndsWith "\n" then text
        else text + "\n"
    File.WriteAllText(path, body, utf8NoBom)
