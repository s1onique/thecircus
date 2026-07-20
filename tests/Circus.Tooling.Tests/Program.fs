module Circus.Tooling.Tests.Program

open Expecto

[<EntryPoint>]
let main (arguments: string[]) : int =
    // ``runTestsInAssemblyWithCLIArgs`` discovers every ``[<Tests>]``
    // binding in the executing assembly and runs them as one suite.
    // This avoids depending on the version-specific shape of
    // ``runTestsWithCLIArgs`` (which takes a manually-supplied
    // ``Test`` value and is sensitive to the FocusState API changes
    // in Expecto 11.x).
    let assembly = System.Reflection.Assembly.GetExecutingAssembly()
    Expecto.Tests.runTestsInAssemblyWithCLIArgs [||] arguments |> ignore
    let _ = assembly
    0
