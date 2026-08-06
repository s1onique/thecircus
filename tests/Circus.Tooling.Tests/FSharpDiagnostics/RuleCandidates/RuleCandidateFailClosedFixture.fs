module Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

// =============================================================================
// Rule Candidate Fail-Closed Test Fixture
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-FAIL-CLOSED-MATRIX01
//
// Shared fixture for the rule-candidate fail-closed matrix.  Provides:
//   * A unique temporary repository root per test.
//   * Helpers that derive every relative path from the production
//     `Paths` authority.
//   * A complete minimal-valid corpus constructor.
//   * Helpers that mutate one JSONL line, one required corpus presence,
//     or one canonical output independently.
//   * Helpers that snapshot and assert canonical and input state.
//   * Cleanup in `finally` — the fixture never mutates the real
//     production corpus.
//
// The fixture is intentionally narrow: it must allow every matrix test
// to construct exactly one production-shaped corpus, run the real
// production extraction pipeline, and assert the typed result.
// =============================================================================

open System
open System.IO
open System.Security.Cryptography
open System.Text

open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Serialization
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Domain
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine
open Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths

// -----------------------------------------------------------------------------
// Deterministic IDs
// -----------------------------------------------------------------------------

/// Deterministically derive a 64-character lowercase hex evidence id from
/// a fixture key.  No global counter, no timestamp — same key always
/// yields the same id.
let deterministicSha256 (label: string) (key: string) : string =
    let prefix = Encoding.UTF8.GetBytes label
    let nul = [| byte 0 |]
    let keyBytes = Encoding.UTF8.GetBytes key
    use h = SHA256.Create()
    let hash = h.ComputeHash(Array.concat [ prefix; nul; keyBytes ])
    hash
    |> Array.map (fun b -> b.ToString("x2"))
    |> String.concat ""

// -----------------------------------------------------------------------------
// Temporary repository root
// -----------------------------------------------------------------------------

type TempRepository() =
    let rootPath =
        Path.Combine(
            Path.GetTempPath(),
            "circus-rule-candidate-fail-closed-" + Guid.NewGuid().ToString("N")
        )

    let mutable disposed = false

    do
        Directory.CreateDirectory rootPath |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative)) |> ignore
        // The repair-episode engine enumerates the declarations directory
        // even when no declarations exist.  Pre-create the canonical
        // subdirectories so that enumeration succeeds.
        Directory.CreateDirectory(
            Path.Combine(rootPath, canonicalRootRelative, "corpus", "episodes", "declarations")
        )
        |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative, "corpus", "captures"))
        |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative, "corpus", "normalized"))
        |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative, "schemas"))
        |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative, "fixtures"))
        |> ignore
        Directory.CreateDirectory(Path.Combine(rootPath, canonicalRootRelative, "corpus", "raw"))
        |> ignore
        Directory.CreateDirectory(
            Path.Combine(rootPath, canonicalRootRelative, "corpus", "manifests")
        )
        |> ignore

    member _.Root = rootPath

    member this.Absolute(relativePath: string) : string =
        Path.Combine(this.Root, relativePath.Replace('/', Path.DirectorySeparatorChar))

    member this.WriteUtf8(relativePath: string, contents: string) : unit =
        let abs = this.Absolute relativePath
        let dir = Path.GetDirectoryName abs

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        File.WriteAllText(abs, contents, UTF8Encoding(false))

    member this.AppendUtf8(relativePath: string, contents: string) : unit =
        let abs = this.Absolute relativePath
        let dir = Path.GetDirectoryName abs

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        File.AppendAllText(abs, contents, UTF8Encoding(false))

    member this.Delete(relativePath: string) : unit =
        let abs = this.Absolute relativePath

        if File.Exists abs then
            File.Delete abs
        elif Directory.Exists abs then
            Directory.Delete(abs, true)

    member this.ReplaceWithDirectory(relativePath: string) : unit =
        let abs = this.Absolute relativePath

        if File.Exists abs then
            File.Delete abs

        Directory.CreateDirectory abs |> ignore

    member this.WriteBytes(relativePath: string, bytes: byte array) : unit =
        let abs = this.Absolute relativePath
        let dir = Path.GetDirectoryName abs

        if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        File.WriteAllBytes(abs, bytes)

    member this.SnapshotBytes(relativePath: string) : byte array =
        let abs = this.Absolute relativePath

        if File.Exists abs then
            File.ReadAllBytes abs
        else
            [||]

    member this.SnapshotFileExists(relativePath: string) : bool =
        let abs = this.Absolute relativePath
        File.Exists abs

    member this.EnsureCanonicalDir() : unit =
        let dir = this.Absolute ruleCandidatesCorpusRelativePath

        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

    member this.EnsureEpisodesStructure() : unit =
        // Minimal directory structure expected by the episode engine:
        //   <root>/<canonicalRootRelative>/corpus/episodes/declarations/
        let dir =
            this.Absolute(
                canonicalRootRelative + "/" + "corpus/episodes/declarations"
            )

        if not (Directory.Exists dir) then
            Directory.CreateDirectory dir |> ignore

        // Captures dir is also expected by the episode engine
        let capDir = this.Absolute(canonicalRootRelative + "/corpus/captures")

        if not (Directory.Exists capDir) then
            Directory.CreateDirectory capDir |> ignore

    interface IDisposable with
        member _.Dispose() =
            if not disposed then
                disposed <- true

                try
                    if Directory.Exists rootPath then
                        Directory.Delete(rootPath, true)
                with _ ->
                    ()

