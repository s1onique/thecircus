module Circus.DevHost.ProcessRunner

open System
open System.Diagnostics
open System.IO

open Domain

/// Full specification of a single subprocess invocation.
type ProcessSpec = {
    FileName: string
    Arguments: string list
    WorkingDirectory: string
    Environment: Map<string, string>
    Timeout: TimeSpan
    StandardInput: string option
    RedactedArguments: string list option
}

type ProcessResult = {
    ExitCode: int
    StandardOutput: string
    StandardError: string
    Duration: TimeSpan
}

/// Internal helper that captures a string into a `StringWriter`-like
/// buffer via async callbacks. The .NET `Process.OutputDataReceived`
/// interface uses `DataReceivedEventArgs`, but we redirect the streams
/// ourselves so we can guarantee ordering.
type private BufferRedirector(standardOut: string ref, standardErr: string ref) =
    let writeBuf (r: string ref) (s: string) =
        if not (isNull s) then
            r := (!r) + s + System.Environment.NewLine

    member _.OnStdOut(line: string) = writeBuf standardOut line
    member _.OnStdErr(line: string) = writeBuf standardErr line

/// The adapter interface used by every installer and doctor. Tests provide
/// a fake implementation that returns predetermined results without
/// touching the filesystem.
type IProcessRunner =
    abstract Run : ProcessSpec -> Async<Result<ProcessResult, DevHostFailure>>

/// Default adapter: starts a real `Process` using `ArgumentList` and
/// captures stdout/stderr without invoking a shell.
type RealProcessRunner() =

    interface IProcessRunner with

        member _.Run (spec: ProcessSpec) =

            async {
                if not (File.Exists spec.FileName) then
                    return Error(ProcessStartFailure(spec.FileName, "executable not found"))
                else
                    let stdout = ref ""
                    let stderr = ref ""
                    let psi = ProcessStartInfo()
                    psi.FileName <- spec.FileName
                    psi.UseShellExecute <- false
                    psi.RedirectStandardOutput <- true
                    psi.RedirectStandardError <- true
                    psi.RedirectStandardInput <- spec.StandardInput.IsSome
                    psi.WorkingDirectory <- spec.WorkingDirectory
                    for arg in spec.Arguments do
                        psi.ArgumentList.Add arg
                    for kv in spec.Environment do
                        psi.Environment.[kv.Key] <- kv.Value
                    use proc = new Process()
                    proc.StartInfo <- psi
                    let exitEvent = new System.Threading.ManualResetEventSlim(false)
                    let errorBuf = System.Text.StringBuilder()
                    do
                        proc.OutputDataReceived.Add(fun e ->
                            match e.Data with
                            | null -> ()
                            | s -> stdout := (!stdout) + s + System.Environment.NewLine)
                        proc.ErrorDataReceived.Add(fun e ->
                            match e.Data with
                            | null -> ()
                            | s ->
                                stderr := (!stderr) + s + System.Environment.NewLine
                                errorBuf.Append(s) |> ignore)
                        proc.Exited.Add(fun _ -> exitEvent.Set())

                    let started = ref false
                    let mutable startedOk = false
                    try
                        startedOk <- proc.Start()
                        started := true
                    with ex ->
                        return Error(ProcessStartFailure(spec.FileName, ex.Message))

                    if not startedOk then
                        return Error(ProcessStartFailure(spec.FileName, "Process.Start returned false"))

                    proc.BeginOutputReadLine()
                    proc.BeginErrorReadLine()

                    if spec.StandardInput.IsSome then
                        proc.StandardInput.Write(spec.StandardInput.Value)
                        proc.StandardInput.Flush()
                        proc.StandardInput.Close()

                    let timedOut = not (exitEvent.Wait(spec.Timeout))
                    if timedOut then
                        try proc.Kill(true) with _ -> ()
                        try proc.WaitForExit(5000) with _ -> ()
                        return Error(ProcessExitFailure(spec.FileName, -1, "timeout"))
                    else
                        try proc.WaitForExit() with _ -> ()
                        let rc =
                            try proc.ExitCode
                            with _ -> -1

                        let result = {
                            ExitCode = rc
                            StandardOutput = (!stdout).TrimEnd()
                            StandardError = (!stderr).TrimEnd()
                            Duration = TimeSpan.Zero
                        }
                        if rc <> 0 then
                            return Error(ProcessExitFailure(spec.FileName, rc, result.StandardError))
                        else
                            return Ok result
            }

/// Run a process synchronously inside a task. Used by installer flows.
let runSync (runner: IProcessRunner) (spec: ProcessSpec) : Result<ProcessResult, DevHostFailure> =
    Async.RunSynchronously (runner.Run spec)

/// Construct a default `ProcessSpec` builder that has the typical defaults
/// we want for every invocation (CWD = current, no stdin, no extra env).
type SpecBuilder(filename: string) =
    let mutable cwd = Directory.GetCurrentDirectory()
    let mutable args : string list = []
    let mutable env : Map<string, string> = Map.empty
    let mutable timeout = TimeSpan.FromMinutes(30.0)
    let mutable stdin : string option = None

    member _.WithArguments (a: string list) =
        args <- a
        SpecBuilder filename

    member _.WithArgument (a: string) =
        args <- args @ [ a ]
        SpecBuilder filename

    member _.WithWorkingDirectory (d: string) =
        cwd <- d
        SpecBuilder filename

    member _.WithEnvironment (k: string, v: string) =
        env <- Map.add k v env
        SpecBuilder filename

    member _.WithTimeout (t: TimeSpan) =
        timeout <- t
        SpecBuilder filename

    member _.WithStandardInput (s: string) =
        stdin <- Some s
        SpecBuilder filename

    member _.Build () : ProcessSpec =
        {
            FileName = filename
            Arguments = args
            WorkingDirectory = cwd
            Environment = env
            Timeout = timeout
            StandardInput = stdin
            RedactedArguments = None
        }

let spec (filename: string) : SpecBuilder = SpecBuilder filename

/// Direct constructor for a `ProcessSpec` record. Used by every installer
/// and check that needs to bypass the SpecBuilder chain syntax.
let mkSpec (fileName: string) (args: string list) (cwd: string) (env: Map<string, string>) (timeout: TimeSpan) (stdin: string option) : ProcessSpec =
    {
        FileName = fileName
        Arguments = args
        WorkingDirectory = cwd
        Environment = env
        Timeout = timeout
        StandardInput = stdin
        RedactedArguments = None
    }
