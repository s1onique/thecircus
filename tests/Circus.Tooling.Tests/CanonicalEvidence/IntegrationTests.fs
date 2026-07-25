module Circus.Tooling.Tests.CanonicalEvidence.IntegrationTests

// Repository integration and mutation tests for CORRECTION03. These tests use
// isolated temporary files and subprocesses; they never regenerate or modify
// the repository's canonical evidence artifacts.

open System
open System.Diagnostics
open System.IO
open System.Text
open System.Text.Json
open Expecto

open Circus.Tooling.CanonicalEvidence.Domain
open Circus.Tooling.CanonicalEvidence.Serialization

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".."))

let private tempDir label =
    let path = Path.Combine(Path.GetTempPath(), label + "-" + Guid.NewGuid().ToString("N"))
    Directory.CreateDirectory path |> ignore
    path

let private cleanup path =
    try
        if Directory.Exists path then Directory.Delete(path, true)
    with _ -> ()

let private runProcess workingDirectory executable arguments =
    let psi = ProcessStartInfo()
    psi.FileName <- executable
    psi.WorkingDirectory <- workingDirectory
    psi.UseShellExecute <- false
    psi.RedirectStandardOutput <- true
    psi.RedirectStandardError <- true
    for argument in arguments do psi.ArgumentList.Add argument
    use child = Process.Start psi
    let stdoutTask = child.StandardOutput.ReadToEndAsync()
    let stderrTask = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdoutTask.Result, stderrTask.Result

let private sampleCheck id =
    {
        Id = id
        CommandArgv = [ "dotnet"; "test"; id ]
        WorkingDirectory = repoRoot
        DurationMilliseconds = 1L
        ExitCode = Some 0
        Status = Pass
        StdoutSha256 = Some(String.replicate 64 "a")
        StderrSha256 = Some(String.replicate 64 "b")
        FailureKind = None
    }

let private sampleEvidence () =
    let document =
        {
            SchemaVersion = SchemaVersionValue
            ProviderName = ProviderNameValue
            ProviderVersion = ProviderVersionValue
            TestedCommitOid = String.replicate 40 "c"
            TestedTreeOid = String.replicate 40 "d"
            ObjectFormat = "sha1"
            Checks = SupportedCheckIds |> List.map sampleCheck |> List.sortBy (fun check -> check.Id)
            OverallStatus = Pass
            SemanticSha256 = ""
        }
    { document with SemanticSha256 = computeSemanticHash document }

let private writeCanonical path evidence =
    File.WriteAllText(path, renderWireJson evidence + "\n", UTF8Encoding(false))

let private projectionScript = Path.Combine(repoRoot, "scripts", "project_leamas_gate_summary.py")
let private policyScript = Path.Combine(repoRoot, "scripts", "verify_canonical_evidence_policy.py")

let private project canonical output =
    runProcess repoRoot "python3"
        [ projectionScript
          "--canonical"; canonical
          "--output"; output
          "--generated-at"; "2026-07-25T00:00:00Z" ]

let private verifyProjection canonical output =
    runProcess repoRoot "python3"
        [ projectionScript
          "--canonical"; canonical
          "--output"; output
          "--verify-only" ]

let private gateSummaryBlock digest =
    let lines = File.ReadAllLines digest
    lines
    |> Array.skipWhile (fun line -> line <> "## GATE_SUMMARY")
    |> Array.skip 1
    |> Array.takeWhile (fun line -> not (line.StartsWith("## ", StringComparison.Ordinal)))
    |> String.concat "\n"

