module Circus.Tooling.Tests.EvidenceValidatorTests

open System
open System.Text
open System.Text.Json
open Expecto

open Circus.Tooling.Tests.AuthorityTestSupport
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.EvidenceValidator.Domain
open Circus.Tooling.EvidenceValidator.Validation

let private evidencePath = "evidence.json"

let private basePayload subject tree generatedAfter payloadHash extraProperties =
    "{"
    + "\"schema_version\":1,"
    + "\"tested_subject_commit_oid\":" + JsonSerializer.Serialize subject + ","
    + "\"tested_subject_tree_oid\":" + JsonSerializer.Serialize tree + ","
    + "\"evidence_generated_after_subject\":" + (if generatedAfter then "true" else "false") + ","
    + "\"evidence_payload_sha256\":" + JsonSerializer.Serialize payloadHash + ","
    + "\"evidence_payload_sha256_input_placeholder\":" + JsonSerializer.Serialize Sha256Placeholder
    + extraProperties
    + "}\n"

let private withComputedHash subject tree generatedAfter extraProperties =
    let marker = String.replicate 64 "f"
    let initial = basePayload subject tree generatedAfter marker extraProperties

    let computed =
        match computeActPayloadHash initial with
        | Ok value -> value
        | Error issue -> failtestf "could not hash fixture: %s" (issueToString issue)

    initial.Replace(
        "\"evidence_payload_sha256\":\"" + marker + "\"",
        "\"evidence_payload_sha256\":\"" + computed + "\""
    )

type private EvidenceFixture = {
    Repository: TempGitRepository
    Subject: string
    SubjectTree: string
    Evidence: string
}

let private createSimpleFixture payloadBuilder =
    let repository = new TempGitRepository("evidence-validator")
    repository.Write("README.md", "base\n")
    repository.Commit("base") |> ignore
    repository.Write("implementation.fs", "let authority = true\n")
    let subject = repository.Commit("subject S")
    let tree = repository.Tree subject
    repository.Write(evidencePath, payloadBuilder subject tree)
    let evidence = repository.Commit("evidence E")

    { Repository = repository
      Subject = subject
      SubjectTree = tree
      Evidence = evidence }

let private withSimpleFixture payloadBuilder action =
    let fixture = createSimpleFixture payloadBuilder

    try
        action fixture
    finally
        (fixture.Repository :> IDisposable).Dispose()

let private validPayload subject tree =
    withComputedHash subject tree true ""

let private expectFailure outcome message =
    Expect.isFalse (isPass outcome) message

let private smokeTranscript tests passed failed errored exitCode =
    let names =
        [ "passing runner returns 0"
          "failed runner returns 1"
          "errored runner returns 2"
          "arbitrary non-zero runner returns its exact value (37)"
          "exactly one production runWith definition exists" ]
        |> List.map (fun value -> "  " + value)
        |> String.concat "\n"

    sprintf
        "EXPECTO! %d tests run in 00:00:00.01 for Smoke – %d passed, 0 ignored, %d failed, %d errored. Success!\nPassed:\n%s\nprocess exit code: %d\n"
        tests
        passed
        failed
        errored
        names
        exitCode

let private scanJson tests passed failed errored exitCode =
    sprintf
        "{\"tests\":%d,\"passed\":%d,\"failed\":%d,\"errored\":%d,\"exit_code\":%d}\n"
        tests
        passed
        failed
        errored
        exitCode

let private createSmokeFixture scanTests =
    let repository = new TempGitRepository("evidence-smoke")
    repository.Write("README.md", "base\n")
    repository.Commit("base") |> ignore
    repository.Write("implementation.fs", "let authority = true\n")
    let subject = repository.Commit("subject S")
    let tree = repository.Tree subject
    let transcriptPath = "smoke.txt"
    let scanPath = "scan.json"
    let transcript = smokeTranscript 5 5 0 0 0
    let scan = scanJson scanTests scanTests 0 0 0
    repository.Write(transcriptPath, transcript)
    repository.Write(scanPath, scan)
    let transcriptBlob = repository.BlobOfWorkingFile transcriptPath
    let scanBlob = repository.BlobOfWorkingFile scanPath
    let transcriptHash = sha256OfUtf8 transcript
    let scanHash = sha256OfUtf8 scan

    let smoke =
        ",\"direct\":{\"hermetic\":{"
        + "\"expecto_summary\":{\"tests\":5,\"passed\":5,\"failed\":0,\"errored\":0},"
        + "\"exit_code\":0,"
        + "\"transcript_path\":" + JsonSerializer.Serialize transcriptPath + ","
        + "\"transcript_blob_oid\":" + JsonSerializer.Serialize transcriptBlob + ","
        + "\"output_sha256\":" + JsonSerializer.Serialize transcriptHash + ","
        + "\"scan_path\":" + JsonSerializer.Serialize scanPath + ","
        + "\"scan_blob_oid\":" + JsonSerializer.Serialize scanBlob + ","
        + "\"scan_sha256\":" + JsonSerializer.Serialize scanHash
        + "}}"

    repository.Write(evidencePath, withComputedHash subject tree true smoke)
    let evidence = repository.Commit("evidence E")

    { Repository = repository
      Subject = subject
      SubjectTree = tree
      Evidence = evidence }

