module Circus.Tooling.ScopeAuthority.Authority

// =============================================================================
// Active ACT scope authority -- bounded Git binding
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION03
//
// For an evaluated commit H this module proves, from committed Git objects:
// H exists as a commit; H:.factory/active-scope.json exists; the declaration
// path resolves to the pointer's exact blob; ACT ID and baseline agree; and the
// baseline is an ancestor of H.  A CLI declaration/baseline is only an
// additional consistency assertion -- it can never replace the tracked pointer.
// =============================================================================

open System
open System.Text

open Circus.Tooling.FSharpDiagnostics.RepairEpisodes.Git
open Circus.Tooling.ScopeAuthority.Domain

type GitBytesResult =
    { ExitCode: int
      Stdout: byte array
      Stderr: byte array }

type ScopeAuthorityDependencies =
    { RunGit: string -> string list -> Result<GitBytesResult, string> }

let private diagnosticText (bytes: byte array) = Encoding.UTF8.GetString(bytes).Trim()

let productionDependencies () =
    { RunGit =
        fun repoRoot arguments ->
            match runGitBytesTyped repoRoot defaultGitRunOptions arguments with
            | Error error -> Error(sprintf "%A" error)
            | Ok result ->
                Ok
                    { ExitCode = result.ExitCode
                      Stdout = result.Stdout
                      Stderr = result.Stderr } }

exception private AuthorityException of ScopeAuthorityError

let private fail error = raise (AuthorityException error)

let private run deps repoRoot operation arguments =
    match deps.RunGit repoRoot arguments with
    | Error detail -> fail (GitOperationFailed(operation, detail))
    | Ok result -> result

let private runRequired deps repoRoot operation missingKind missingValue arguments =
    let result = run deps repoRoot operation arguments

    if result.ExitCode <> 0 then
        let stderr = diagnosticText result.Stderr

        fail (
            GitObjectMissing(
                missingKind,
                if String.IsNullOrEmpty stderr then
                    missingValue
                else
                    missingValue + " (" + stderr + ")"
            )
        )

    result

let private strictUtf8 (path: string) (bytes: byte array) =
    try
        let encoding = UTF8Encoding(false, true)
        encoding.GetString bytes
    with :? DecoderFallbackException as error ->
        fail (InvalidUtf8Blob(path, error.Message))

let private oidFromResult (operation: string) (result: GitBytesResult) =
    let value = strictUtf8 operation result.Stdout |> fun text -> text.Trim()

    if not (isAsciiHexOid value) then
        fail (GitOperationFailed(operation, sprintf "expected one full ASCII hexadecimal OID, got %s" value))

    value

let private equalOid left right =
    String.Equals(left, right, StringComparison.OrdinalIgnoreCase)

let private resolveCommit deps repoRoot reference =
    let result =
        runRequired
            deps
            repoRoot
            "rev-parse-commit"
            "commit"
            reference
            [ "rev-parse"; "--verify"; "--end-of-options"; reference + "^{commit}" ]

    oidFromResult "rev-parse-commit" result

let private resolveTree deps repoRoot commitOid =
    let result =
        runRequired
            deps
            repoRoot
            "rev-parse-tree"
            "tree"
            commitOid
            [ "rev-parse"; "--verify"; "--end-of-options"; commitOid + "^{tree}" ]

    oidFromResult "rev-parse-tree" result

let private resolvePathBlob deps repoRoot commitOid path =
    let spec = commitOid + ":" + path

    let result =
        runRequired
            deps
            repoRoot
            ("rev-parse-path:" + path)
            "path"
            spec
            [ "rev-parse"; "--verify"; "--end-of-options"; spec ]

    oidFromResult ("rev-parse-path:" + path) result

let private readBlob deps repoRoot path blobOid =
    let result =
        runRequired deps repoRoot ("cat-file-blob:" + path) "blob" blobOid [ "cat-file"; "blob"; blobOid ]

    strictUtf8 path result.Stdout

let private requireSame field pointerValue declarationValue =
    if not (String.Equals(pointerValue, declarationValue, StringComparison.Ordinal)) then
        fail (PointerDeclarationMismatch(field, pointerValue, declarationValue))

let private requireSameOid field pointerValue declarationValue =
    if not (equalOid pointerValue declarationValue) then
        fail (PointerDeclarationMismatch(field, pointerValue, declarationValue))

let resolveCommitReferenceWith deps repoRoot reference =
    try
        Ok(resolveCommit deps repoRoot reference)
    with AuthorityException error ->
        Error error

