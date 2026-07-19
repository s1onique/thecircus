module Circus.Tooling.Tests.TestDoubles

open System.IO

type TempDirectory() =
    let path = Path.Combine(Path.GetTempPath(), "circus-tooling-tests-" + System.Guid.NewGuid().ToString("n"))
    do Directory.CreateDirectory path |> ignore
    member _.Path = path
    interface System.IDisposable with
        member _.Dispose() =
            try
                if Directory.Exists path then
                    Directory.Delete(path, true)
            with _ -> ()
