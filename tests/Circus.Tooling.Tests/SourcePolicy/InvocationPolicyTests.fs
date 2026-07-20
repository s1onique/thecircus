module Circus.Tooling.Tests.SourcePolicy.InvocationPolicyTests

open Expecto
open Circus.Tooling.SourcePolicy.InvocationPolicy
open Circus.Tooling.SourcePolicy.Language

[<Tests>]
let tests =
    testList "Invocation policy" [
        test "Makefile with python3 is flagged" {
            let text = "all:\n\tpython3 scripts/check.py\n"
            let findings = evaluate "Makefile" text
            Expect.isTrue (List.exists (fun (f: Circus.Tooling.SourcePolicy.Domain.Finding) -> f.Code = ForbiddenInterpreterInvocation) findings) "python3 invocation"
        }
        test "Makefile with go run is flagged" {
            let text = "all:\n\tgo run ./cmd/foo\n"
            let findings = evaluate "Makefile" text
            Expect.isTrue (List.exists (fun (f: Circus.Tooling.SourcePolicy.Domain.Finding) -> f.Code = ForbiddenInterpreterInvocation) findings) "go run"
        }
        test "Makefile with node is flagged" {
            let text = "all:\n\tnode script.js\n"
            let findings = evaluate "Makefile" text
            Expect.isTrue (List.exists (fun (f: Circus.Tooling.SourcePolicy.Domain.Finding) -> f.Code = ForbiddenInterpreterInvocation) findings) "node"
        }
        test "Dockerfile with python is flagged" {
            let text = "FROM ubuntu\nRUN python3 -c \"print('hi')\"\n"
            let findings = evaluate "Dockerfile" text
            Expect.isTrue (List.exists (fun (f: Circus.Tooling.SourcePolicy.Domain.Finding) -> f.Code = ForbiddenInterpreterInvocation) findings) "python in Dockerfile"
        }
        test "Shell with python shebang is flagged" {
            let text = "#!/bin/sh\npython3 -c \"x\"\n"
            let findings = evaluate "scripts/x.sh" text
            Expect.isTrue (List.exists (fun (f: Circus.Tooling.SourcePolicy.Domain.Finding) -> f.Code = ForbiddenInterpreterInvocation) findings) "python in shell"
        }
        test "Non-operational file is not scanned" {
            let text = "python3 foo\n"
            let findings = evaluate "README.md" text
            Expect.equal findings [] "not scanned"
        }
        test "Comment line is not flagged" {
            let text = "# this is a comment\nall:\n\tls\n"
            let findings = evaluate "Makefile" text
            Expect.equal findings [] "comment ignored"
        }
    ]