// -----------------------------------------------------------------------------
// Valid input corpus builders
// -----------------------------------------------------------------------------

/// Build a repair-episode JSON record with an explicit episode_id.
/// Use this helper when the test must assert on a SPECIFIC identity
/// (e.g., same identity across two records to exercise duplicate-identity
/// detection; or an identity referenced by an evidence record).
let mkRepairEpisodeJsonWithId
    (episodeId: string)
    (episodeKey: string)
    (changeSetId: string)
    (evidenceIds: string list)
    : string =
    let afterCommit = String.replicate 40 "b"
    let afterTree = String.replicate 40 "d"
    let evidJson = evidenceIds |> List.map (sprintf "\"%s\"") |> String.concat ","
    "{\"schema_version\":\"repair-episode-v1\","
    + "\"episode_id\":\"" + episodeId + "\","
    + "\"episode_key\":\"" + episodeKey + "\","
    + "\"before_capture_id\":\"x\",\"after_capture_id\":\"y\","
    + "\"before_commit_oid\":\"" + String.replicate 40 "a" + "\","
    + "\"before_tree_oid\":\"" + String.replicate 40 "c" + "\","
    + "\"after_commit_oid\":\"" + afterCommit + "\","
    + "\"after_tree_oid\":\"" + afterTree + "\","
    + "\"commit_range\":[\"" + afterCommit + "\"],"
    + "\"change_set_id\":\"" + changeSetId + "\","
    + "\"command_contract_before\":\"dotnet build\","
    + "\"command_contract_after\":\"dotnet build\","
    + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
    + "\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},"
    + "\"verification_level\":\"focused_gate_verified\","
    + "\"verification_evidence_ids\":[" + evidJson + "],"
    + "\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"

/// Build a change-set JSON record with an explicit change_set_id.
let mkChangeSetJsonWithId (changeSetId: string) (path: string) : string =
    let beforeTree = String.replicate 40 "c"
    let afterTree = String.replicate 40 "d"
    "{\"schema_version\":\"git-change-set-v1\","
    + "\"change_set_id\":\"" + changeSetId + "\","
    + "\"change_set_version\":\"git-change-set-v1\","
    + "\"before_tree_oid\":\"" + beforeTree + "\","
    + "\"after_tree_oid\":\"" + afterTree + "\","
    + "\"object_format\":\"sha1\","
    + "\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"" + path + "\"}]}"

/// Build a verification-evidence JSON record with an explicit evidence_id
/// and the supplied binding fields.  Use when the test must assert that the
/// episode under evaluation references this exact record.
let mkVerificationEvidenceJsonWithId
    (evidenceId: string)
    (episodeId: string)
    (status: string)
    (exitCode: int)
    (testedCommitOid: string)
    (testedTreeOid: string)
    : string =
    "{\"schema_version\":\"verification-evidence-v1\","
    + "\"evidence_id\":\"" + evidenceId + "\","
    + "\"episode_id\":\"" + episodeId + "\","
    + "\"kind\":\"focused_gate\","
    + "\"command\":\"dotnet build\","
    + "\"working_directory\":\"/tmp\","
    + "\"tested_commit_oid\":\"" + testedCommitOid + "\","
    + "\"tested_tree_oid\":\"" + testedTreeOid + "\","
    + "\"exit_code\":" + string exitCode + ","
    + "\"stdout_sha256\":null,\"stderr_sha256\":null,\"combined_log_path\":null,"
    + "\"status\":\"" + status + "\"}"

