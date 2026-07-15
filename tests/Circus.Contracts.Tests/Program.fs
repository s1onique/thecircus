module Circus.Contracts.Tests.Program

open Expecto

[<EntryPoint>]
let main (args: string[]) =
    let suites =
        [ EnvelopeContractTests.bundle
          StartedEventContractTests.bundle
          FinishedEventContractTests.bundle
          UnknownEventContractTests.bundle
          FixtureContractTests.bundle ]

    Tests.runTestsWithCLIArgs [||] args (testList "Circus.Contracts" suites)
