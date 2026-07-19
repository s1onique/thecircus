module Circus.DevHost.Platform

open System
open System.IO
open System.Runtime.InteropServices

open Domain

/// Snapshot of the running host: kernel arch, OS-release distribution ID,
/// and the runtime identifier we will publish against.
type HostInfo =
    { Arch: string
      OsId: string
      PrettyName: string
      RuntimeId: string
      UserName: string
      UserId: int
      Groups: string list
      Kernel: string }

/// Probe the running machine for the fields the devhost needs to classify
/// the host. Treats missing values as empty strings.
module Probes =

    let architecture () : string =
        let v = Environment.GetEnvironmentVariable "PROCESSOR_ARCHITECTURE"

        if System.String.IsNullOrEmpty v then
            RuntimeInformation.OSArchitecture.ToString()
        else
            v

    let distribution () : (string * string) option =
        // /etc/os-release is the canonical Linux source. If it's missing we
        // cannot classify the host.
        let path = "/etc/os-release"

        if not (File.Exists path) then
            None
        else
            try
                let lines = File.ReadAllLines path
                let mutable id = ""
                let mutable pretty = ""

                for line in lines do
                    let eq = line.IndexOf '='

                    if eq > 0 then
                        let key = line.Substring(0, eq).Trim()
                        let value = line.Substring(eq + 1).Trim().Trim('"').Replace("\\\"", "\"")

                        if key = "ID" then
                            id <- value.ToLower()
                        elif key = "PRETTY_NAME" then
                            pretty <- value

                if String.IsNullOrEmpty id then None else Some(id, pretty)
            with _ ->
                None

    let userName () : string =
        try
            Environment.UserName
        with _ ->
            "unknown"

    let userId () : int =
        let raw = Environment.GetEnvironmentVariable "UID"

        if System.String.IsNullOrEmpty raw then
            0
        else
            match System.Int32.TryParse raw with
            | true, n -> n
            | _ -> 0

    let groups () : string list =
        let raw = Environment.GetEnvironmentVariable "USER_GROUPS"

        if System.String.IsNullOrEmpty raw then
            []
        else
            raw.Split([| ' '; ',' |], StringSplitOptions.RemoveEmptyEntries) |> Array.toList

    let kernel () : string =
        let v = Environment.GetEnvironmentVariable "KERNEL_RELEASE"
        if System.String.IsNullOrEmpty v then "(unknown)" else v

/// Map a distribution ID into our typed classification.
let classifyDistribution (id: string) : SupportedDistribution =
    match id.ToLower() with
    | "ubuntu" -> Ubuntu
    | "debian" -> Debian
    | "linuxmint"
    | "linuxmintd" -> LinuxMint
    | _ -> OtherLinux

/// Decide whether the host qualifies for bootstrap. Architecture must be
/// `LinuxX64` and the distribution must be one of the supported IDs.
let isSupported (info: HostInfo) : Result<unit, DevHostFailure> =
    if
        not (
            info.Arch.Equals("X64", StringComparison.OrdinalIgnoreCase)
            || info.Arch.Equals("AMD64", StringComparison.OrdinalIgnoreCase)
            || info.Arch.Equals("x86_64", StringComparison.OrdinalIgnoreCase)
        )
    then
        Error(UnsupportedArchitecture info.Arch)
    else
        match classifyDistribution info.OsId with
        | Ubuntu
        | Debian
        | LinuxMint -> Ok()
        | OtherLinux -> Error(UnsupportedOperatingSystem info.OsId)

/// Build a `HostInfo` from current process state. Used by Doctor and
/// Bootstrap but never by tests.
let probeHost () : HostInfo =
    let (id, pretty) =
        match Probes.distribution () with
        | Some pair -> pair
        | None -> ("", "")

    let arch =
        if String.IsNullOrEmpty(Probes.architecture ()) then
            "unknown"
        else
            Probes.architecture ()

    { Arch = arch
      OsId = id
      PrettyName = pretty
      RuntimeId = RuntimeInformation.RuntimeIdentifier
      UserName = Probes.userName ()
      UserId = Probes.userId ()
      Groups = Probes.groups ()
      Kernel = Probes.kernel () }
