module Circus.DevHost.ProcessRunner

open System
open System.Diagnostics
open System.IO

open Domain

type ProcessSpec =
    { FileName: string
      Arguments: string list
      WorkingDirectory: string
      Environment: Map<string, string>
      Timeout: TimeSpan
      StandardInput: string option
      RedactedArguments: string list option }

type ProcessResult =
    { ExitCode: int
      StandardOutput: string
      StandardError: string
      Duration: TimeSpan }

type IProcessRunner =
    abstract Run: ProcessSpec -> Async<Result<ProcessResult, DevHostFailure>>

/// Starts a process directly, allowing the operating system to resolve bare
/// executable names through PATH. Output streams are drained concurrently to
/// avoid pipe-buffer deadlocks.
type RealProcessRunner() =
    interface IProcessRunner with
        member _.Run(spec: ProcessSpec) =
            async {
                try
                    let startInfo = ProcessStartInfo()
                    startInfo.FileName <- spec.FileName
                    startInfo.UseShellExecute <- false
                    startInfo.RedirectStandardOutput <- true
                    startInfo.RedirectStandardError <- true
                    startInfo.RedirectStandardInput <- spec.StandardInput.IsSome
                    startInfo.WorkingDirectory <- spec.WorkingDirectory

                    for argument in spec.Arguments do
                        startInfo.ArgumentList.Add argument

                    for pair in spec.Environment do
                        startInfo.Environment.[pair.Key] <- pair.Value

                    use child = new Process()
                    child.StartInfo <- startInfo
                    let stopwatch = Stopwatch.StartNew()

                    if not (child.Start()) then
                        return Error(ProcessStartFailure(spec.FileName, "Process.Start returned false"))
                    else
                        let stdoutTask = child.StandardOutput.ReadToEndAsync()
                        let stderrTask = child.StandardError.ReadToEndAsync()

                        match spec.StandardInput with
                        | Some input ->
                            child.StandardInput.Write input
                            child.StandardInput.Flush()
                            child.StandardInput.Close()
                        | None -> ()

                        let timeoutMilliseconds =
                            if spec.Timeout = System.Threading.Timeout.InfiniteTimeSpan then
                                -1
                            else
                                spec.Timeout.TotalMilliseconds |> max 0.0 |> min (float Int32.MaxValue) |> int

                        if not (child.WaitForExit timeoutMilliseconds) then
                            try
                                child.Kill true
                            with _ ->
                                ()

                            try
                                child.WaitForExit()
                            with _ ->
                                ()

                            return Error(ProcessExitFailure(spec.FileName, -1, "timeout"))
                        else
                            let standardOutput = stdoutTask.GetAwaiter().GetResult().TrimEnd()
                            let standardError = stderrTask.GetAwaiter().GetResult().TrimEnd()
                            stopwatch.Stop()

                            let result =
                                { ExitCode = child.ExitCode
                                  StandardOutput = standardOutput
                                  StandardError = standardError
                                  Duration = stopwatch.Elapsed }

                            if result.ExitCode = 0 then
                                return Ok result
                            else
                                return Error(ProcessExitFailure(spec.FileName, result.ExitCode, result.StandardError))
                with ex ->
                    return Error(ProcessStartFailure(spec.FileName, ex.Message))
            }

let runSync (runner: IProcessRunner) (spec: ProcessSpec) : Result<ProcessResult, DevHostFailure> =
    Async.RunSynchronously(runner.Run spec)

type SpecBuilder(filename: string) as this =
    let mutable workingDirectory = Directory.GetCurrentDirectory()
    let mutable arguments: string list = []
    let mutable environment: Map<string, string> = Map.empty
    let mutable timeout = TimeSpan.FromMinutes 30.0
    let mutable standardInput: string option = None

    member _.WithArguments(values: string list) =
        arguments <- values
        this

    member _.WithArgument(value: string) =
        arguments <- arguments @ [ value ]
        this

    member _.WithWorkingDirectory(value: string) =
        workingDirectory <- value
        this

    member _.WithEnvironment(key: string, value: string) =
        environment <- Map.add key value environment
        this

    member _.WithTimeout(value: TimeSpan) =
        timeout <- value
        this

    member _.WithStandardInput(value: string) =
        standardInput <- Some value
        this

    member _.Build() : ProcessSpec =
        { FileName = filename
          Arguments = arguments
          WorkingDirectory = workingDirectory
          Environment = environment
          Timeout = timeout
          StandardInput = standardInput
          RedactedArguments = None }

let spec (filename: string) : SpecBuilder = SpecBuilder filename

let mkSpec
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    (environment: Map<string, string>)
    (timeout: TimeSpan)
    (standardInput: string option)
    : ProcessSpec =
    { FileName = fileName
      Arguments = arguments
      WorkingDirectory = workingDirectory
      Environment = environment
      Timeout = timeout
      StandardInput = standardInput
      RedactedArguments = None }
