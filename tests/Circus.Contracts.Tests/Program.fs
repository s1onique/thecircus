module Circus.Contracts.Tests.Program

open Expecto

[<EntryPoint>]
let main (args: string[]) =
    let suites =
        [
            EnvelopeContractTests.tests
            StartedEventContractTests.tests
            FinishedEventContractTests.tests
            UnknownEventContractTests.tests
            FixtureContractTests.tests
        ]

    Tests.runTestsWithCLIArgs [||] args (testList "Circus.Contracts" suites)