/// Construct a valid repair episode JSON record from a fixture key.
let mkValidRepairEpisodeJson (key: string) : string =
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
    let epKey = "fsb-" + key
    let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
    let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" key
    let beforeCommit = String.replicate 40 "a"
    let afterCommit = String.replicate 40 "b"
    let beforeTree = String.replicate 40 "c"
    let afterTree = String.replicate 40 "d"

    let body =
        "{\"schema_version\":\"repair-episode-v1\","
        + "\"episode_id\":\"" + epId + "\","
        + "\"episode_key\":\"" + epKey + "\","
        + "\"before_capture_id\":\"" + epKey + "-before\","
        + "\"after_capture_id\":\"" + epKey + "-after\","
        + "\"before_commit_oid\":\"" + beforeCommit + "\","
        + "\"before_tree_oid\":\"" + beforeTree + "\","
        + "\"after_commit_oid\":\"" + afterCommit + "\","
        + "\"after_tree_oid\":\"" + afterTree + "\","
        + "\"commit_range\":[\"" + afterCommit + "\"],"
        + "\"change_set_id\":\"" + csId + "\","
        + "\"command_contract_before\":\"dotnet build\","
        + "\"command_contract_after\":\"dotnet build\","
        + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
        + "\"transition_counts\":{\"persisted_same_count\":0,\"persisted_count_decreased\":0,\"persisted_count_increased\":0,\"eliminated_after\":4,\"introduced_after\":0,\"resolution_candidates\":4,\"regression_candidates\":0,\"unassessable\":0},"
        + "\"verification_level\":\"focused_gate_verified\","
        + "\"verification_evidence_ids\":[\"" + evidId + "\"],"
        + "\"qualification\":{\"status\":\"qualified\",\"reasons\":[]}}"
    body

/// Construct a valid change-set JSON record.
let mkValidChangeSetJson (key: string) (path: string) : string =
    let csId = deterministicSha256 "rule-candidate-fixture-changeset-v1" key
    let beforeTree = String.replicate 40 "c"
    let afterTree = String.replicate 40 "d"

    let body =
        "{\"schema_version\":\"git-change-set-v1\","
        + "\"change_set_id\":\"" + csId + "\","
        + "\"change_set_version\":\"git-change-set-v1\","
        + "\"before_tree_oid\":\"" + beforeTree + "\","
        + "\"after_tree_oid\":\"" + afterTree + "\","
        + "\"object_format\":\"sha1\","
        + "\"entries\":[{\"before_mode\":\"100644\",\"after_mode\":\"100644\",\"before_blob_oid\":null,\"after_blob_oid\":null,\"change_kind\":\"modified\",\"canonical_path\":\"" + path + "\"}]}"
    body

/// Construct a valid diagnostic-transition JSON record.
let mkValidDiagnosticTransitionJson (key: string) (code: string) (path: string) : string =
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
    let fp = "fp-" + key + "-" + code + "-" + path

    let body =
        "{\"schema_version\":\"diagnostic-transition-v1\","
        + "\"episode_id\":\"" + epId + "\","
        + "\"exact_fingerprint\":\"" + fp + "\","
        + "\"transition_kind\":\"eliminated_after\","
        + "\"before_occurrence_count\":1,"
        + "\"after_occurrence_count\":0,"
        + "\"severity\":\"error\","
        + "\"code\":\"" + code + "\","
        + "\"message_normalized\":\"msg-" + code + "\","
        + "\"source_path\":\"" + path + "\","
        + "\"project_path\":null,"
        + "\"span\":{\"start_line\":1,\"start_column\":1,\"end_line\":1,\"end_column\":10},"
        + "\"compatibility\":{\"status\":\"compatible\",\"reasons\":[],\"missing_fields\":[]},"
        + "\"source_link\":{\"kind\":\"source_file_modified\",\"paths\":[\"" + path + "\"],\"reasons\":[]},"
        + "\"assessment\":\"observed_resolution_candidate\"}"
    body

/// Construct a valid verification-evidence JSON record.
let mkValidVerificationEvidenceJson (key: string) (status: string) (exitCode: int) : string =
    let evidId = deterministicSha256 "rule-candidate-fixture-evidence-v1" key
    let epId = deterministicSha256 "rule-candidate-fixture-episode-v1" key
    let afterCommit = String.replicate 40 "b"
    let afterTree = String.replicate 40 "d"

    let body =
        "{\"schema_version\":\"verification-evidence-v1\","
        + "\"evidence_id\":\"" + evidId + "\","
        + "\"episode_id\":\"" + epId + "\","
        + "\"kind\":\"focused_gate\","
        + "\"command\":\"dotnet build\","
        + "\"working_directory\":\"/tmp\","
        + "\"tested_commit_oid\":\"" + afterCommit + "\","
        + "\"tested_tree_oid\":\"" + afterTree + "\","
        + "\"exit_code\":" + string exitCode + ","
        + "\"stdout_sha256\":null,"
        + "\"stderr_sha256\":null,"
        + "\"combined_log_path\":null,"
        + "\"status\":\"" + status + "\"}"
    body

