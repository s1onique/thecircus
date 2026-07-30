module Circus.Tooling.Tests.AuthorityTestSupport

open System
open System.Diagnostics
open System.IO
open System.Text

let runGit path arguments =
    let startInfo = ProcessStartInfo()
    startInfo.FileName <- "git"
    startInfo.WorkingDirectory <- path
    startInfo.UseShellExecute <- false
    startInfo.RedirectStandardOutput <- true
    startInfo.RedirectStandardError <- true

    for argument in arguments do
        startInfo.ArgumentList.Add argument

    use child = Process.Start startInfo
    let stdout = child.StandardOutput.ReadToEndAsync()
    let stderr = child.StandardError.ReadToEndAsync()
    child.WaitForExit()
    child.ExitCode, stdout.Result, stderr.Result

let git path arguments =
    let code, stdout, stderr = runGit path arguments

    if code <> 0 then
        failwithf "git %s failed (%d): %s" (String.concat " " arguments) code stderr

    stdout.Trim()

type TempGitRepository(label: string) =
    let path =
        Path.Combine(Path.GetTempPath(), "circus-" + label + "-" + Guid.NewGuid().ToString("N"))

    do
        Directory.CreateDirectory path |> ignore
        git path [ "init"; "-q"; "-b"; "main" ] |> ignore
        git path [ "config"; "user.email"; "ci@local" ] |> ignore
        git path [ "config"; "user.name"; "Circus CI" ] |> ignore
        git path [ "config"; "commit.gpgsign"; "false" ] |> ignore

    member _.Path = path

    member _.Write(relativePath: string, content: string) =
        let absolutePath = Path.Combine(path, relativePath)
        let directory = Path.GetDirectoryName absolutePath

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        File.WriteAllText(absolutePath, content, UTF8Encoding(false))

    member _.WriteBytes(relativePath: string, content: byte array) =
        let absolutePath = Path.Combine(path, relativePath)
        let directory = Path.GetDirectoryName absolutePath

        if not (String.IsNullOrEmpty directory) then
            Directory.CreateDirectory directory |> ignore

        File.WriteAllBytes(absolutePath, content)

    member _.Delete(relativePath: string) =
        let absolutePath = Path.Combine(path, relativePath)

        if File.Exists absolutePath then
            File.Delete absolutePath

    member _.Commit(message: string) =
        git path [ "add"; "-A" ] |> ignore
        git path [ "commit"; "-q"; "--no-gpg-sign"; "-m"; message ] |> ignore
        git path [ "rev-parse"; "HEAD^{commit}" ]

    member _.Head = git path [ "rev-parse"; "HEAD^{commit}" ]

    member _.Tree(commit: string) =
        git path [ "rev-parse"; commit + "^{tree}" ]

    member _.BlobOfWorkingFile(relativePath: string) =
        git path [ "hash-object"; "--"; relativePath ]

    member _.Run(arguments: string list) = git path arguments
    member _.TryRun(arguments: string list) = runGit path arguments

    interface IDisposable with
        member _.Dispose() =
            try
                if Directory.Exists path then
                    Directory.Delete(path, true)
            with _ ->
                ()
