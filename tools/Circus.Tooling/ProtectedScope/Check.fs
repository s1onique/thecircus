module Circus.Tooling.ProtectedScope.Check

// Pure categorisation over a strict, Git-bound ScopeAuthority declaration.

open Circus.Tooling.ScopeAuthority.Domain
open Circus.Tooling.ProtectedScope.Domain

let categorizePath (declaration: ScopeDeclaration) path =
    let repositoryProtected =
        RepositoryProtectedProductionAndMigrationRoots
        |> List.exists (fun pattern -> patternMatches pattern path)

    if repositoryProtected
       || declaration.GloballyProtected |> List.exists (fun pattern -> patternMatches pattern path) then
        GloballyProtected path
    elif declaration.ActOwned |> List.exists (fun pattern -> patternMatches pattern path) then
        ActOwned path
    else
        Undeclared path

let categorize (binding: ScopeBinding) changedPaths =
    let globallyProtected, actOwned, undeclared =
        (([], [], []), changedPaths)
        ||> List.fold (fun (protectedAcc, ownedAcc, undeclaredAcc) path ->
            match categorizePath binding.Declaration path with
            | GloballyProtected value -> value :: protectedAcc, ownedAcc, undeclaredAcc
            | ActOwned value -> protectedAcc, value :: ownedAcc, undeclaredAcc
            | Undeclared value -> protectedAcc, ownedAcc, value :: undeclaredAcc)

    { EvaluatedCommitOid = binding.EvaluatedCommitOid
      DeclarationPath = binding.DeclarationPath
      DeclarationBlobOid = binding.DeclarationBlobOid
      PointerBlobOid = binding.PointerBlobOid
      ActId = binding.ActId
      BaselineCommitOid = binding.BaselineCommitOid
      GloballyProtectedChanges = List.sort globallyProtected
      ActOwnedChanges = List.sort actOwned
      UndeclaredChanges = List.sort undeclared
      Authorisations =
        binding.Declaration.RejectUndeclaredChanges
        && binding.Declaration.DoNotAuthorizeProductionOrMigrationPaths }