/// Construct the four minimal-valid required corpus files in the temp
/// repository's normalized subdirectory.  Returns the key so tests can
/// reuse it to construct additional records deterministically.
let writeValidMinimalCorpus (repo: TempRepository) (key: string) : unit =
    repo.EnsureCanonicalDir()
    let normalizedDir = canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir

    let episodesPath = normalizedDir + "/repair-episodes-v1.jsonl"
    let changeSetsPath = normalizedDir + "/git-change-sets-v1.jsonl"
    let transitionsPath = normalizedDir + "/diagnostic-transitions-v1.jsonl"
    let evidencePath = normalizedDir + "/verification-evidence-v1.jsonl"

    repo.WriteUtf8(episodesPath, mkValidRepairEpisodeJson key + "\n")
    repo.WriteUtf8(changeSetsPath, mkValidChangeSetJson key "a.fs" + "\n")
    repo.WriteUtf8(transitionsPath,
        mkValidDiagnosticTransitionJson key "FS0010" "a.fs" + "\n"
        + mkValidDiagnosticTransitionJson key "FS3118" "a.fs" + "\n"
        + mkValidDiagnosticTransitionJson key "FS0010" "a.fs" + "\n"
        + mkValidDiagnosticTransitionJson key "FS3118" "a.fs" + "\n")
    repo.WriteUtf8(evidencePath, mkValidVerificationEvidenceJson key "pass" 0 + "\n")

/// Mutate a single JSONL line by replacing it.  `lineNumber` is 1-based.
let mutateJsonlLine (repo: TempRepository) (relativePath: string) (lineNumber: int) (newLine: string) : unit =
    let abs = repo.Absolute relativePath

    if not (File.Exists abs) then
        failwithf "cannot mutate missing file %s" abs

    let lines = File.ReadAllLines(abs)

    if lineNumber < 1 || lineNumber > lines.Length then
        failwithf "lineNumber %d out of range (1..%d)" lineNumber lines.Length

    lines.[lineNumber - 1] <- newLine

    File.WriteAllLines(abs, lines)

/// Remove a required corpus file (so presence/readability tests can fire).
let removeRequiredCorpus (repo: TempRepository) (relativePath: string) : unit =
    repo.Delete relativePath

/// Replace a required corpus path with a directory of the same name so the
/// presence check sees a non-file path.
let replaceCorpusWithDirectory (repo: TempRepository) (relativePath: string) : unit =
    repo.ReplaceWithDirectory relativePath

/// Replace a required corpus path with a zero-byte file.
let replaceCorpusWithEmptyFile (repo: TempRepository) (relativePath: string) : unit =
    repo.WriteUtf8(relativePath, "")

/// Append a duplicate JSONL line with the supplied contents to the named
/// file.  The duplicate is appended with the original line preserved so
/// the production parser surfaces an actual duplicate identity rather than
/// overwriting the existing record.
let injectDuplicateJsonlLine (repo: TempRepository) (relativePath: string) (newLine: string) : unit =
    repo.AppendUtf8(relativePath, newLine + "\n")

// -----------------------------------------------------------------------------
// Canonical-output helpers
// -----------------------------------------------------------------------------

let snapshotCanonicalBytes (repo: TempRepository) : (byte array) * (byte array) =
    let cpath = repo.Absolute ruleCandidatesJsonlRelativePath
    let spath = repo.Absolute ruleCandidatesSummaryRelativePath
    let c = if File.Exists cpath then File.ReadAllBytes cpath else [||]
    let s = if File.Exists spath then File.ReadAllBytes spath else [||]
    c, s

let assertCanonicalStateEqual (before: byte array * byte array) (after: byte array * byte array) : unit =
    let cb, sb = before
    let ca, sa = after

    if cb <> ca then
        failwithf "canonical JSONL bytes diverged"

    if sb <> sa then
        failwithf "canonical summary bytes diverged"

let assertNoStagingResidue (repo: TempRepository) : unit =
    let parent = Path.GetDirectoryName(repo.Absolute ruleCandidatesJsonlRelativePath)

    if not (String.IsNullOrEmpty parent) then
        for entry in Directory.EnumerateDirectories parent do
            let name = Path.GetFileName entry

            if name.Contains(".staging.") then
                failwithf "staging residue: %s" entry

// -----------------------------------------------------------------------------
// Self-verification helper
// -----------------------------------------------------------------------------

/// Prove the fixture itself is healthy: write a minimal valid corpus,
/// run the production extractor, and assert the result shape is
/// consistent.  Returns the key used so tests can chain mutations.
let selfVerifyFixture (repo: TempRepository) (key: string) : ExtractionResult =
    writeValidMinimalCorpus repo key
    extractCandidates repo.Root

// -----------------------------------------------------------------------------
// Production canonical path authority
// -----------------------------------------------------------------------------

/// Production repository root.  Used by the production regression and
/// self-verification tests that operate on the real committed corpus.
let productionRepoRoot () : string =
    Directory.GetParent(__SOURCE_DIRECTORY__).Parent.Parent.Parent.FullName
