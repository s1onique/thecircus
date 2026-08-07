module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.EpisodeEngineCanonicalPreservationTests

open System
open System.IO
open Expecto
open Circus.Tooling.FSharpDiagnostics.Paths
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Engine
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths
open Circus.Tooling.FSharpDiagnostics.RuleCandidates
open Circus.Tooling.Tests.FSharpDiagnostics.RuleCandidates.RuleCandidateFailClosedFixture

let private canonicalFileNames =
    [| repairEpisodesFile
       diagnosticTransitionsFile
       gitChangeSetsFile
       repairEpisodeSummaryFile
       verificationEvidenceFile |]

let private canonicalRelative (file: string) : string =
    canonicalRootRelative + "/" + normalizedCorpusRelativeSubdir + "/" + file

let private canonicalAbsolute (root: string) (file: string) : string =
    Path.Combine(root, canonicalRelative file)

let private snapshotCanonical (root: string) : Map<string, string * bool> =
    // Each entry is (sha256-or-empty, exists).  Existence must be true
    // for every expected file both BEFORE and AFTER.
    canonicalFileNames
    |> Array.map (fun name ->
        let abs = canonicalAbsolute root name
        let exists = File.Exists abs
        let hash = if exists then Circus.Tooling.FSharpDiagnostics.Hashing.sha256OfFile abs else ""
        name, (hash, exists))
    |> Map.ofArray

let private verifyCanonicalExistenceAndBytes
    (root: string)
    (before: Map<string, string * bool>)
    (label: string)
    : unit =
    let after = snapshotCanonical root
    for name in canonicalFileNames do
        let bHash, bExists = Map.find name before
        let aOpt = Map.tryFind name after
        match aOpt with
        | None ->
            failwithf "%s: canonical %s disappeared" label name
        | Some (aHash, aExists) ->
            if not bExists then
                failwithf "%s: canonical %s did not exist before the run; preservation cannot be asserted" label name
            if not aExists then
                failwithf "%s: canonical %s disappeared during the run" label name
            Expect.equal
                (aHash = bHash)
                true
                (sprintf "%s: canonical %s bytes must be unchanged (before=%s after=%s)" label name bHash aHash)

/// Set up a temp repository containing a clone of the production
/// corpus plus a recursive copy of the production .git subtree, so
/// commit OIDs resolve against the real history without mutating the
/// production tree.  The temp directory is fully isolated; no
/// production files are written.
let private setupIsolatedCorpusRepo (repo: TempRepository) : unit =
    let srcRoot = productionRepoRoot ()
    let rec copyRecursive (src: string) (dst: string) : unit =
        if File.Exists src then
            let dir = Path.GetDirectoryName dst
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore
            File.Copy(src, dst, true)
        elif Directory.Exists src then
            if not (Directory.Exists dst) then
                Directory.CreateDirectory dst |> ignore
            for entry in Directory.EnumerateFileSystemEntries src do
                let name = Path.GetFileName entry
                copyRecursive entry (Path.Combine(dst, name))
    let copyRelative (rel: string) : unit =
        let src = Path.Combine(srcRoot, rel.Replace('/', Path.DirectorySeparatorChar))
        let dst = repo.Absolute rel
        copyRecursive src dst
    copyRelative "factory/evidence/fsharp-diagnostics/corpus/episodes/declarations"
    copyRelative "factory/evidence/fsharp-diagnostics/corpus/raw"
    copyRelative "factory/evidence/fsharp-diagnostics/corpus/manifests"
    copyRelative "factory/evidence/fsharp-diagnostics/corpus/normalized"
    // Copy the entire .git subtree recursively so commit OIDs resolve
    // against the real history.  The temp directory never mutates the
    // production checkout.
    copyRelative ".git"

let private duplicateDeclarationBody () : string =
    """{"schema_version":"repair-episode-declaration-v1","episode_key":"fsb-dup-bytes","before_capture_id":"fsb-0025-before-c79f0ec","after_capture_id":"fsb-0025-after-c79f0ec","before_commit_oid":"be84cb3cb0b540fa0c895afd7f7c6a41c01c81c6","after_commit_oid":"c79f0ecfff6b7e4c34ae469ea55a4a4b60adca91","expected_before_tree_oid":"111de4f330d2076f2b7e96d683a3f4b142c3bee4","expected_after_tree_oid":"2cf1c11e8e6f3c9c950affa87706361c9601755b","verification_evidence_ids":["8eb41f21b7e2c8809db481daa8af71fea55eb21146106245ca95fb4baeabfb70"],"declared_relevant_paths":["tools/Circus.Tooling/NoForcePush/GitHubRules.fs"],"notes":"Duplicate declaration injected for canonical preservation test."}"""

let private writeDuplicate (repo: TempRepository) : unit =
    let path =
        repo.Absolute
            "factory/evidence/fsharp-diagnostics/corpus/episodes/declarations/fsb-dup-bytes.json"
    File.WriteAllText(path, duplicateDeclarationBody ())

