module Circus.DevHost.FrontendInstaller

open System
open System.IO

open Domain
open Circus.DevHost.Adapters
open Circus.DevHost.ProcessRunner

/// Parse the version output of `elm --version`.
let parseElmVersionOutput (text: string) : string =
    let t = text.Trim()
    if t.StartsWith "Elm " then t.Substring(4).Trim()
    else t

/// Check the local `elm --version` matches the *compiler* version.
let verifyElm
    (runner: IProcessRunner)
    (webDir: string)
    (expectedCompilerVersion: ToolVersion)
    : Result<string, DevHostFailure> =
    let elmBin = Path.Combine(webDir, "node_modules", ".bin", "elm")
    if not (File.Exists elmBin) then Error(MissingTool ElmCompiler)
    else
        let spec =
            mkSpec elmBin [ "--version" ] webDir Map.empty (TimeSpan.FromSeconds(30.0)) None
        match runSync runner spec with
        | Error e -> Error e
        | Ok r ->
            let actual = parseElmVersionOutput r.StandardOutput
            let expected = ToolVersion.value expectedCompilerVersion
            if actual = expected then Ok actual
            else Error(WrongToolVersion(ElmCompiler, expected, actual))

/// Verify `elm-test` is installed.
let verifyElmTest
    (fs: IFilesystem)
    (webDir: string)
    : Result<unit, DevHostFailure> =
    let elmTestBin = Path.Combine(webDir, "node_modules", ".bin", "elm-test")
    if not (fs.IsFile elmTestBin) then Error(MissingTool ElmTest)
    else Ok ()

/// Use the *installed* Node.js to run `npm ci --ignore-scripts` then
/// `node node_modules/elm/install.js`.
let restoreFrontend
    (runner: IProcessRunner)
    (webDir: string)
    (nodeDir: string)
    (cancellation: unit -> Async<unit>)
    : Async<Result<unit, DevHostFailure>> =
    async {
        let nodeBin = Path.Combine(nodeDir, "bin", "node")
        let npmBin = Path.Combine(nodeDir, "bin", "npm")

        if not (File.Exists nodeBin) then
            return Error(VerificationFailure "installed node missing")
        if not (File.Exists npmBin) then
            return Error(VerificationFailure "installed npm missing")
        if not (File.Exists(Path.Combine(webDir, "package-lock.json"))) then
            return Error(VerificationFailure "package-lock.json missing")

        let env = Map.ofList [ "PATH", Path.Combine(nodeDir, "bin") ]
        let ciSpec =
            mkSpec npmBin [ "ci"; "--ignore-scripts" ] webDir env (TimeSpan.FromMinutes(5.0)) None
        match runSync runner ciSpec with
        | Error e -> return Error e
        | Ok _ ->
            let installerJs = Path.Combine(webDir, "node_modules", "elm", "install.js")
            if not (File.Exists installerJs) then
                return Error(VerificationFailure "elm/install.js missing")
            let installerSpec =
                mkSpec nodeBin [ installerJs ] webDir env (TimeSpan.FromSeconds(60.0)) None
            let! _ = cancellation ()
            match runSync runner installerSpec with
            | Error e -> return Error e
            | Ok _ -> return Ok ()
    }

/// Pure helper used by the doctor to extract the compiler version.
let classifyElmOutput (output: string) : Result<ToolVersion, DevHostFailure> =
    let raw = parseElmVersionOutput output
    match ToolVersion.parse raw with
    | Ok v -> Ok v
    | Error _ -> Error(MalformedAuthorityFile ("elm --version:" + output))
