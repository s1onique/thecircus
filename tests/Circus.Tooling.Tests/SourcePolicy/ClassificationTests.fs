module Circus.Tooling.Tests.SourcePolicy.ClassificationTests

open Expecto
open Circus.Tooling.SourcePolicy.Language

[<Tests>]
let tests =
    testList "Approved and forbidden language classification" [
        test ".fs is F# approved" {
            Expect.isTrue (isApprovedFSharpExtension ".fs") "fs"
            Expect.isTrue (isApprovedFSharpExtension ".fsi") "fsi"
            Expect.isTrue (isApprovedFSharpExtension ".fsproj") "fsproj"
        }
        test ".elm is Elm approved" {
            Expect.isTrue (isApprovedElmExtension ".elm") "elm"
        }
        test ".sh is shell approved" {
            Expect.isTrue (isApprovedShellExtension ".sh") "sh"
        }
        test "Python is forbidden" {
            Expect.isTrue (isForbiddenExtension ".py") "py"
            Expect.isTrue (isForbiddenExtension ".pyw") "pyw"
        }
        test "Go is forbidden" {
            Expect.isTrue (isForbiddenExtension ".go") "go"
            Expect.isTrue (isForbiddenGoModuleFile "go.mod") "go.mod"
            Expect.isTrue (isForbiddenGoModuleFile "go.sum") "go.sum"
        }
        test "TypeScript/JavaScript forbidden" {
            Expect.isTrue (isForbiddenExtension ".ts") "ts"
            Expect.isTrue (isForbiddenExtension ".tsx") "tsx"
            Expect.isTrue (isForbiddenExtension ".js") "js"
        }
        test "Haskell/OCaml forbidden" {
            Expect.isTrue (isForbiddenExtension ".hs") "hs"
            Expect.isTrue (isForbiddenExtension ".ml") "ml"
            Expect.isTrue (isForbiddenExtension ".lhs") "lhs"
        }
    ]
