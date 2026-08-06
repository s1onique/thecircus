module Circus.Tooling.FSharpDiagnostics.RuleCandidates.Paths

// =============================================================================
// Output path authority
// =============================================================================
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-RULE-CANDIDATE-EXTRACTION01-CORRECTION01
//
// The canonical artifact filenames for the v2 schema are
// `rule-candidates-v2.jsonl` and `rule-candidate-summary-v2.json`.  The v1
// schema was already published as a compatibility surface and must not be
// silently overwritten.

let ruleCandidateSchemaRelativePath =
    "factory/evidence/fsharp-diagnostics/schemas/rule-candidate-v2.schema.json"

let ruleCandidateSummarySchemaRelativePath =
    "factory/evidence/fsharp-diagnostics/schemas/rule-candidate-summary-v2.schema.json"

let ruleCandidatesCorpusRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized"

let ruleCandidatesJsonlRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidates-v2.jsonl"

let ruleCandidatesSummaryRelativePath =
    "factory/evidence/fsharp-diagnostics/corpus/normalized/rule-candidate-summary-v2.json"

/// Convert a relative path to an absolute path given a repository root.
let toAbsolutePath (repoRoot: string) (relativePath: string) : string =
    if System.IO.Path.IsPathRooted relativePath then
        relativePath
    else
        System.IO.Path.Combine(repoRoot, relativePath)