let resolveCommitReference repoRoot reference =
    resolveCommitReferenceWith (productionDependencies ()) repoRoot reference

let resolveWith
    (deps: ScopeAuthorityDependencies)
    (repoRoot: string)
    (evaluatedCommitOid: string)
    (cliDeclarationPath: string option)
    (cliBaselineCommitOid: string option)
    : Result<ScopeBinding, ScopeAuthorityError> =
    try
        if not (isAsciiHexOid evaluatedCommitOid) then
            fail (InvalidOid("scope-binding", "evaluated_commit_oid", evaluatedCommitOid))

        let resolvedCommit = resolveCommit deps repoRoot evaluatedCommitOid

        if not (equalOid resolvedCommit evaluatedCommitOid) then
            fail (GitObjectIdentityMismatch("evaluated commit", evaluatedCommitOid, resolvedCommit))

        let evaluatedTree = resolveTree deps repoRoot resolvedCommit

        let pointerBlob =
            resolvePathBlob deps repoRoot resolvedCommit ActiveScopePointerPath

        let pointerRaw = readBlob deps repoRoot ActiveScopePointerPath pointerBlob

        let pointer: ActiveScopePointer =
            match parseActiveScopePointer pointerRaw with
            | Ok value -> value
            | Error error -> fail error

        match cliDeclarationPath with
        | Some supplied ->
            match validateRepositoryPath false supplied with
            | Error detail -> fail (InvalidRepositoryPath("CLI", "scope_declaration", supplied, detail))
            | Ok() ->
                if not (String.Equals(supplied, pointer.DeclarationPath, StringComparison.Ordinal)) then
                    fail (CliPointerDisagreement("declaration_path", supplied, pointer.DeclarationPath))
        | None -> ()

        match cliBaselineCommitOid with
        | Some supplied ->
            if not (isAsciiHexOid supplied) then
                fail (InvalidOid("CLI", "baseline_commit_oid", supplied))

            if not (equalOid supplied pointer.BaselineCommitOid) then
                fail (CliPointerDisagreement("baseline_commit_oid", supplied, pointer.BaselineCommitOid))
        | None -> ()

        let declarationBlob =
            resolvePathBlob deps repoRoot resolvedCommit pointer.DeclarationPath

        if not (equalOid declarationBlob pointer.DeclarationBlobOid) then
            fail (GitObjectIdentityMismatch("declaration blob", pointer.DeclarationBlobOid, declarationBlob))

        let declarationRaw = readBlob deps repoRoot pointer.DeclarationPath declarationBlob

        let declaration: ScopeDeclaration =
            match parseScopeDeclaration declarationRaw with
            | Ok value -> value
            | Error error -> fail error

        requireSame "act_id" pointer.ActId declaration.ActId
        requireSameOid "baseline_commit_oid" pointer.BaselineCommitOid declaration.BaselineCommitOid

        let resolvedBaseline = resolveCommit deps repoRoot pointer.BaselineCommitOid

        if not (equalOid resolvedBaseline pointer.BaselineCommitOid) then
            fail (GitObjectIdentityMismatch("baseline commit", pointer.BaselineCommitOid, resolvedBaseline))

        let ancestry =
            run
                deps
                repoRoot
                "merge-base-is-ancestor"
                [ "merge-base"; "--is-ancestor"; resolvedBaseline; resolvedCommit ]

        match ancestry.ExitCode with
        | 0 -> ()
        | 1 -> fail (GitObjectNotAncestor(resolvedBaseline, resolvedCommit))
        | code ->
            fail (
                GitOperationFailed(
                    "merge-base-is-ancestor",
                    sprintf "exit=%d stderr=%s" code (diagnosticText ancestry.Stderr)
                )
            )

        Ok
            { EvaluatedCommitOid = resolvedCommit
              EvaluatedTreeOid = evaluatedTree
              PointerPath = ActiveScopePointerPath
              PointerBlobOid = pointerBlob
              DeclarationPath = pointer.DeclarationPath
              DeclarationBlobOid = declarationBlob
              BaselineCommitOid = resolvedBaseline
              ActId = pointer.ActId
              Pointer = pointer
              Declaration = declaration }
    with AuthorityException error ->
        Error error

let resolve repoRoot evaluatedCommitOid cliDeclarationPath cliBaselineCommitOid =
    resolveWith (productionDependencies ()) repoRoot evaluatedCommitOid cliDeclarationPath cliBaselineCommitOid
