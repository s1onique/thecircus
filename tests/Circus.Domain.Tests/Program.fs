module Circus.Domain.Tests.Program

open Expecto
open Circus.Domain.Tests.ProductIdentityTests

[<EntryPoint>]
let main (args: string[]) =
    Tests.runTestsWithCLIArgs [||] args ProductIdentityTests.tests
