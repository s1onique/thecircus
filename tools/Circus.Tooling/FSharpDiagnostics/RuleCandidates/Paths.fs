module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths

// =============================================================================
// Output path authority
// =============================================================================
//
// This module defines the canonical output paths for rule-candidate artifacts.
// These paths follow the repository's existing normalized corpus layout.

// -----------------------------------------------------------------------------
// Path constants
// -----------------------------------------------------------------------------

/// Relative path to the rule-candidate schema file.
let ruleCandidateSchemaRelativePath =
    "factory/evidence/fsharp-diagnostics/schemas/rule-candidate-v1.schema.json"

/// Relative path to the rule-candidates corpus directory.
let ruleCandidatesCorpusRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized"

/// Relative path to the rule-candidates JSONL output.
let ruleCandidatesJsonlRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v1.jsonl"

/// Relative path to the rule-candidate summary JSON output.
let ruleCandidatesSummaryRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v1.json"

/// Convert a relative path to an absolute path given a repository root.
let toAbsolutePath (repoRoot: string) (relativePath: string) : string =
    if System.IO.Path.IsPathRooted relativePath then
        relativePath
    else
        System.IO.Path.Combine(repoRoot, relativePath)
