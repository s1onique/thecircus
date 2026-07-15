module Circus.Application.Tests.Program

open Expecto

[<EntryPoint>]
let main (args: string[]) =
    let allTests =
        testList
            "Circus.Application.Tests"
            [ Circus.Application.Tests.JournalDecisionTests.tests
              Circus.Application.Tests.RunProjectionTests.tests
              Circus.Application.Tests.ProjectionDecodingTests.tests
              Circus.Application.Tests.RetryPolicyTests.tests ]

    Tests.runTestsWithCLIArgs [||] args allTests
