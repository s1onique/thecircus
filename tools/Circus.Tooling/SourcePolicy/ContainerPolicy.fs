module Circus.Tooling.SourcePolicy.ContainerPolicy

/// Static container-publication policy checks. F# port of the deleted
/// `scripts/verify_container_policy.py` (see predecessor
/// `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01`).
///
/// This is a minimal initial port focused on the structural checks. The
/// parity table in `ACT-CIRCUS-ML-ONLY-SOURCE-POLICY01-CORRECTION01` will
/// be filled out in subsequent ACTs as the full Python semantics are
/// characterised and re-implemented.

open System
open System.IO

type Violation = {
    Check: string
    Path: string
    Detail: string
}

type ContainerPolicyReport = {
    ChecksTotal: int
    ChecksPassed: int
    Violations: Violation list
}

exception CheckFailed of string

let private fail (msg: string) : 'a = raise (CheckFailed msg)

let private readText (root: string) (relative: string) : string =
    let full = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar))
    if not (File.Exists full) then
        fail (sprintf "missing required file: %s" relative)
    File.ReadAllText full

let private checkRequiredFiles (root: string) : Violation list =
    let required = [
        "Dockerfile.backend"
        "Dockerfile.frontend"
        ".dockerignore"
        ".github/workflows/harbor.yml"
        ".github/workflows/harbor-build-image.yml"
        "scripts/ci/build_image.sh"
        "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"
        ".github/scripts/install-spbnix-harbor-ca.sh"
    ]
    let mutable violations : Violation list = []
    for rel in required do
        let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
        if not (File.Exists full) then
            violations <- { Check = "required_files"; Path = rel; Detail = sprintf "required file missing: %s" rel } :: violations
    List.rev violations

let private checkShellExecutable (root: string) : Violation list =
    let scripts = [
        "scripts/ci/build_image.sh"
        "scripts/ci/publish_image.sh"
        "scripts/ci/verify_build_image.sh"
        "scripts/ci/wire_buildx_builder.sh"
    ]
    let mutable violations : Violation list = []
    for rel in scripts do
        let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
        try
            let info = new FileInfo(full)
            if (info.UnixFileMode &&& UnixFileMode.UserExecute) <> UnixFileMode.UserExecute then
                violations <- { Check = "shell_executable"
                                Path = rel
                                Detail = sprintf "required shell script is not executable: %s" rel } :: violations
        with _ -> ()
    List.rev violations

let private checkDockerignore (root: string) : Violation list =
    let mutable violations : Violation list = []
    try
        let text = readText root ".dockerignore"
        let required = [ ".git"; ".github"; ".factory"; "**/bin"; "**/obj"; "**/node_modules"; "**/elm-stuff"; "**/TestResults"; ".env"; ".env.*"; "*.pem"; "*.key"; "*.crt" ]
        let actual = text.Split('\n') |> Array.map (fun l -> l.Trim()) |> Set.ofArray
        for r in required do
            if not (Set.contains r actual) then
                violations <- { Check = "dockerignore_exclusions"
                                Path = ".dockerignore"
                                Detail = sprintf ".dockerignore misses required exclusion: %s" r } :: violations
    with _ -> ()
    List.rev violations

let verify (root: string) : ContainerPolicyReport =
    let results =
        [ "required_files", checkRequiredFiles root
          "shell_executable", checkShellExecutable root
          "dockerignore_exclusions", checkDockerignore root ]
    let mutable passed = 0
    let mutable failed : Violation list = []
    for (_, v) in results do
        if List.isEmpty v then passed <- passed + 1
        else failed <- List.rev v @ failed
    { ChecksTotal = List.length results
      ChecksPassed = passed
      Violations = failed }

let runVerify (root: string) : int =
    try
        let report = verify root
        if List.isEmpty report.Violations then 0 else 1
    with
    | _ -> 2