let private copyPolicyFixture destination =
    let files =
        [ ".factory/evidence-provider-registry.json"
          ".factory/evidence-provider-schema.json"
          ".factory/canonical-evidence.json"
          ".gitattributes"
          "Makefile"
          "scripts/project_leamas_gate_summary.py"
          "scripts/verify_canonical_evidence_policy.py"
          "tools/Circus.Tooling/CanonicalEvidence/Domain.fs"
          "tools/Circus.Tooling/CanonicalEvidence/Provider.fs"
          "tools/Circus.Tooling/CanonicalEvidence/Cli.fs"
          "tools/Circus.Tooling/CanonicalEvidence/Serialization.fs"
          "tools/Circus.Tooling/CanonicalEvidence/Validation.fs" ]
    for relative in files do
        let source = Path.Combine(repoRoot, relative)
        let target = Path.Combine(destination, relative)
        Directory.CreateDirectory(Path.GetDirectoryName target) |> ignore
        if relative = ".factory/canonical-evidence.json" then
            writeCanonical target (sampleEvidence ())
        else
            File.Copy(source, target, true)

let private mutateJson path mutation =
    use document = JsonDocument.Parse(File.ReadAllText path)
    let root = document.RootElement
    let mutableNode = System.Text.Json.Nodes.JsonNode.Parse(root.GetRawText())
    mutation mutableNode
    File.WriteAllText(path, mutableNode.ToJsonString(JsonSerializerOptions(WriteIndented = true)) + "\n")

