module Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Paths

open Circus.Tooling.FSharpDiagnostics.Paths

// =============================================================================
// Episode subdirectory and file paths
// =============================================================================
//
// All paths follow the existing convention: three explicit domains
// (leaf filename, canonical-corpus-relative path, repository-relative
// canonical path).  Path construction literals are NOT duplicated in
// production or tests — callers consume these constants.

// Subdirectory under the canonical corpus root that holds episode artefacts.
let episodesSubdir = canonicalRootRelative + "/corpus/episodes"

// Subdirectory for explicit declaration JSON inputs.
let declarationsSubdir = episodesSubdir + "/declarations"

// Subdirectory for normalized episode outputs (JSONL + summary).
let normalizedEpisodesSubdir = episodesSubdir + "/normalized"

// -----------------------------------------------------------------------------
// Schemas (leaf / corpus-relative / repo-relative)
// -----------------------------------------------------------------------------

let repairEpisodeDeclarationSchemaFile = "repair-episode-declaration-v1.schema.json"
let repairEpisodeDeclarationSchemaCorpusRelativePath =
    "schemas/" + repairEpisodeDeclarationSchemaFile
let repairEpisodeDeclarationSchemaCanonicalPath =
    canonicalRootRelative + "/" + repairEpisodeDeclarationSchemaCorpusRelativePath

let repairEpisodeSchemaFile = "repair-episode-v1.schema.json"
let repairEpisodeSchemaCorpusRelativePath = "schemas/" + repairEpisodeSchemaFile
let repairEpisodeSchemaCanonicalPath =
    canonicalRootRelative + "/" + repairEpisodeSchemaCorpusRelativePath

let diagnosticTransitionSchemaFile = "diagnostic-transition-v1.schema.json"
let diagnosticTransitionSchemaCorpusRelativePath =
    "schemas/" + diagnosticTransitionSchemaFile
let diagnosticTransitionSchemaCanonicalPath =
    canonicalRootRelative + "/" + diagnosticTransitionSchemaCorpusRelativePath

let gitChangeSetSchemaFile = "git-change-set-v1.schema.json"
let gitChangeSetSchemaCorpusRelativePath = "schemas/" + gitChangeSetSchemaFile
let gitChangeSetSchemaCanonicalPath =
    canonicalRootRelative + "/" + gitChangeSetSchemaCorpusRelativePath

let verificationEvidenceSchemaFile = "verification-evidence-v1.schema.json"
let verificationEvidenceSchemaCorpusRelativePath =
    "schemas/" + verificationEvidenceSchemaFile
let verificationEvidenceSchemaCanonicalPath =
    canonicalRootRelative + "/" + verificationEvidenceSchemaCorpusRelativePath

// -----------------------------------------------------------------------------
// Normalized outputs (leaf / corpus-relative / repo-relative)
// -----------------------------------------------------------------------------

let repairEpisodesFile = "repair-episodes-v1.jsonl"
let repairEpisodesCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + repairEpisodesFile
let repairEpisodesCanonicalPath =
    canonicalRootRelative + "/" + repairEpisodesCorpusRelativePath

let diagnosticTransitionsFile = "diagnostic-transitions-v1.jsonl"
let diagnosticTransitionsCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + diagnosticTransitionsFile
let diagnosticTransitionsCanonicalPath =
    canonicalRootRelative + "/" + diagnosticTransitionsCorpusRelativePath

let gitChangeSetsFile = "git-change-sets-v1.jsonl"
let gitChangeSetsCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + gitChangeSetsFile
let gitChangeSetsCanonicalPath =
    canonicalRootRelative + "/" + gitChangeSetsCorpusRelativePath

let repairEpisodeSummaryFile = "repair-episode-summary-v1.json"
let repairEpisodeSummaryCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + repairEpisodeSummaryFile
let repairEpisodeSummaryCanonicalPath =
    canonicalRootRelative + "/" + repairEpisodeSummaryCorpusRelativePath

let verificationEvidenceFile = "verification-evidence-v1.jsonl"
let verificationEvidenceCorpusRelativePath =
    normalizedCorpusRelativeSubdir + "/" + verificationEvidenceFile
let verificationEvidenceCanonicalPath =
    canonicalRootRelative + "/" + verificationEvidenceCorpusRelativePath

// -----------------------------------------------------------------------------
// Declaration directory contents (leaf / corpus-relative / repo-relative)
// -----------------------------------------------------------------------------

let declarationDirCorpusRelativePath = "corpus/episodes/declarations"
let declarationDirCanonicalPath = canonicalRootRelative + "/" + declarationDirCorpusRelativePath

// Combined list of all episode-managed canonical paths, useful for tests and
// inventory.  Does NOT include the existing foundation outputs (which the
// foundation manifest already inventories separately).
let allEpisodeCanonicalPaths : string list =
    [
        repairEpisodeDeclarationSchemaCanonicalPath
        repairEpisodeSchemaCanonicalPath
        diagnosticTransitionSchemaCanonicalPath
        gitChangeSetSchemaCanonicalPath
        verificationEvidenceSchemaCanonicalPath
        repairEpisodesCanonicalPath
        diagnosticTransitionsCanonicalPath
        gitChangeSetsCanonicalPath
        repairEpisodeSummaryCanonicalPath
        verificationEvidenceCanonicalPath
    ]
