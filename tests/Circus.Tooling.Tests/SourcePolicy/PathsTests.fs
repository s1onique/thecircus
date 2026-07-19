module Circus.Tooling.Tests.SourcePolicy.PathsTests

open Expecto
open Circus.Tooling.SourcePolicy.Paths

let tests =
    testList "Path safety" [
        test "absolute path is rejected" {
            Expect.isTrue (isAbsolute "/abs/path") "/abs"
            Expect.isTrue (isAbsolute "/x") "/x"
            Expect.isFalse (isAbsolute "rel/path") "rel"
        }
        test "parent traversal detected" {
            Expect.isTrue (containsParentTraversal "../foo" <> "") ".. foo"
            Expect.isTrue (containsParentTraversal "a/../../b" <> "") "a/../../b"
            Expect.isTrue (containsParentTraversal "a/b/../c" <> "") "trailing"
            Expect.equal (containsParentTraversal "a/b") "" "no traversal"
        }
        test "vendor path detection" {
            Expect.isTrue (isVendoredElmPath "web/node_modules/foo") "node_modules"
            Expect.isTrue (isVendoredElmPath "web/elm-stuff/foo") "elm-stuff"
            Expect.isTrue (isVendoredElmPath "web/elm-stuff") "elm-stuff exact"
            Expect.isTrue (isVendoredElmPath "web/node_modules") "node_modules exact"
            Expect.isFalse (isVendoredElmPath "src/foo") "not vendor"
        }
        test "extension extraction" {
            Expect.equal (extensionOf "foo/bar.sh") ".sh" "sh"
            Expect.equal (extensionOf "foo/bar.FSX") ".fsx" "upper"
            Expect.equal (extensionOf "Makefile") "" "no ext"
            Expect.equal (extensionOf "foo/.hidden") "" "hidden"
        }
        test "POSIX path normalisation" {
            Expect.equal (toPosix "a\\b\\c") "a/b/c" "windows backslash"
            Expect.equal (toPosix "a/b/c") "a/b/c" "already posix"
        }
    ]
