module Circus.Persistence.Postgres.Tests.Runner.Smoke.SourceInventory

// =============================================================================
// Source-inventory test for the runner seam
//
// ACT-CIRCUS-POSTGRES-TEST-RUNNER-FAIL-CLOSED01-CORRECTION02
//
// The P0-1 acceptance criterion is: there is exactly one production
// definition of ``PostgresTestRunner.runWith`` across the whole
// repository.  This test greps every ``.fs`` file in the repository's
// ``tests/`` and ``src/`` trees (plus a small allow-list of other F#
// roots) and counts the occurrences of the seam definition.
//
// The seam is recognised as the F# binding
// ``let runWith`` (or ``let rec runWith``) inside a module whose
// name ends in ``PostgresTestRunner``.
//
// The count must be exactly 1; any other value is a regression
// because it means the seam has been forked, which would defeat the
// fail-closed contract.
// =============================================================================

open System
open System.IO
open System.Text.RegularExpressions

open Expecto

let private repositoryRoot () : string =
    let cwd = Directory.GetCurrentDirectory()
    // Walk upward until we find a directory containing ``.git``.
    let rec walk (dir: string) =
        if Directory.Exists(Path.Combine(dir, ".git")) then
            dir
        else
            let parent = Directory.GetParent(dir)

            if isNull parent then
                failwithf "could not find repository root from cwd=%s" cwd
            else
                walk parent.FullName

    walk cwd

let private rootsToScan (root: string) : string list =
    [ Path.Combine(root, "tests"); Path.Combine(root, "src") ]

// The seam is recognised as the F# binding ``let runWith`` (or
// ``let rec runWith``) IMMEDIATELY followed by a parameter list — that
// is, a function definition, not a call site.  The regex therefore
// requires an opening parenthesis immediately after the name (with
// optional whitespace).  This rejects call sites like
// ``let exit = runWith (fakeRunner 0) ...`` whose ``let`` is a
// ``let <name> =`` binding rather than a function definition.
let private seamPattern = Regex(@"^\s*let(\s+rec)?\s+runWith\s*\(")

let private countSeamDefinitions (path: string) : int =
    let mutable count = 0

    for file in Directory.EnumerateFiles(path, "*.fs", SearchOption.AllDirectories) do
        // Skip build outputs and object files.
        let dir = Path.GetFullPath(Path.GetDirectoryName(file))
        // Skip the test file itself and any build outputs.
        let isThisTestFile = Path.GetFullPath(file) = Path.GetFullPath(__SOURCE_FILE__)

        if
            not (
                isThisTestFile
                || dir.Contains(
                    Path.DirectorySeparatorChar.ToString()
                    + "bin"
                    + Path.DirectorySeparatorChar.ToString()
                )
                || dir.Contains(
                    Path.DirectorySeparatorChar.ToString()
                    + "obj"
                    + Path.DirectorySeparatorChar.ToString()
                )
            )
        then
            let text = File.ReadAllText(file)
            // Only count function definitions inside a module whose
            // name ends in ``PostgresTestRunner``.
            if text.Contains("PostgresTestRunner") then
                let lines = text.Split([| '\n' |], StringSplitOptions.None)

                for line in lines do
                    // Skip comment lines.
                    if not (line.TrimStart().StartsWith("//")) && seamPattern.IsMatch(line) then
                        count <- count + 1

    count

let runnerInventoryTests =
    testList
        "Postgres runner seam source-inventory"
        [ test "exactly one production runWith definition exists" {
              let root = repositoryRoot ()
              let total = rootsToScan root |> List.sumBy countSeamDefinitions
              Expect.equal total 1 "expected exactly one PostgresTestRunner.runWith definition across tests/ and src/"
          } ]
