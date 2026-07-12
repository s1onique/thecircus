module Circus.Api.Tests.Program

open Expecto
open Circus.Api.Tests.HttpContractTests

[<EntryPoint>]
let main (args: string[]) =
    Tests.runTestsWithCLIArgs [||] args HttpContractTests.tests
