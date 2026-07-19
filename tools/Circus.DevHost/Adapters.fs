module Circus.DevHost.Adapters

open System
open System.IO
open System.Net.Http
open System.Threading

open Domain
open Circus.DevHost.Downloads
open Circus.DevHost.ProcessRunner

/// Filesystem adapter surface. We classify outcomes rather than throw.
type IFilesystem =
    abstract Exists : path:string -> bool
    abstract IsFile : path:string -> bool
    abstract IsDirectory : path:string -> bool
    abstract ReadAllText : path:string -> string
    abstract ReadAllLines : path:string -> string list
    abstract WriteAllText : path:string * content:string -> unit
    abstract AppendAllText : path:string * content:string -> unit
    abstract CreateDirectory : path:string -> unit
    abstract DeleteFile : path:string -> unit
    abstract DeleteDir : path:string -> unit
    abstract Move : source:string * dest:string -> unit
    abstract MakeExecutable : path:string -> unit
    abstract Combine : paths:string list -> string
    abstract TempFile : suffix:string -> string

type RealFilesystem() =

    interface IFilesystem with
        member _.Exists(p) = File.Exists p || Directory.Exists p
        member _.IsFile(p) = File.Exists p
        member _.IsDirectory(p) = Directory.Exists p
        member _.ReadAllText p = File.ReadAllText p
        member _.ReadAllLines p =
            File.ReadAllLines p |> Array.toList
        member _.WriteAllText (p, c) = File.WriteAllText(p, c)
        member _.AppendAllText (p, c) =
            File.AppendAllText(p, c)
        member _.CreateDirectory p =
            Directory.CreateDirectory p |> ignore
        member _.DeleteFile p =
            if File.Exists p then File.Delete p
        member _.DeleteDir p =
            if Directory.Exists p then Directory.Delete(p, true)
        member _.Move (s, d) = File.Move(s, d)
        member _.MakeExecutable p =
            // chmod +x on POSIX. On Windows this would be irrelevant for our
            // target host.
            try
                File.SetUnixFileMode(p,
                    (UnixFileMode.UserRead ||| UnixFileMode.UserWrite ||| UnixFileMode.UserExecute
                     ||| UnixFileMode.GroupRead ||| UnixFileMode.GroupExecute
                     ||| UnixFileMode.OtherRead ||| UnixFileMode.OtherExecute))
            with _ -> ()
        member _.Combine parts =
            String.concat (string Path.DirectorySeparatorChar) parts
        member _.TempFile suffix =
            let p = Path.GetTempFileName()
            if not (String.IsNullOrEmpty suffix) then
                let target = p + suffix
                File.Move(p, target)
                target
            else p

type IFakeFilesystem() =
    let mutable map : Map<string, string> = Map.empty
    let mutable dirs : Set<string> = Set.empty
    let mutable files : Set<string> = Set.empty

    interface IFilesystem with
        member _.Exists p = (Set.contains p dirs) || (Set.contains p files)
        member _.IsFile p = Set.contains p files
        member _.IsDirectory p = Set.contains p dirs
        member _.ReadAllText p =
            if Map.containsKey p map then Map.find p map
            else raise (FileNotFoundException p)
        member _.ReadAllLines p =
            (Map.find p map).Split([| '\n' |]) |> Array.toList
        member _.WriteAllText (p, c) =
            map <- Map.add p c map
            files <- Set.add p files
        member _.AppendAllText (p, c) =
            let prev = if Map.containsKey p map then Map.find p map else ""
            map <- Map.add p (prev + c) map
            files <- Set.add p files
        member _.CreateDirectory p = dirs <- Set.add p dirs
        member _.DeleteFile p =
            files <- Set.remove p files
            map <- Map.remove p map
        member _.DeleteDir p =
            dirs <- Set.remove p dirs
        member _.Move (s, d) =
            if Map.containsKey s map then
                map <- Map.add d (Map.find s map) map
                map <- Map.remove s map
                files <- Set.add d files
                files <- Set.remove s files
        member _.MakeExecutable _ = ()
        member _.Combine parts =
            String.concat "/" parts
        member _.TempFile _ = "/tmp/fake-temp"

/// Wall-clock adapter. Used to compute deterministic durations in tests.
type IClock =
    abstract Now : unit -> DateTimeOffset

type RealClock() =
    interface IClock with
        member _.Now() = DateTimeOffset.Now

type FixedClock(initial: DateTimeOffset) =
    let mutable now = initial
    interface IClock with
        member _.Now() = now
    member _.Set(value: DateTimeOffset) = now <- value

/// Environment adapter surface used by every piece that reads env.
type IEnvironment =
    abstract GetEnv : key:string -> string option
    abstract SetEnv : key:string * value:string -> unit
    abstract WorkingDirectory : unit -> string

type RealEnvironment() =
    interface IEnvironment with
        member _.GetEnv key =
            let v = Environment.GetEnvironmentVariable key
            if String.IsNullOrEmpty v then None else Some v
        member _.SetEnv(key, value) = Environment.SetEnvironmentVariable(key, value)
        member _.WorkingDirectory() = Directory.GetCurrentDirectory()

type FakeEnvironment(initial: Map<string, string>) =
    let mutable bag : Map<string, string> = initial
    interface IEnvironment with
        member _.GetEnv key = Map.tryFind key bag
        member _.SetEnv(key, value) =
            bag <- Map.add key value bag
        member _.WorkingDirectory() = "/tmp/fake-cwd"

/// Console adapter used by doctor/env to split stdout and stderr.
type IConsole =
    abstract Stdout : text:string -> unit
    abstract Stderr : text:string -> unit

type RealConsole() =
    interface IConsole with
        member _.Stdout text =
            if not (String.IsNullOrEmpty text) then
                Console.Out.Write(text + System.Environment.NewLine)
        member _.Stderr text =
            if not (String.IsNullOrEmpty text) then
                Console.Error.Write(text + System.Environment.NewLine)

type FakeConsole() =
    let mutable out : string list = []
    let mutable err : string list = []
    member _.StdoutLines = List.rev out
    member _.StderrLines = List.rev err
    interface IConsole with
        member _.Stdout text =
            out <- text :: out
        member _.Stderr text =
            err <- text :: err
