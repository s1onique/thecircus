module Circus.Tooling.ProcessTreeFixture.Program

// =============================================================================
// Precompiled F# process fixture for BoundedProcess authority tests.
//
// The fixture owns a small set of named modes selected by the first
// command-line argument (`argv.[0]`). Invocation is through the
// .NET runtime host:
//
//   dotnet <fixture.dll> <mode> [args...]
//
// Every mode writes its output by raw byte writes against the
// appropriate Console stream (NOT via TextWriter). This matches the
// way Circus.Tooling.FSharpDiagnostics.RepairEpisodes.BoundedProcess
// reads redirected stdout and stderr (Stream.ReadAsync), so the
// bytes the fixture writes are exactly the bytes the production
// reader receives. The fixture never opens or creates a file; it
// only writes to its standard streams.
//
// Byte alphabets (chosen so the authority tests can assert exact
// bytes rather than substrings):
//   stdout -> 'a'..'z' cycle
//   stderr -> 'A'..'Z' cycle
//
// Modes:
//   empty                                  - exit 0 with no output
//   stdout <count>                         - write <count> stdout bytes
//   stderr <count>                         - write <count> stderr bytes
//   both <stdout-count> <stderr-count>     - write <stdout-count> stdout
//                                            bytes and <stderr-count>
//                                            stderr bytes
//   sleep <milliseconds>                   - sleep then exit 0
//   exit <code>                            - exit with the specified code
//   exit-with-both <stdout-count> <stderr-count> <code>
//                                          - write stdout and stderr then
//                                            exit with the specified code
//   echo-args <args...>                    - echo every argument after the
//                                            mode to stdout, separated by
//                                            single spaces followed by a
//                                            trailing newline
//   working-directory                      - write the current working
//                                            directory to stdout without
//                                            a trailing newline
//
// The fixture never crashes on a malformed invocation; it prints
// the unsupported mode to stderr and exits with code 2 so the
// authority test is never confused by a fixture defect.
// =============================================================================

open System
open System.IO
open System.Text
open System.Threading

let private exitCodeForUnknownMode = 2
let private exitCodeForFixtureError = 3

let private makeStdoutBytes (count: int) : byte array =
    Array.init count (fun i -> byte (97 + (i % 26))) // 'a'..'z'

let private makeStderrBytes (count: int) : byte array =
    Array.init count (fun i -> byte (65 + (i % 26))) // 'A'..'Z'

let private writeBytes (stream: Stream) (bytes: byte array) : unit =
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush()

let private writeString (stream: Stream) (text: string) : unit =
    let bytes = Encoding.UTF8.GetBytes(text)
    stream.Write(bytes, 0, bytes.Length)
    stream.Flush()

let private runEmpty () : int = 0

let private runStdout (countStr: string) : int =
    let count = Int32.Parse(countStr)
    let bytes = makeStdoutBytes count
    writeBytes (Console.OpenStandardOutput()) bytes
    0

let private runStderr (countStr: string) : int =
    let count = Int32.Parse(countStr)
    let bytes = makeStderrBytes count
    writeBytes (Console.OpenStandardError()) bytes
    0

let private runBoth (stdoutCountStr: string) (stderrCountStr: string) : int =
    let stdoutCount = Int32.Parse(stdoutCountStr)
    let stderrCount = Int32.Parse(stderrCountStr)
    let stdoutBytes = makeStdoutBytes stdoutCount
    let stderrBytes = makeStderrBytes stderrCount
    writeBytes (Console.OpenStandardOutput()) stdoutBytes
    writeBytes (Console.OpenStandardError()) stderrBytes
    0

let private runSleep (msStr: string) : int =
    let ms = Int32.Parse(msStr)
    Thread.Sleep(ms)
    0

let private runExit (codeStr: string) : int = Int32.Parse(codeStr)

let private runExitWithBoth (stdoutCountStr: string) (stderrCountStr: string) (codeStr: string) : int =
    let stdoutCount = Int32.Parse(stdoutCountStr)
    let stderrCount = Int32.Parse(stderrCountStr)
    let code = Int32.Parse(codeStr)
    let stdoutBytes = makeStdoutBytes stdoutCount
    let stderrBytes = makeStderrBytes stderrCount
    writeBytes (Console.OpenStandardOutput()) stdoutBytes
    writeBytes (Console.OpenStandardError()) stderrBytes
    code

let private runEchoArgs (args: string[]) : int =
    let stdout = Console.OpenStandardOutput()

    for i in 1 .. args.Length - 1 do
        writeString stdout (args.[i] + " ")

    writeString stdout Environment.NewLine
    0

let private runWorkingDirectory () : int =
    let stdout = Console.OpenStandardOutput()
    writeString stdout (Directory.GetCurrentDirectory())
    0

[<EntryPoint>]
let main (argv: string[]) : int =
    try
        match argv with
        | [| "empty" |] -> runEmpty ()
        | [| "stdout"; countStr |] -> runStdout countStr
        | [| "stderr"; countStr |] -> runStderr countStr
        | [| "both"; stdoutCountStr; stderrCountStr |] -> runBoth stdoutCountStr stderrCountStr
        | [| "sleep"; msStr |] -> runSleep msStr
        | [| "exit"; codeStr |] -> runExit codeStr
        | [| "exit-with-both"; stdoutCountStr; stderrCountStr; codeStr |] ->
            runExitWithBoth stdoutCountStr stderrCountStr codeStr
        | array when Array.length array >= 1 && array.[0] = "echo-args" -> runEchoArgs array
        | [| "working-directory" |] -> runWorkingDirectory ()
        | _ ->
            writeString (Console.OpenStandardError()) (sprintf "unknown fixture mode: %A" argv)
            exitCodeForUnknownMode
    with ex ->
        writeString (Console.OpenStandardError()) (sprintf "fixture error: %s" ex.Message)
        exitCodeForFixtureError