[<Tests>]
let tests =
    testList "CanonicalEvidence.Integration" [
        test "Leamas consumes the fixed-path projection with nine named checks" {
            let dir = tempDir "circus-canonev-leamas"
            try
                let canonical = Path.Combine(dir, "canonical.json")
                let projection = Path.Combine(dir, "gate-summary.json")
                let digest = Path.Combine(dir, "digest.txt")
                writeCanonical canonical (sampleEvidence ())
                let projectCode, _, projectErr = project canonical projection
                Expect.equal projectCode 0 projectErr
                let factoryDir = Path.Combine(dir, ".factory")
                Directory.CreateDirectory factoryDir |> ignore
                File.Copy(projection, Path.Combine(factoryDir, "gate-summary.json"))
                let initCode, _, initErr = runProcess dir "git" [ "init"; "-q" ]
                Expect.equal initCode 0 initErr
                let digestCode, _, digestErr = runProcess dir "leamas" [ "factory"; "digest"; "--dirty"; "--output"; digest ]
                Expect.equal digestCode 0 digestErr
                let block = gateSummaryBlock digest
                Expect.stringContains block "source=.factory/gate-summary.json" "actual fixed source"
                Expect.stringContains block "source_status=present" "projection decoded"
                Expect.stringContains block "checks_total=9" "all checks consumed"
                Expect.stringContains block "checks_passed=9" "all checks pass"
                Expect.stringContains block "checks_failed=0" "no failed checks"
                for name in SupportedCheckIds do
                    Expect.stringContains block ("name=" + name + " status=pass") ("named check " + name)
                let checkLines =
                    block.Split('\n')
                    |> Array.filter (fun line -> line.StartsWith("  - name=", StringComparison.Ordinal))
                Expect.all checkLines (fun line -> line.Contains(" evidence=", StringComparison.Ordinal)) "every check has evidence"
            finally cleanup dir
        }

        test "missing projection fails closed" {
            let dir = tempDir "circus-canonev-projection-missing"
            try
                let canonical = Path.Combine(dir, "canonical.json")
                writeCanonical canonical (sampleEvidence ())
                let code, stdout, stderr = verifyProjection canonical (Path.Combine(dir, "missing.json"))
                Expect.notEqual code 0 "missing projection rejected"
                Expect.stringContains (stdout + stderr) "not found" "reason surfaced"
            finally cleanup dir
        }

        test "stale projection fails closed" {
            let dir = tempDir "circus-canonev-projection-stale"
            try
                let canonical = Path.Combine(dir, "canonical.json")
                let projection = Path.Combine(dir, "projection.json")
                let initial = sampleEvidence ()
                writeCanonical canonical initial
                let code, _, err = project canonical projection
                Expect.equal code 0 err
                let changedCheck = { initial.Checks.Head with DurationMilliseconds = 2L }
                let changed = { initial with Checks = changedCheck :: initial.Checks.Tail; SemanticSha256 = "" }
                writeCanonical canonical { changed with SemanticSha256 = computeSemanticHash changed }
                let verifyCode, stdout, stderr = verifyProjection canonical projection
                Expect.notEqual verifyCode 0 "stale projection rejected"
                Expect.stringContains (stdout + stderr) "stale or incomplete" "stale binding surfaced"
            finally cleanup dir
        }

        test "projection semantic hash mismatch fails closed" {
            let dir = tempDir "circus-canonev-projection-hash"
            try
                let canonical = Path.Combine(dir, "canonical.json")
                let projection = Path.Combine(dir, "projection.json")
                writeCanonical canonical (sampleEvidence ())
                let code, _, err = project canonical projection
                Expect.equal code 0 err
                let text = File.ReadAllText projection
                File.WriteAllText(projection, text.Replace(String.replicate 64 "a", String.replicate 64 "e"))
                let verifyCode, _, verifyErr = verifyProjection canonical projection
                Expect.notEqual verifyCode 0 "tampered evidence binding rejected"
                Expect.stringContains verifyErr "semantic binding mismatch" "hash mismatch surfaced"
            finally cleanup dir
        }

        test "projected check names are non-empty and unique" {
            let dir = tempDir "circus-canonev-projection-names"
            try
                let canonical = Path.Combine(dir, "canonical.json")
                let projection = Path.Combine(dir, "projection.json")
                writeCanonical canonical (sampleEvidence ())
                let code, _, err = project canonical projection
                Expect.equal code 0 err
                use document = JsonDocument.Parse(File.ReadAllText projection)
                let names =
                    document.RootElement.GetProperty("checks").EnumerateArray()
                    |> Seq.map (fun check -> check.GetProperty("name").GetString())
                    |> Seq.toList
                Expect.all names (String.IsNullOrWhiteSpace >> not) "all names non-empty"
                Expect.equal (names |> Set.ofList |> Set.count) names.Length "all names unique"
            finally cleanup dir
        }

        test "gate policy verifies and removal mutation is detected" {
            let code, stdout, stderr = runProcess repoRoot "python3" [ policyScript; "--repo-root"; repoRoot ]
            Expect.equal code 0 stderr
            Expect.stringContains stdout "mutation detected" "control mutation executed"
            let dir = tempDir "circus-canonev-gate-policy"
            try
                copyPolicyFixture dir
                let makefile = Path.Combine(dir, "Makefile")
                let text = File.ReadAllText makefile
                File.WriteAllText(makefile, text.Replace("gate: verify-canonical-evidence factorize", "gate: factorize"))
                let mutationCode, _, mutationErr = runProcess dir "python3" [ Path.Combine(dir, "scripts", "verify_canonical_evidence_policy.py"); "--repo-root"; dir ]
                Expect.notEqual mutationCode 0 "removed prerequisite rejected"
                Expect.stringContains mutationErr "gate must contain verify-canonical-evidence" "wiring failure surfaced"
            finally cleanup dir
        }

        test "schema typo mutation is detected by exact provider agreement" {
            let dir = tempDir "circus-canonev-schema-policy"
            try
                copyPolicyFixture dir
                let schema = Path.Combine(dir, ".factory", "evidence-provider-schema.json")
                File.WriteAllText(schema, File.ReadAllText(schema).Replace("circus-canonical-evidence", "circuit-canonical-evidence"))
                let code, _, stderr = runProcess dir "python3" [ Path.Combine(dir, "scripts", "verify_canonical_evidence_policy.py"); "--repo-root"; dir ]
                Expect.notEqual code 0 "schema typo rejected"
                Expect.stringContains stderr "schema.provider mismatch" "exact disagreement surfaced"
            finally cleanup dir
        }

        test "CanonicalEvidence production inventory does not invoke Git executable mutators" {
            let sourceDir = Path.Combine(repoRoot, "tools", "Circus.Tooling", "CanonicalEvidence")
            let source =
                Directory.GetFiles(sourceDir, "*.fs")
                |> Array.sort
                |> Array.map File.ReadAllText
                |> String.concat "\n"
            for token in [ "setGitExecutable"; "resetGitExecutable" ] do
                Expect.isFalse (source.Contains token) ("production source excludes " + token)
        }
    ]
