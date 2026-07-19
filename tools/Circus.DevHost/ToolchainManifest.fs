module Circus.DevHost.ToolchainManifest

open System
open System.IO
open System.Text.Json

open Domain

type ActionlintSpec =
    { Version: string
      LinuxX64Url: string
      Sha256: string }

type ShellCheckSpec =
    { Version: string
      LinuxX64Url: string
      Sha256: string }

type PythonPolicySpec =
    { Python: string
      Pip: string
      PyYaml: string }

type BootstrapSdkImageSpec = { Reference: string; Digest: string }

/// Parsed, typed contents of eng/devhost-toolchain.json.
type ToolchainData =
    { SchemaVersion: int
      Actionlint: ActionlintSpec
      ShellCheck: ShellCheckSpec
      PythonPolicy: PythonPolicySpec
      BootstrapSdkImage: BootstrapSdkImageSpec option }

exception ManifestFormatException of string

let private isMcrDotnetSdkReference (reference: string) =
    reference.StartsWith("mcr.microsoft.com/dotnet/sdk:", StringComparison.Ordinal)
    && not (reference.Contains("@", StringComparison.Ordinal))

let private isPinnedSha256 (digest: string) =
    digest.StartsWith("sha256:", StringComparison.Ordinal)
    && digest.Length = 71

/// Validate the manifest's structural invariants without reading the
/// repository authority. Surfaces contract errors as a `Result` so callers
/// can decide between exit class 2 and 1.
let validate (manifest: ToolchainData) : Result<unit, DevHostFailure> =
    let problems =
        [
            match manifest.BootstrapSdkImage with
            | None -> "bootstrap_sdk_image is required"
            | Some image when not (isMcrDotnetSdkReference image.Reference) ->
                sprintf "bootstrap_sdk_image.reference '%s' is not an mcr.microsoft.com/dotnet/sdk tag" image.Reference
            | Some image when not (isPinnedSha256 image.Digest) ->
                sprintf "bootstrap_sdk_image.digest '%s' is not a pinned sha256:..." image.Digest
            | Some _ -> ""
        ]
        |> List.filter (fun problem -> not (String.IsNullOrEmpty problem))

    match problems with
    | [] -> Ok()
    | problem :: _ -> Error(MalformedAuthorityFile("eng/devhost-toolchain.json: " + problem))

module Manifest =
    let private tryProperty (element: JsonElement) (name: string) : JsonElement option =
        let mutable property = Unchecked.defaultof<JsonElement>

        if element.TryGetProperty(name, &property) then
            Some property
        else
            None

    let private requiredProperty (element: JsonElement) (name: string) : JsonElement =
        match tryProperty element name with
        | Some property -> property
        | None -> raise (ManifestFormatException(sprintf "missing field '%s'" name))

    let private requiredString (element: JsonElement) (name: string) : string =
        let value = (requiredProperty element name).GetString()

        if String.IsNullOrEmpty value then
            raise (ManifestFormatException(sprintf "missing field '%s'" name))
        else
            value

    let parse (json: string) : ToolchainData =
        try
            use document = JsonDocument.Parse json
            let root = document.RootElement

            let schema =
                match tryProperty root "schema_version" with
                | Some property -> property.GetInt32()
                | None -> 1

            if schema <> 1 then
                raise (ManifestFormatException(sprintf "unsupported schema_version %d" schema))

            let actionlint = requiredProperty root "actionlint"
            let shellcheck = requiredProperty root "shellcheck"
            let pythonPolicy = requiredProperty root "python_policy"

            let bootstrapImage =
                tryProperty root "bootstrap_sdk_image"
                |> Option.map (fun element ->
                    { Reference = requiredString element "reference"
                      Digest = requiredString element "digest" })

            { SchemaVersion = schema
              Actionlint =
                { Version = requiredString actionlint "version"
                  LinuxX64Url = requiredString actionlint "linux_x64_url"
                  Sha256 = requiredString actionlint "sha256" }
              ShellCheck =
                { Version = requiredString shellcheck "version"
                  LinuxX64Url = requiredString shellcheck "linux_x64_url"
                  Sha256 = requiredString shellcheck "sha256" }
              PythonPolicy =
                { Python = requiredString pythonPolicy "python"
                  Pip = requiredString pythonPolicy "pip"
                  PyYaml = requiredString pythonPolicy "pyyaml" }
              BootstrapSdkImage = bootstrapImage }
        with
        | ManifestFormatException _ -> reraise ()
        | ex -> raise (ManifestFormatException(sprintf "manifest parse error: %s" ex.Message))

    let loadFromPath (path: string) : ToolchainData =
        if not (File.Exists path) then
            raise (ManifestFormatException(sprintf "manifest missing at '%s'" path))

        File.ReadAllText path |> parse

    let reconcileAgainst
        (manifest: ToolchainData)
        (_dotnet: ToolVersion option)
        (_node: ToolVersion option)
        (_elm: ToolVersion option)
        : DevHostFailure list =
        match validate manifest with
        | Ok() -> []
        | Error failure -> [ failure ]
