module Circus.Contracts.Tests.AssemblyResolver

open System
open System.Reflection

/// Forward FSharp.Core requests to whichever version ships with the
/// running .NET host (in this build environment the host provides
/// FSharp.Core 10.0.0.0). Without this forwarder the test executable
/// — which Expecto references as FSharp.Core 10.1.0.0 — fails to load
/// because the host only knows the 10.0.0.0 surface.
let install () : unit =
    AppDomain.CurrentDomain.add_AssemblyResolve (
        ResolveEventHandler (fun _ name ->
            if name.Name = "FSharp.Core" then
                Assembly.Load "FSharp.Core, Version=10.0.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a"
            else
                null
        )
    )
