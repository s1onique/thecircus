module Circus.Tooling.Tests.Program

open Expecto

[<EntryPoint>]
let main (arguments: string[]) : int =
    Tests.runTestsWithCLIArgs [||] arguments
