module Circus.Api.Tests.Program

open Expecto
open Circus.Api.Tests.HttpContractTests
open Circus.Api.Tests.HostLifecycleTests

[<EntryPoint>]
let main (args: string[]) =
    // HostLifecycleTests mutates the process-global CIRCUS_DATABASE_URL
    // environment variable inside each test.  Without sequencing, two
    // tests can race on this global state and observe each other's
    // value.  Wrap the host-lifecycle suite in testSequenced so every
    // test sees a stable environment for its full duration.
    let allTests =
        testList "Circus.Api.Tests" [ HttpContractTests.tests; testSequenced HostLifecycleTests.tests ]

    Tests.runTestsWithCLIArgs [||] args allTests
