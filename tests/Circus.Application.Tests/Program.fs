module Circus.Application.Tests.Program

open Expecto
open Circus.Application.Tests.JournalDecisionTests
open Circus.Application.Tests.RunProjectionTests

[<EntryPoint>]
let main (args: string[]) =
    let allTests =
        testList
            "Circus.Application.Tests"
            [ JournalDecisionTests.tests
              RunProjectionTests.tests ]

    Tests.runTestsWithCLIArgs [||] args allTests
