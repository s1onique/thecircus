module Circus.Tooling.Tests.ScopeAuthorityTests

open System
open System.Text.Json
open Expecto

open Circus.Tooling.Tests.AuthorityTestSupport
open Circus.Tooling.ScopeAuthority.Domain
open Circus.Tooling.ScopeAuthority.Authority
open Circus.Tooling.ProtectedScope.Domain
open Circus.Tooling.ProtectedScope.Check

let private actId = "ACT-TEST-SCOPE-AUTHORITY"
let private scopePath = "scope.json"

let private stringArray values =
    JsonSerializer.Serialize(values |> List.toArray)

let private qualificationsJson =
    "["
    + "{\"path\":\"src/Circus.Persistence.Postgres/\","
    + "\"reason\":\"production persistence root is immutable in this ACT\","
    + "\"expected_descendants\":[\"src/Circus.Persistence.Postgres/JournalRepository.fs\"],"
    + "\"sibling_mutation_test\":\"src/Circus.Persistence.Postgres.Tests/undeclared.fs\"},"
    + "{\"path\":\"db/migrations/\","
    + "\"reason\":\"migration history is immutable in this ACT\","
    + "\"expected_descendants\":[\"db/migrations/000001_event_journal.sql\"],"
    + "\"sibling_mutation_test\":\"db/not-migrations/undeclared.sql\"}"
    + "]"

let private declarationJson baseline globallyProtected actOwned rejectUndeclared protectProduction =
    "{"
    + "\"schema_version\":1,"
    + "\"act_id\":" + JsonSerializer.Serialize actId + ","
    + "\"act_classification\":\"P0\","
    + "\"baseline_commit_oid\":" + JsonSerializer.Serialize baseline + ","
    + "\"purpose\":\"strict authority fixture\","
    + "\"globally_protected\":" + stringArray globallyProtected + ","
    + "\"act_owned\":" + stringArray actOwned + ","
    + "\"prefix_qualifications\":" + qualificationsJson + ","
    + "\"reject_undeclared_changes\":" + (if rejectUndeclared then "true" else "false") + ","
    + "\"do_not_authorize_production_or_migration_paths\":" + (if protectProduction then "true" else "false")
    + "}\n"

let private validDeclarationJson baseline owned =
    declarationJson
        baseline
        RepositoryProtectedProductionAndMigrationRoots
        owned
        true
        true

let private pointerJson baseline declarationBlob declarationPath =
    "{"
    + "\"schema_version\":1,"
    + "\"act_id\":" + JsonSerializer.Serialize actId + ","
    + "\"declaration_path\":" + JsonSerializer.Serialize declarationPath + ","
    + "\"declaration_blob_oid\":" + JsonSerializer.Serialize declarationBlob + ","
    + "\"baseline_commit_oid\":" + JsonSerializer.Serialize baseline
    + "}\n"

type private ScopeFixture = {
    Repository: TempGitRepository
    Baseline: string
    Head: string
    DeclarationBlob: string
}

let private createFixture pointerTransform declarationTransform =
    let repository = new TempGitRepository("scope-authority")
    repository.Write("README.md", "baseline\n")
    let baseline = repository.Commit("baseline")
    repository.Write("implementation.txt", "authority implementation\n")

    let owned =
        [ "implementation.txt"; scopePath; ActiveScopePointerPath ]

    let declaration = validDeclarationJson baseline owned |> declarationTransform
    repository.Write(scopePath, declaration)
    let declarationBlob = repository.BlobOfWorkingFile scopePath
    let pointer = pointerJson baseline declarationBlob scopePath |> pointerTransform
    repository.Write(ActiveScopePointerPath, pointer)
    let head = repository.Commit("scope subject")

    { Repository = repository
      Baseline = baseline
      Head = head
      DeclarationBlob = declarationBlob }

