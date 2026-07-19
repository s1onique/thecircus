module Circus.Tooling.SourcePolicy.LineCounting

let count (bytes: byte[]) : int =
    if bytes.Length = 0 then 0
    else
        let mutable n = 0
        for i in 0 .. bytes.Length - 1 do
            if bytes.[i] = byte '\n' then n <- n + 1
        if bytes.[bytes.Length - 1] <> byte '\n' then n <- n + 1
        n
