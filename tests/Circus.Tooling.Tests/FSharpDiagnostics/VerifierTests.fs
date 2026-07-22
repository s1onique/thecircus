module Circus.Tooling.Tests.FSharpDiagnostics.VerifierTests

open Expecto
open System.IO
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.Hashing
open Circus.Tooling.FSharpDiagnostics.Manifest
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.Serialization
open Circus.Tooling.FSharpDiagnostics.Verifier
open Circus.Tooling.FSharpDiagnostics.Paths

let private makeRoot () =
    let root =
        Path.Combine(Path.GetTempPath(), "fd-verifier-" + System.Guid.NewGuid().ToString("N"))

    Directory.CreateDirectory root |> ignore
    root

let private cleanup (d: string) =
    if Directory.Exists d then
        try
            Directory.Delete(d, true)
        with _ ->
            ()

let private writeCapture (repoRoot: string) (captureId: string) (rawContent: string) =
    let capDir = Path.Combine(repoRoot, rawSubdir, captureId)
    Directory.CreateDirectory capDir |> ignore
    File.WriteAllText(Path.Combine(capDir, "build.log"), rawContent)

    let manifest =
        { SchemaVersion = CaptureManifestSchemaVersion
          CaptureId = captureId
          CaptureKind = captureKindToken LegacyText
          RawArtifacts = [ "build.log" ]
          Command = Some "dotnet build"
          WorkingDirectory = Some "/home/me/project"
          RepositoryCommitOid = None
          RepositoryTreeOid = None
          WorkingTreeState = None
          SourceRootAliases =
            [ { AbsoluteRoot = "/home/me/project"
                CanonicalRoot = "<REPO>" } ]
          DotnetSdkVersion = None
          MsbuildVersion = None
          FsharpCompilerVersion = None
          OperatingSystem = None
          Architecture = None
          Culture = None
          StartedAt = None
          CompletedAt = None
          ExitCode = Some 1
          MetadataGaps = [] }

    writeCaptureManifest (Path.Combine(capDir, "capture.json")) manifest

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.Verifier"
        [ test "end-to-end pipeline runs on synthetic capture" {
              let root = makeRoot ()

              try
                  writeCapture
                      root
                      "cap-1"
                      "/home/me/project/src/Foo.fs(1,1): warning FS0001: x\n/home/me/project/src/Foo.fs(1,1): error FS0002: y"

                  let summary, outcome, _, _ = runPipeline root None
                  Expect.isTrue outcome.Success "publish succeeded"
                  Expect.equal summary.OccurrenceCount 2 "two occurrences"
                  Expect.equal summary.CapturesTotal 1 "one capture"
                  Expect.equal summary.LegacyTextCaptures 1 "one legacy_text"
              finally
                  cleanup root
          }
          test "two regenerations are byte-identical" {
              let root = makeRoot ()

              try
                  writeCapture root "cap-1" "/home/me/project/src/Foo.fs(1,1): warning FS0001: hello"
                  let normalizedDir = Path.Combine(root, "normalized")
                  // First run
                  let s1, o1, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue o1.Success "first publish succeeded"
                  let a = Path.Combine(normalizedDir, occurrencesFile)
                  let b = Path.Combine(normalizedDir, summaryFile)
                  let ha = sha256OfFile a
                  let hb = sha256OfFile b
                  // Second run
                  let s2, o2, _, _ = runPipeline root (Some normalizedDir)
                  Expect.isTrue o2.Success "second publish succeeded"
                  Expect.equal (sha256OfFile a) ha "occurrences identical"
                  Expect.equal (sha256OfFile b) hb "summary identical"
                  Expect.equal s1.OccurrenceCount s2.OccurrenceCount "counts match"
              finally
                  cleanup root
          }
          test "diagnostic-looking unparsed line in legacy capture fails verification" {
              let root = makeRoot ()

              try
                  writeCapture root "cap-1" "/home/me/project/src/Foo.fs(1,1): warning NoCodeHere msg"
                  let summary, outcome, _, _ = runPipeline root None
                  Expect.isTrue outcome.Success "publish succeeded"
                  Expect.isTrue (summary.DiagnosticLookingUnparsedLines > 0) "unparsed count > 0"
              finally
                  cleanup root
          } ]
