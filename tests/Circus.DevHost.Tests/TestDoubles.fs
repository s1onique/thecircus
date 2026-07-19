module Circus.DevHost.Tests.TestDoubles

open System
open System.IO

open Circus.DevHost.Domain
open Circus.DevHost.ProcessRunner

type StubProcessRunner(handler: ProcessSpec -> Result<ProcessResult, DevHostFailure>) =
    interface IProcessRunner with
        member _.Run specification = async.Return(handler specification)

let processResult (standardOutput: string) : ProcessResult =
    { ExitCode = 0
      StandardOutput = standardOutput
      StandardError = ""
      Duration = TimeSpan.Zero }

type TempDirectory() =
    let path =
        Path.Combine(Path.GetTempPath(), "circus-dev-tests-" + Guid.NewGuid().ToString("n"))

    do Directory.CreateDirectory path |> ignore

    member _.Path = path

    interface IDisposable with
        member _.Dispose() =
            try
                if Directory.Exists path then
                    Directory.Delete(path, true)
            with _ ->
                ()