[<Tests>]
let tests =
    testList
        "PostgresTestRunnerAuthorities.EvidenceValidator"
        [ test "valid earlier subject and exact evidence bytes pass" {
              let fixture = createSmokeFixture 5

              try
                  let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                  Expect.isTrue (isPass outcome) (sprintf "valid outcome failed: %A %A" outcome.Issues outcome.OperationalFailure)
                  Expect.isTrue outcome.Proof.EvidenceCommitExists "E exists"
                  Expect.isTrue outcome.Proof.EvidencePathExists "E:path exists"
                  Expect.isTrue outcome.Proof.WorkingBytesEqualEvidenceBlob "working bytes equal E blob"
                  Expect.isTrue outcome.Proof.SubjectCommitExists "S exists"
                  Expect.isTrue outcome.Proof.SubjectTreeMatches "S tree matches"
                  Expect.isTrue outcome.Proof.SubjectIsAncestorOfEvidence "S ancestor E"
                  Expect.isTrue outcome.Proof.SubjectDiffersFromEvidence "S differs E"
                  Expect.equal outcome.Proof.TranscriptSummaryMatches (Some true) "payload/transcript agree"
                  Expect.equal outcome.Proof.TranscriptAndScanMatch (Some true) "transcript/scan agree"
              finally
                  (fixture.Repository :> IDisposable).Dispose()
          }

          test "subject equal to evidence commit fails" {
              withSimpleFixture
                  validPayload
                  (fun fixture ->
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Evidence fixture.Evidence
                      expectFailure outcome "S=E rejected"
                      Expect.isFalse outcome.Proof.SubjectDiffersFromEvidence "strict inequality proven false")
          }

          test "missing evidence commit is operational failure" {
              withSimpleFixture
                  validPayload
                  (fun fixture ->
                      let missing = String.replicate 40 "0"
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Subject missing
                      expectFailure outcome "missing E rejected"
                      Expect.isSome outcome.OperationalFailure "Git resolution failure is operational")
          }

          test "missing path in evidence commit fails" {
              withSimpleFixture
                  validPayload
                  (fun fixture ->
                      let outcome = validate fixture.Repository.Path "missing.json" fixture.Subject fixture.Evidence
                      expectFailure outcome "missing E:path rejected"
                      Expect.isSome outcome.OperationalFailure "missing path cannot PASS")
          }

          test "working bytes differing from committed blob fail" {
              withSimpleFixture
                  validPayload
                  (fun fixture ->
                      fixture.Repository.Write(evidencePath, "{}\n")
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                      expectFailure outcome "working mutation rejected"
                      Expect.isFalse outcome.Proof.WorkingBytesEqualEvidenceBlob "byte mismatch proven")
          }

          test "wrong subject tree fails" {
              withSimpleFixture
                  (fun subject _tree -> withComputedHash subject (String.replicate 40 "a") true "")
                  (fun fixture ->
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                      expectFailure outcome "wrong subject tree rejected"
                      Expect.isFalse outcome.Proof.SubjectTreeMatches "tree mismatch proven")
          }

          test "non-ancestor subject fails" {
              use repository = new TempGitRepository("evidence-nonancestor")
              repository.Write("README.md", "base\n")
              let baseCommit = repository.Commit("base")
              repository.Run([ "checkout"; "-q"; "-b"; "subject-branch" ]) |> ignore
              repository.Write("subject.fs", "let subject = true\n")
              let subject = repository.Commit("subject S")
              let tree = repository.Tree subject
              repository.Run([ "checkout"; "-q"; "main" ]) |> ignore
              Expect.equal repository.Head baseCommit "returned to base branch"
              repository.Write(evidencePath, validPayload subject tree)
              let evidence = repository.Commit("unrelated evidence E")
              let outcome = validate repository.Path evidencePath subject evidence
              expectFailure outcome "non-ancestor S rejected"
              Expect.isFalse outcome.Proof.SubjectIsAncestorOfEvidence "ancestry false"
          }

          test "payload mutation with well-formed wrong hash fails" {
              withSimpleFixture
                  (fun subject tree -> basePayload subject tree true (String.replicate 64 "a") ",\"mutated\":true")
                  (fun fixture ->
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                      expectFailure outcome "payload hash mutation rejected"
                      Expect.isFalse outcome.Proof.PayloadHashMatches "payload mutation detected")
          }

          test "malformed payload hash fails" {
              withSimpleFixture
                  (fun subject tree -> basePayload subject tree true "not-a-hash" "")
                  (fun fixture ->
                      let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                      expectFailure outcome "malformed hash rejected"

                      Expect.isTrue
                          (outcome.Issues |> List.exists (function | InvalidSha256 _ -> true | _ -> false))
                          "malformed hash issue surfaced")
          }

          test "bounded Git operational failure can never yield PASS" {
              let dependencies =
                  { RunGit = fun _ _ -> Error "synthetic bounded Git failure"
                    ReadWorkingBytes = fun _ -> Ok(Encoding.UTF8.GetBytes("{}\n")) }

              let outcome =
                  validateWithDependencies
                      dependencies
                      "/tmp"
                      evidencePath
                      (String.replicate 40 "a")
                      (String.replicate 40 "b")

              expectFailure outcome "bounded Git failure rejected"
              Expect.isSome outcome.OperationalFailure "operational failure retained"
          }

          test "transcript and JSON scan mismatch is rejected" {
              let fixture = createSmokeFixture 4

              try
                  let outcome = validate fixture.Repository.Path evidencePath fixture.Subject fixture.Evidence
                  expectFailure outcome "transcript/scan mismatch rejected"
                  Expect.equal outcome.Proof.TranscriptSummaryMatches (Some true) "payload still matches transcript"
                  Expect.equal outcome.Proof.TranscriptAndScanMatch (Some false) "scan mismatch detected"
              finally
                  (fixture.Repository :> IDisposable).Dispose()
          } ]