let private withFixture pointerTransform declarationTransform action =
    let fixture = createFixture pointerTransform declarationTransform

    try
        action fixture
    finally
        (fixture.Repository :> IDisposable).Dispose()

let private expectError result message =
    match result with
    | Ok value -> failtestf "%s; unexpectedly succeeded: %A" message value
    | Error _ -> ()

let private parseValid baseline owned =
    match parseScopeDeclaration (validDeclarationJson baseline owned) with
    | Ok declaration -> declaration
    | Error error -> failtestf "fixture declaration failed: %s" (errorToString error)

[<Tests>]
let tests =
    testList
        "PostgresTestRunnerAuthorities.ScopeAuthorityProtectedScope"
        [ test "valid pointer and declaration bind to committed Git objects" {
              withFixture
                  id
                  id
                  (fun fixture ->
                      match resolve fixture.Repository.Path fixture.Head None None with
                      | Error error -> failtestf "valid scope failed: %s" (errorToString error)
                      | Ok binding ->
                          Expect.equal binding.EvaluatedCommitOid fixture.Head "H resolved exactly"
                          Expect.equal binding.DeclarationBlobOid fixture.DeclarationBlob "declaration blob bound"
                          Expect.equal binding.ActId actId "ACT IDs agree"
                          Expect.equal binding.BaselineCommitOid fixture.Baseline "baseline bound"
                          Expect.isTrue (binding.PointerBlobOid.Length = 40 || binding.PointerBlobOid.Length = 64) "pointer blob recorded")
          }

          test "wrong declaration blob fails closed" {
              withFixture
                  (fun pointer -> pointer.Replace("\"declaration_blob_oid\":\"", "\"declaration_blob_oid\":\"0000000000000000000000000000000000000000" + "\",\"ignored\":\""))
                  id
                  (fun fixture ->
                      // Use a syntactically valid pointer with a deliberately wrong OID.
                      let raw = pointerJson fixture.Baseline (String.replicate 40 "0") scopePath
                      fixture.Repository.Write(ActiveScopePointerPath, raw)
                      let head = fixture.Repository.Commit("wrong declaration blob")
                      expectError (resolve fixture.Repository.Path head None None) "wrong declaration blob")
          }

          test "pointer and declaration baseline disagreement fails closed" {
              use repository = new TempGitRepository("scope-wrong-baseline")
              repository.Write("README.md", "one\n")
              let first = repository.Commit("first")
              repository.Write("README.md", "two\n")
              let second = repository.Commit("second")
              let owned = [ scopePath; ActiveScopePointerPath ]
              repository.Write(scopePath, validDeclarationJson first owned)
              let blob = repository.BlobOfWorkingFile scopePath
              repository.Write(ActiveScopePointerPath, pointerJson second blob scopePath)
              let head = repository.Commit("mismatch")
              expectError (resolve repository.Path head None None) "wrong baseline"
          }

          test "non-ancestor baseline fails closed" {
              use repository = new TempGitRepository("scope-nonancestor")
              repository.Write("README.md", "base\n")
              let baseCommit = repository.Commit("base")
              repository.Run([ "checkout"; "-q"; "-b"; "side" ]) |> ignore
              repository.Write("side.txt", "side\n")
              let sideCommit = repository.Commit("side")
              repository.Run([ "checkout"; "-q"; "main" ]) |> ignore
              repository.Write("main.txt", "main\n")
              let owned = [ "main.txt"; scopePath; ActiveScopePointerPath ]
              repository.Write(scopePath, validDeclarationJson sideCommit owned)
              let blob = repository.BlobOfWorkingFile scopePath
              repository.Write(ActiveScopePointerPath, pointerJson sideCommit blob scopePath)
              let head = repository.Commit("main scope")
              Expect.notEqual baseCommit sideCommit "fixture branches differ"
              expectError (resolve repository.Path head None None) "non-ancestor baseline"
          }

          test "malformed JSON fails closed" {
              expectError (parseActiveScopePointer "{not-json") "malformed pointer"
          }

          test "duplicate JSON property fails closed" {
              let oid = String.replicate 40 "a"
              let raw =
                  "{\"schema_version\":1,\"schema_version\":1,\"act_id\":\"A\","
                  + "\"declaration_path\":\"scope.json\",\"declaration_blob_oid\":\"" + oid
                  + "\",\"baseline_commit_oid\":\"" + oid + "\"}"
              expectError (parseActiveScopePointer raw) "duplicate property"
          }

          test "non-string array item fails closed" {
              let baseline = String.replicate 40 "a"
              let raw =
                  (validDeclarationJson baseline [ "scope.json" ])
                      .Replace("\"act_owned\":[\"scope.json\"]", "\"act_owned\":[\"scope.json\",7]")
              expectError (parseScopeDeclaration raw) "non-string array item"
          }

          test "non-ASCII-hex OID fails closed" {
              let invalid = String.replicate 39 "a" + "g"
              let raw = pointerJson invalid (String.replicate 40 "b") scopePath
              expectError (parseActiveScopePointer raw) "non-hex OID"
          }

          test "missing mandatory Boolean fails closed" {
              let baseline = String.replicate 40 "a"
              let raw =
                  (validDeclarationJson baseline [ "scope.json" ])
                      .Replace("\"reject_undeclared_changes\":true,", "")
              expectError (parseScopeDeclaration raw) "missing mandatory Boolean"
          }

          test "false mandatory Boolean fails closed" {
              let baseline = String.replicate 40 "a"
              let raw =
                  declarationJson
                      baseline
                      RepositoryProtectedProductionAndMigrationRoots
                      [ "scope.json" ]
                      false
                      true
              expectError (parseScopeDeclaration raw) "false mandatory Boolean"
          }

          test "duplicate path fails closed" {
              let baseline = String.replicate 40 "a"
              let raw = validDeclarationJson baseline [ "scope.json"; "scope.json" ]
              expectError (parseScopeDeclaration raw) "duplicate path"
          }

          test "global and owned overlap fails closed" {
              let baseline = String.replicate 40 "a"
              let raw = validDeclarationJson baseline [ "db/migrations/example.sql" ]
              expectError (parseScopeDeclaration raw) "global/owned overlap"
          }

          test "undeclared sibling remains rejected" {
              let baseline = String.replicate 40 "a"
              let declaration = parseValid baseline [ "owned.txt" ]

              match categorizePath declaration "sibling.txt" with
              | Undeclared path -> Expect.equal path "sibling.txt" "sibling is undeclared"
              | category -> failtestf "expected undeclared sibling, got %A" category
          }

          test "production path cannot be authorized" {
              let baseline = String.replicate 40 "a"
              let raw = validDeclarationJson baseline [ "src/Circus.Persistence.Postgres/JournalSql.fs" ]
              expectError (parseScopeDeclaration raw) "production ownership"
          }

          test "migration path cannot be authorized" {
              let baseline = String.replicate 40 "a"
              let raw = validDeclarationJson baseline [ "db/migrations/999999_forbidden.sql" ]
              expectError (parseScopeDeclaration raw) "migration ownership"
          }

          test "CLI and pointer declaration disagreement fails ambiguous" {
              withFixture
                  id
                  id
                  (fun fixture ->
                      expectError
                          (resolve fixture.Repository.Path fixture.Head (Some "different-scope.json") None)
                          "CLI/pointer disagreement")
          }

          test "missing tracked active scope fails closed even with CLI declaration" {
              use repository = new TempGitRepository("scope-missing-pointer")
              repository.Write("README.md", "baseline\n")
              let baseline = repository.Commit("baseline")
              repository.Write(scopePath, validDeclarationJson baseline [ scopePath ])
              let head = repository.Commit("declaration only")
              expectError (resolve repository.Path head (Some scopePath) None) "missing active scope"
          } ]
