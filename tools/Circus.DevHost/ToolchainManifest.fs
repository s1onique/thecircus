module Circus.DevHost.ToolchainManifest

open System.Text.Json
open Domain

type ActionlintSpec = {
    Version: string
    LinuxX64Url: string
    Sha256: string
}

type ShellCheckSpec = {
    Version: string
    LinuxX64Url: string
    Sha256: string
}

type PythonPolicySpec = {
    Python: string
    Pip: string
    PyYaml: string
}

type BootstrapSdkImageSpec = {
    Reference: string
    Digest: string
}

type Manifest = {
    SchemaVersion: int
    Actionlint: ActionlintSpec
    ShellCheck: ShellCheckSpec
    PythonPolicy: PythonPolicySpec
    BootstrapSdkImage: BootstrapSdkImageSpec option
}

exception ManifestFormatException of string

module Manifest =

    let private tryString (el: JsonElement) (name: string) : string option =
        if el.TryGetProperty name then
            let v = el.GetProperty(name).GetString()
            if isNull v then None else Some v
        else None

    let parse (json: string) : Manifest =
        try
            let doc = JsonDocument.Parse(json)
            let root = doc.RootElement
            let mutable schema = 1
            if root.TryGetProperty "schema_version" then
                schema <- root.GetProperty("schema_version").GetInt32()
            if schema <> 1 then
                raise (ManifestFormatException(sprintf "unsupported schema_version %d" schema))

            let mutable actionlintEl = Unchecked.defaultof<JsonElement>
            let mutable shellcheckEl = Unchecked.defaultof<JsonElement>
            let mutable pythonEl = Unchecked.defaultof<JsonElement>
            if root.TryGetProperty "actionlint" then
                actionlintEl <- root.GetProperty("actionlint")
            if root.TryGetProperty "shellcheck" then
                shellcheckEl <- root.GetProperty("shellcheck")
            if root.TryGetProperty "python_policy" then
                pythonEl <- root.GetProperty("python_policy")

            let getStr parent name =
                match tryString parent name with
                | Some s -> s
                | None -> raise (ManifestFormatException(sprintf "missing field '%s'" name))

            let bootstrapImageOpt : BootstrapSdkImageSpec option =
                if root.TryGetProperty "bootstrap_sdk_image" then
                    let el = root.GetProperty("bootstrap_sdk_image")
                    Some({
                        Reference = getStr el "reference"
                        Digest = el.GetProperty("digest").GetString()
                    })
                else None

            {
                SchemaVersion = schema
                Actionlint = {
                    Version = getStr actionlintEl "version"
                    LinuxX64Url = getStr actionlintEl "linux_x64_url"
                    Sha256 = getStr actionlintEl "sha256"
                }
                ShellCheck = {
                    Version = getStr shellcheckEl "version"
                    LinuxX64Url = getStr shellcheckEl "linux_x64_url"
                    Sha256 = getStr shellcheckEl "sha256"
                }
                PythonPolicy = {
                    Python = getStr pythonEl "python"
                    Pip = getStr pythonEl "pip"
                    PyYaml = getStr pythonEl "pyyaml"
                }
                BootstrapSdkImage = bootstrapImageOpt
            }
        with ex ->
            raise (ManifestFormatException(sprintf "manifest parse error: %s" ex.Message))

    let loadFromPath (path: string) : Manifest =
        if not (System.IO.File.Exists path) then
            raise (ManifestFormatException(sprintf "manifest missing at '%s'" path))
        else
            System.IO.File.ReadAllText path |> parse

    let reconcileAgainst
        (manifest: Manifest)
        (_dotnet: ToolVersion option)
        (_node: ToolVersion option)
        (_elm: ToolVersion option)
        : DevHostFailure list =

        if manifest.BootstrapSdkImage.IsNone then
            [ MalformedAuthorityFile "eng/devhost-toolchain.json:bootstrap_sdk_image" ]
        else []
