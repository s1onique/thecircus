module Circus.DevHost.Tests.Program

open Expecto

[<EntryPoint>]
let main (arguments: string[]) =
    testList
        "Circus.DevHost.Tests"
        [ Circus.DevHost.Tests.DomainTests.tests
          Circus.DevHost.Tests.CliTests.tests
          Circus.DevHost.Tests.IntegrityTests.tests
          Circus.DevHost.Tests.DownloadsTests.tests
          Circus.DevHost.Tests.ArchivesTests.tests
          Circus.DevHost.Tests.ShellProfileTests.tests
          Circus.DevHost.Tests.LauncherPolicyTests.tests ]
    |> Tests.runTestsWithCLIArgs [||] arguments
