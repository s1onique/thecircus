module Circus.DevHost.Tests.DomainTests

open Expecto
open Circus.DevHost.Domain

let private check name status =
    { Name = name
      Status = status
      Detail = None }

let tests =
    testList
        "Domain"
        [ test "aggregateIsPassing accepts passed and deliberately skipped checks" {
              let checks = [ check "passed" Passed; check "skipped" (Skipped "not applicable") ]

              Expect.isTrue (aggregateIsPassing checks) "Passed/skipped should aggregate to passing"
          }

          test "aggregateIsPassing rejects a failed check" {
              let checks = [ check "failed" (Failed(MissingTool Docker)) ]
              Expect.isFalse (aggregateIsPassing checks) "A failure must fail the aggregate"
          }

          test "ToolVersion.parse accepts semantic versions and rejects malformed input" {
              match ToolVersion.parse "10.0.202" with
              | Ok version -> Expect.equal (ToolVersion.value version) "10.0.202" "Version should round-trip"
              | Error error -> failtestf "Expected valid version, got %s" error

              match ToolVersion.parse "10.0" with
              | Error _ -> ()
              | Ok version -> failtestf "Expected malformed version, got %s" (ToolVersion.value version)
          }

          test "renderFailure produces a stable repository identity message" {
              Expect.equal
                  (renderFailure (RepositoryIdentityFailure "wrong root"))
                  "repository identity: wrong root"
                  "Failure rendering is part of the CLI contract"
          }

          test "classify separates success, capability failures, and contract errors" {
              Expect.equal (classify { Checks = [] }) Success "An empty aggregate is successful"

              Expect.equal
                  (classify { Checks = [ check "docker" (Failed DockerPermissionDenied) ] })
                  CapabilityFailure
                  "A missing capability must map to exit class 1"

              Expect.equal
                  (classify { Checks = [ check "repo" (Failed(RepositoryIdentityFailure "wrong root")) ] })
                  ContractError
                  "A repository contract violation must map to exit class 2"
          } ]
