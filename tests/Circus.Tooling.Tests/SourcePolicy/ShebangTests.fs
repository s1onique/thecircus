module Circus.Tooling.Tests.SourcePolicy.ShebangTests

open Expecto
open Circus.Tooling.SourcePolicy.Shebang

[<Tests>]
let tests =
    testList
        "Shebang classification"
        [ test "#!/bin/sh is POSIX" {
              match classify "#!/bin/sh\n" with
              | ShebangPosixShell _ -> ()
              | _ -> failtestf "Expected ShebangPosixShell"
          }
          test "#!/usr/bin/env sh is POSIX" {
              match classify "#!/usr/bin/env sh\n" with
              | ShebangPosixShell _ -> ()
              | _ -> failtestf "Expected ShebangPosixShell"
          }
          test "#!/bin/bash is Bash (not POSIX)" {
              match classify "#!/bin/bash\n" with
              | ShebangBash _ -> ()
              | _ -> failtestf "Expected ShebangBash"
          }
          test "#!/usr/bin/env python3 is forbidden" {
              match classify "#!/usr/bin/env python3\n" with
              | ShebangForbidden _ -> ()
              | _ -> failtestf "Expected ShebangForbidden"
          }
          test "no shebang returns Missing" {
              match classify "echo hello\n" with
              | ShebangMissing -> ()
              | _ -> failtestf "Expected ShebangMissing"
          }
          test "BOM-rejected shebang" {
              let bomBytes = [| 0xEFuy; 0xBBuy; 0xBFuy |]
              let bomStr = System.Text.Encoding.UTF8.GetString bomBytes

              match classify (bomStr + "#!/bin/sh\n") with
              | ShebangBomRejected _ -> ()
              | _ -> failtestf "Expected ShebangBomRejected"
          } ]
