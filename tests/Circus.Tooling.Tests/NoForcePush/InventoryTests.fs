module Circus.Tooling.Tests.NoForcePush.InventoryTests

open System
open System.IO
open Expecto
open Circus.Tooling.NoForcePush.SurfaceInventory
open Circus.Tooling.NoForcePush.Types

let private writeFile (root: string) (rel: string) (content: string) =
    let full = Path.Combine(root, rel.Replace('/', Path.DirectorySeparatorChar))
    let dir = Path.GetDirectoryName full
    if not (Directory.Exists dir) then
        Directory.CreateDirectory dir |> ignore
    File.WriteAllText(full, content)

let private newTempRepo () : string =
    let path = Path.Combine(Path.GetTempPath(), "circus-nfp-inv-" + Guid.NewGuid().ToString("n"))
    Directory.CreateDirectory path |> ignore
    path

[<Tests>]
let tests =
    testList
        "NoForcePush Inventory"
        [ test "parses valid CSV inventory" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  ".githooks/pre-push,executable,plaintext-command,repository,Canonical launcher\n"
              
              match readInventory root with
              | Ok entries ->
                  Expect.equal (List.length entries) 1 "one entry"
                  Expect.equal entries.Head.Path ".githooks/pre-push" "path"
                  Expect.equal entries.Head.SurfaceKind Executable "surface kind"
              | Error e ->
                  failwithf "unexpected error: %A" e
              
              Directory.Delete(root, true)
          }
          test "rejects malformed CSV (wrong field count)" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  ".githooks/pre-push,executable,repository\n"
              
              match readInventory root with
              | Ok _ -> failwith "should have failed"
              | Error (MalformedCsvRow _) -> ()
              | Error e -> failwithf "wrong error: %A" e
              
              Directory.Delete(root, true)
          }
          test "rejects duplicate paths" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  ".githooks/pre-push,executable,plaintext-command,repository,First\n" +
                  ".githooks/pre-push,executable,plaintext-command,repository,Second\n"
              
              match readInventory root with
              | Ok _ -> failwith "should have failed"
              | Error (MalformedCsvRow _) -> ()
              | Error e -> failwithf "wrong error: %A" e
              
              Directory.Delete(root, true)
          }
          test "rejects invalid surface kind" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  ".githooks/pre-push,invalid-kind,plaintext-command,repository,test\n"
              
              match readInventory root with
              | Ok _ -> failwith "should have failed"
              | Error (MalformedCsvRow _) -> ()
              | Error e -> failwithf "wrong error: %A" e
              
              Directory.Delete(root, true)
          }
          test "validates missing files" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  ".githooks/pre-push,executable,plaintext-command,repository,Missing file\n"
              
              match readInventory root with
              | Ok _ -> failwith "should have failed"
              | Error (FileMissing _) -> ()
              | Error e -> failwithf "wrong error: %A" e
              
              Directory.Delete(root, true)
          }
          test "validates symlink escaping" {
              let root = newTempRepo ()
              writeFile root "factory/no-force-push-surfaces.csv"
                  "path,surface_kind,parser_kind,authority,reason\n" +
                  "link-to-outside,executable,plaintext-command,repository,Escaping\n"
              
              // Create a symlink that escapes
              let linkPath = Path.Combine(root, "link-to-outside")
              let targetPath = Path.Combine(root, "..", "outside")
              try
                  File.CreateSymbolicLink(linkPath, targetPath) |> ignore
              with _ -> ()
              
              match readInventory root with
              | Ok _ -> failwith "should have failed"
              | Error (SymlinkEscapes _) -> ()
              | Error e -> failwithf "wrong error: %A" e
              
              Directory.Delete(root, true)
          } ]
