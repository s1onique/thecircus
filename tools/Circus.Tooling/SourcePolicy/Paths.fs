module Circus.Tooling.SourcePolicy.Paths

let toPosix (path: string) : string =
    path.Replace('\\', '/')

let filenameOf (path: string) : string =
    let n = toPosix path
    let i = n.LastIndexOf '/'
    if i < 0 then n else n.Substring(i + 1)

let extensionOf (path: string) : string =
    let name = filenameOf path
    let i = name.LastIndexOf '.'
    if i <= 0 then "" else name.Substring(i).ToLowerInvariant()

let containsParentTraversal (path: string) : string =
    let n = toPosix path
    let pieces = n.Split('/')
    let mutable depth = 0
    let mutable bad = ""
    for piece in pieces do
        if piece = ".." then
            depth <- depth - 1
            if depth < 0 then bad <- ".."
    bad

let isAbsolute (path: string) : bool =
    System.IO.Path.IsPathRooted path

let canonicalise (relativePath: string) : string =
    let pieces = (toPosix relativePath).Split([| '/' |], System.StringSplitOptions.RemoveEmptyEntries)
    let stack = System.Collections.Generic.Stack<string>()
    for piece in pieces do
        if piece = "." then ()
        elif piece = ".." then
            if stack.Count > 0 then stack.Pop() |> ignore
        else stack.Push piece
    let ordered = stack.ToArray() |> Array.rev
    String.concat "/" ordered

let safeResolve (repoRoot: string) (relativePath: string) : string =
    System.IO.Path.GetFullPath(
        System.IO.Path.Combine(repoRoot, (toPosix relativePath).TrimStart('/')))

let escapesRepository (repoRoot: string) (relativePath: string) : bool =
    let n = toPosix relativePath
    if isAbsolute n then true
    elif n.Contains ".." then true
    else
        try
            let resolved = System.IO.Path.GetFullPath(System.IO.Path.Combine(repoRoot, n))
            let root = System.IO.Path.GetFullPath repoRoot
            let rootWithSep =
                if root.EndsWith(System.IO.Path.DirectorySeparatorChar.ToString()) then root
                else root + string System.IO.Path.DirectorySeparatorChar
            if resolved = root then false
            else not (resolved.StartsWith(rootWithSep, System.StringComparison.Ordinal))
        with
        | _ -> true

let isVendoredElmPath (relativePath: string) : bool =
    let p = toPosix relativePath
    p.StartsWith("web/node_modules/", System.StringComparison.Ordinal)
    || p.StartsWith("web/elm-stuff/", System.StringComparison.Ordinal)
    || p = "web/node_modules"
    || p = "web/elm-stuff"
