module Circus.Tooling.SourcePolicy.Classifier

open System
open System.IO
open System.Security.Cryptography

open Circus.Tooling.SourcePolicy.Domain
open Circus.Tooling.SourcePolicy.Language
open Circus.Tooling.SourcePolicy.Paths
open Circus.Tooling.SourcePolicy.Shebang

let sha256Hex (bytes: byte[]) : string =
    use sha = SHA256.Create()
    let hash = sha.ComputeHash bytes
    BitConverter.ToString(hash).Replace("-", "").ToLowerInvariant()

let sha256OfFile (absolutePath: string) : string =
    sha256Hex (File.ReadAllBytes absolutePath)

let readLeading (absolutePath: string) (limit: int) : string =
    use stream = File.OpenRead absolutePath
    let mutable len = min limit (int stream.Length)
    let buffer : byte[] = Array.zeroCreate len
    let mutable offset = 0
    while offset < len do
        let read = stream.Read(buffer, offset, len - offset)
        if read <= 0 then len <- offset
        offset <- offset + read
    System.Text.Encoding.UTF8.GetString(buffer, 0, len)

let isExecutable (repoRoot: string) (relativePath: string) : bool =
    try
        let full = safeResolve repoRoot relativePath
        let info = new FileInfo(full)
        (info.UnixFileMode &&& UnixFileMode.UserExecute) <> UnixFileMode.None
    with
    | _ -> false

let classifyCategory (name: string) (ext: string) : FileCategory =
    if name = "go.mod" || name = "go.sum" then FileCategory.Unknown
    elif ext = ".fs" then FileCategory.FSharpProduction
    elif ext = ".fsi" then FileCategory.FSharpProduction
    elif ext = ".fsproj" then FileCategory.FSharpProduction
    elif ext = ".fsx" then FileCategory.FSharpScript
    elif ext = ".elm" then FileCategory.ElmSource
    elif ext = ".sh" then FileCategory.ShellScript
    elif name = "Makefile" then FileCategory.Makefile
    elif name.StartsWith("Makefile.") then FileCategory.Makefile
    elif ext = ".mk" then FileCategory.Makefile
    elif name = "Dockerfile" then FileCategory.Dockerfile
    elif name.StartsWith("Dockerfile.") then FileCategory.Dockerfile
    elif name = "docker-compose.yml" then FileCategory.Dockerfile
    elif name = "docker-compose.yaml" then FileCategory.Dockerfile
    elif name.StartsWith("docker-compose.") then FileCategory.Dockerfile
    elif name.StartsWith("compose.") then FileCategory.Dockerfile
    elif name = "compose.yml" then FileCategory.Dockerfile
    elif name = "compose.yaml" then FileCategory.Dockerfile
    elif name = "flake.nix" then FileCategory.NixFlake
    elif ext = ".md" then FileCategory.DeclarativeDoc
    elif ext = ".markdown" then FileCategory.DeclarativeDoc
    elif ext = ".json" then FileCategory.DeclarativeDoc
    elif ext = ".toml" then FileCategory.DeclarativeDoc
    elif ext = ".xml" then FileCategory.DeclarativeDoc
    elif ext = ".yaml" then FileCategory.DeclarativeDoc
    elif ext = ".yml" then FileCategory.DeclarativeDoc
    elif ext = ".html" then FileCategory.DeclarativeDoc
    elif ext = ".css" then FileCategory.DeclarativeDoc
    elif ext = ".sql" then FileCategory.DeclarativeDoc
    else FileCategory.Unknown

let classifyLanguage (name: string) (ext: string) : SourceLanguage =
    if name = "go.mod" || name = "go.sum" then SourceLanguage.Go
    elif ext = ".fs" || ext = ".fsi" || ext = ".fsproj" || ext = ".fsx" then SourceLanguage.FSharp
    elif ext = ".elm" then SourceLanguage.Elm
    elif ext = ".sh" then SourceLanguage.PosixShell
    elif name = "Makefile" || name.StartsWith("Makefile.") || ext = ".mk" then SourceLanguage.Declarative
    elif name = "Dockerfile" || name.StartsWith("Dockerfile.") then SourceLanguage.Declarative
    elif name = "docker-compose.yml" || name = "docker-compose.yaml"
         || name.StartsWith("docker-compose.")
         || name.StartsWith("compose.")
         || name = "compose.yml" || name = "compose.yaml" then SourceLanguage.Declarative
    elif name = "flake.nix" then SourceLanguage.Declarative
    elif ext = ".md" || ext = ".markdown"
         || ext = ".json" || ext = ".toml" || ext = ".xml"
         || ext = ".yaml" || ext = ".yml"
         || ext = ".html" || ext = ".css" || ext = ".sql" then SourceLanguage.Declarative
    elif ext = ".py" || ext = ".pyw" then SourceLanguage.Python
    elif ext = ".go" then SourceLanguage.Go
    elif ext = ".js" || ext = ".cjs" || ext = ".mjs" || ext = ".jsx" then SourceLanguage.JavaScript
    elif ext = ".ts" || ext = ".cts" || ext = ".mts" || ext = ".tsx" then SourceLanguage.TypeScript
    elif ext = ".rb" then SourceLanguage.Ruby
    elif ext = ".pl" || ext = ".pm" then SourceLanguage.Perl
    elif ext = ".php" then SourceLanguage.Php
    elif ext = ".lua" then SourceLanguage.Lua
    elif ext = ".ps1" || ext = ".psm1" then SourceLanguage.PowerShell
    elif ext = ".hs" || ext = ".lhs" then SourceLanguage.Haskell
    elif ext = ".ml" || ext = ".mli" then SourceLanguage.OCaml
    else SourceLanguage.Unknown

let classify (repoRoot: string) (relativePath: string) : Domain.Classification =
    let ext = extensionOf relativePath
    let name = filenameOf relativePath
    let absolute = safeResolve repoRoot relativePath
    let leadBytes = readLeading absolute 8192
    let shebang = Shebang.classify leadBytes
    let allBytes = File.ReadAllBytes absolute
    let lines = LineCounting.count allBytes
    let sha = sha256Hex allBytes
    let executable = isExecutable repoRoot relativePath

    let mutable category = classifyCategory name ext
    let mutable language = classifyLanguage name ext

    if category = FileCategory.Unknown && ext = "" then
        match shebang with
        | ShebangPosixShell _ -> category <- FileCategory.StageZeroLauncher
        | _ -> ()

    { Path = relativePath
      Category = category
      Language = language
      Shebang = Shebang.tag shebang
      PhysicalLines = lines
      Sha256 = sha
      Executable = executable }
