module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.TransitionTests

open Expecto
open Circus.Tooling.FSharpDiagnostics.Domain
open Circus.Tooling.FSharpDiagnostics.OccurrenceIdentity
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Domain
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Transitions

let private mkOcc (severity: DiagnosticSeverity) (message: string) (path: string) : DiagnosticOccurrence =
    { SchemaVersion = OccurrenceSchemaVersion
      ExtractorVersion = "test-v1"
      CaptureId = "cap"
      SourceKind = LegacyText
      EventOrdinal = 1L
      Severity = severity
      Subcategory = None
      Code = Some "FS0001"
      MessageRaw = message
      MessageNormalized = message
      LocationKind = Source
      SourcePath = Some path
      ProjectPath = None
      Span = { StartLine = Some 1; StartColumn = Some 1; EndLine = Some 1; EndColumn = Some 10 }
      SenderName = None
      EventTimestamp = None
      BuildContext = None
      LegacySourceLineStart = Some 1
      LegacySourceLineEnd = Some 1 }

let private emptySpan : SourceSpan =
    { StartLine = None; StartColumn = None; EndLine = None; EndColumn = None }

let private compat = compatible

[<Tests>]
let tests =
    testList
        "FSharpDiagnostics.RepairEpisodes.Transitions"
        [ test "classifyExactTransition classifies every case" {
              Expect.equal (classifyExactTransition 1 1) PersistedSameCount "persisted same"
              Expect.equal (classifyExactTransition 3 2) PersistedCountDecreased "persisted dec"
              Expect.equal (classifyExactTransition 2 3) PersistedCountIncreased "persisted inc"
              Expect.equal (classifyExactTransition 1 0) EliminatedAfter "eliminated"
              Expect.equal (classifyExactTransition 0 1) IntroducedAfter "introduced"
          }
          test "buildTransitions classifies all five transition kinds" {
              let a1 = mkOcc Warning "msg-a" "src/Foo.fs"
              // msg-a: 1 → 0 = EliminatedAfter
              // msg-b: 1 → 1 = PersistedSameCount
              // msg-c: 3 → 2 = PersistedCountDecreased
              // msg-d: 2 → 4 = PersistedCountIncreased
              // msg-e: 0 → 1 = IntroducedAfter
              let before = [
                  { a1 with MessageNormalized = "msg-a" }
                  { a1 with MessageNormalized = "msg-b" }
                  { a1 with MessageNormalized = "msg-c" }
                  { a1 with MessageNormalized = "msg-c" }
                  { a1 with MessageNormalized = "msg-c" }
                  { a1 with MessageNormalized = "msg-d" }
                  { a1 with MessageNormalized = "msg-d" }
              ]
              let after = [
                  { a1 with MessageNormalized = "msg-b" }
                  { a1 with MessageNormalized = "msg-c" }
                  { a1 with MessageNormalized = "msg-c" }
                  { a1 with MessageNormalized = "msg-d" }
                  { a1 with MessageNormalized = "msg-d" }
                  { a1 with MessageNormalized = "msg-d" }
                  { a1 with MessageNormalized = "msg-d" }
                  { a1 with MessageNormalized = "msg-e" }
              ]
              let entries : GitChangeEntry list = []
              let result = buildTransitions "ep1" compat entries [ "src/Foo.fs" ] before after
              let counts = result.Counts
              Expect.equal counts.PersistedSameCount 1 "1 same"
              Expect.equal counts.PersistedCountDecreased 1 "1 decreased"
              Expect.equal counts.PersistedCountIncreased 1 "1 increased"
              Expect.equal counts.EliminatedAfter 1 "1 eliminated"
              Expect.equal counts.IntroducedAfter 1 "1 introduced"
              Expect.equal (List.length result.Transitions) 5 "5 distinct fingerprints"
          }
          test "source link classifies source modified when path is in changes" {
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "100644"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = Some (System.String('b', 40))
                    ChangeKind = Modified
                    CanonicalPath = "src/Foo.fs" }
              let link = linkSourceChange [ entry ] [] (Some "src/Foo.fs") None
              match link.Kind with
              | SourceFileModified _ -> ()
              | _ -> failwithf "expected SourceFileModified, got %A" link.Kind
          }
          test "source link classifies source deleted" {
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "000000"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = None
                    ChangeKind = Deleted
                    CanonicalPath = "src/Foo.fs" }
              let link = linkSourceChange [ entry ] [] (Some "src/Foo.fs") None
              match link.Kind with
              | SourceFileDeleted _ -> ()
              | _ -> failwithf "expected SourceFileDeleted, got %A" link.Kind
          }
          test "same coordinates with different messages are different transitions" {
              let a1 = mkOcc Warning "msg-a" "src/Foo.fs"
              let a2 = mkOcc Warning "msg-b" "src/Foo.fs"
              let entries : GitChangeEntry list = []
              let result = buildTransitions "ep1" compat entries [] [ a1 ] [ a2 ]
              Expect.equal (List.length result.Transitions) 2 "two distinct transitions"
              let fps = result.Transitions |> List.map (fun t -> t.ExactFingerprint) |> Set.ofList
              Expect.equal (Set.count fps) 2 "distinct fingerprints"
          }
          test "same code with different messages are different transitions" {
              let a1 = mkOcc Warning "msg-a" "src/Foo.fs"
              let a2 = mkOcc Warning "msg-b" "src/Foo.fs"
              let entries : GitChangeEntry list = []
              let result = buildTransitions "ep1" compat entries [] [ a1; a1 ] [ a2 ]
              Expect.equal (List.length result.Transitions) 2 "two transitions"
          }
          test "occurrence order does not change transition output" {
              let a1 = mkOcc Warning "msg-a" "src/Foo.fs"
              let a2 = mkOcc Warning "msg-b" "src/Foo.fs"
              let before1 = [ a1; a2 ]
              let before2 = [ a2; a1 ]
              let after = [ a1; a2 ]
              let entries : GitChangeEntry list = []
              let r1 = buildTransitions "ep1" compat entries [] before1 after
              let r2 = buildTransitions "ep1" compat entries [] before2 after
              Expect.equal r1.Transitions r2.Transitions "transitions are order-independent"
          }
          test "deleted source file makes assessment EliminatedBySourceRemoval" {
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "000000"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = None
                    ChangeKind = Deleted
                    CanonicalPath = "src/Foo.fs" }
              let occ = mkOcc Warning "msg" "src/Foo.fs"
              let before = [ occ ]
              let after = [] : DiagnosticOccurrence list
              let result = buildTransitions "ep1" compat [ entry ] [] before after
              let trans = List.head result.Transitions
              Expect.equal trans.Assessment EliminatedBySourceRemoval "source deletion"
          }
          test "modified source file with eliminated diagnostic gives ObservedResolutionCandidate" {
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "100644"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = Some (System.String('b', 40))
                    ChangeKind = Modified
                    CanonicalPath = "src/Foo.fs" }
              let occ = mkOcc Warning "msg" "src/Foo.fs"
              let before = [ occ ]
              let after = [] : DiagnosticOccurrence list
              let result = buildTransitions "ep1" compat [ entry ] [] before after
              let trans = List.head result.Transitions
              Expect.equal trans.Assessment ObservedResolutionCandidate "resolution candidate"
          }
          test "introduced diagnostic with modified source gives ObservedRegressionCandidate" {
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "100644"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = Some (System.String('b', 40))
                    ChangeKind = Modified
                    CanonicalPath = "src/Foo.fs" }
              let occ = mkOcc Warning "msg" "src/Foo.fs"
              let before = [] : DiagnosticOccurrence list
              let after = [ occ ]
              let result = buildTransitions "ep1" compat [ entry ] [] before after
              let trans = List.head result.Transitions
              Expect.equal trans.Assessment ObservedRegressionCandidate "regression candidate"
          }
          test "incompatible scope makes assessment Unassessable" {
              let occ = mkOcc Warning "msg" "src/Foo.fs"
              let entry : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "100644"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = Some (System.String('b', 40))
                    ChangeKind = Modified
                    CanonicalPath = "src/Foo.fs" }
              let before = [ occ ]
              let after = [] : DiagnosticOccurrence list
              let incompat = incompatible [ "dotnet_sdk_version changed" ]
              let result = buildTransitions "ep1" incompat [ entry ] [] before after
              let trans = List.head result.Transitions
              Expect.equal trans.Assessment Unassessable "unassessable"
          }
          test "delete/add pair is not considered a rename by source link" {
              let old : GitChangeEntry =
                  { BeforeMode = "100644"
                    AfterMode = "000000"
                    BeforeBlobOid = Some (System.String('a', 40))
                    AfterBlobOid = None
                    ChangeKind = Deleted
                    CanonicalPath = "old.fs" }
              let newE : GitChangeEntry =
                  { BeforeMode = "000000"
                    AfterMode = "100644"
                    BeforeBlobOid = None
                    AfterBlobOid = Some (System.String('b', 40))
                    ChangeKind = Added
                    CanonicalPath = "new.fs" }
              // Link to "old.fs" -- it should be SourceFileDeleted, NOT a rename.
              let link = linkSourceChange [ old; newE ] [] (Some "old.fs") None
              match link.Kind with
              | SourceFileDeleted _ -> ()
              | _ -> failwithf "expected SourceFileDeleted for old.fs, got %A" link.Kind
          } ]