[<Tests>]
let episodeEngineCanonicalPreservationTests =
    testList
        "FSharpDiagnostics.RepairEpisodes.CanonicalPreservation"
        [ test "duplicate episode declaration: runEpisodeEngine returns Failed and preserves canonical bytes" {
              use repo = new TempRepository()
              setupIsolatedCorpusRepo repo
              let before = snapshotCanonical repo.Root

              writeDuplicate repo
              let outcome = runEpisodeEngine repo.Root defaultEngineOptions
              let after = snapshotCanonical repo.Root

              match outcome with
              | EpisodeEngineExecution.Failed(EpisodeEngineFailure.DuplicateInputIdentities dups) ->
                  Expect.isFalse (List.isEmpty dups) "at least one duplicate identity"
                  let kinds =
                      dups
                      |> List.map (fun d -> d.Kind)
                      |> List.distinct
                  // The post-computation, pre-publication duplicate gate
                  // surfaces all three upstream kinds when two
                  // declarations share the same capture IDs and
                  // commit OIDs.  This is the only end-to-end
                  // assertion in the suite; the 18 upstream tests
                  // exercise `detectUpstreamDuplicates` directly.
                  Expect.equal
                    kinds
                    [ EpisodeInputIdentityKind.RepairEpisode
                      EpisodeInputIdentityKind.ChangeSet
                      EpisodeInputIdentityKind.DiagnosticTransition ]
                    "duplicate declaration must surface all three upstream identity kinds"
              | other ->
                  failwithf "expected Failed(DuplicateInputIdentities), got %A" other

              verifyCanonicalExistenceAndBytes repo.Root before "duplicate episode"
          }

          test "canonical evidence error key: length-prefixed framing survives embedded separators" {
              // Adversarial regression: the previous delimiter-only
              // `nonDupKey` would collapse these two structurally
              // distinct error tuples to the same string.  The
              // length-prefixed framing must produce distinct keys.
              let path1 = "factory/evidence/fsharp-diagnostics/corpus/normalized/verification-evidence-v1.jsonl"
              let path2 = "factory/evidence/fsharp-diagnostics/corpus/normalized/verification-evidence-v1.jsonl"
              let id1 = "id-with|pipe|1"
              let id2 = "id-with|pipe|2"
              let dup1 =
                  VerificationEvidenceLoadError.DuplicateEvidenceId(path1, id1, 1, 2)
              let dup2 =
                  VerificationEvidenceLoadError.DuplicateEvidenceId(path2, id2, 1, 2)
              let conf =
                  VerificationEvidenceLoadError.ConflictingEvidenceRecord(path1, id1, 1, 2)
              let fwd =
                  Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine.mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed [ dup1; conf; dup2 ]
                  )
              Expect.equal
                  fwd.Length
                  2
                  "expected [Duplicate(identity, identity); VerificationEvidenceLoadFailed(conflicting)]"
              Expect.equal
                  (fwd |> List.distinct |> List.length)
                  2
                  "duplicate and conflicting must remain in different buckets"
          }

          test "non-duplicate evidence order is invariant under record reversal even with embedded delimiters" {
              // True collision regression for the OLD delimiter-only `nonDupKey`:
              //
              //   key("MalformedJson(\"a\", 1, \"b|2|c\")")
              //     = "malformed|a|1|b|2|c|"
              //   key("MalformedJson(\"a|1|b\", 2, \"c\")")
              //     = "malformed|a|1|b|2|c|"
              //
              // Two structurally distinct ParseError(MalformedJson) tuples
              // produce IDENTICAL old `nonDupKey` strings because the `|`
              // inside the field values is not escaped.  When the
              // pre-existing mapping collapsed identical keys via `Map`,
              // one of these two errors was silently dropped.  The NEW
              // length-prefixed framing produces distinct keys, so both
              // errors survive in the mapped output as independent strings.
              //
              // The third error is a `ConflictingEvidenceRecord` whose own
              // delimiter-only key also embeds `|` via its `source` field.
              // Length-prefixed framing keeps it stable too.
              let fwd =
                  Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine.mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed
                          [ VerificationEvidenceLoadError.ParseError(
                                VerificationEvidenceParseError.MalformedJson("a", 1, "b|2|c"))
                            VerificationEvidenceLoadError.ParseError(
                                VerificationEvidenceParseError.MalformedJson("a|1|b", 2, "c"))
                            VerificationEvidenceLoadError.ConflictingEvidenceRecord("p|x", "id|q", 1, 2) ]
                  )
              let rev =
                  Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine.mapEpisodeEngineFailure(
                      EpisodeEngineFailure.VerificationEvidenceLoadFailed
                          [ VerificationEvidenceLoadError.ConflictingEvidenceRecord("p|x", "id|q", 1, 2)
                            VerificationEvidenceLoadError.ParseError(
                                VerificationEvidenceParseError.MalformedJson("a|1|b", 2, "c"))
                            VerificationEvidenceLoadError.ParseError(
                                VerificationEvidenceParseError.MalformedJson("a", 1, "b|2|c")) ]
                  )
              Expect.equal fwd rev "mapped result must be invariant under record reversal even with embedded delimiters"
              // The adapter groups every non-duplicate into a single
              // `VerificationEvidenceLoadFailed` carrying a sorted list of
              // stringified errors.  The OLD delimiter framing would have
              // collapsed the two MalformedJson cases to the same key and
              // emitted only TWO distinct strings.  Length-prefixed framing
              // must yield THREE distinct strings.
              Expect.equal
                  fwd.Length
                  1
                  "non-duplicates must collapse into one VerificationEvidenceLoadFailed"
              match fwd with
              | [ Circus.Tooling.FSharpDiagnostics.RuleCandidates.Engine.EngineError.VerificationEvidenceLoadFailed strs ] ->
                  Expect.equal
                      strs.Length
                      3
                      "both distinct MalformedJson errors and the conflicting record must survive length-prefixed framing"
                  Expect.equal
                      (strs |> List.distinct |> List.length)
                      3
                      "all three rendered strings must be byte-distinct"
              | other ->
                  failwithf "expected [VerificationEvidenceLoadFailed [_;_;_]], got %A" other
          } ]
