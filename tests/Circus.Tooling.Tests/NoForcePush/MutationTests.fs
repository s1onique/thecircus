module Circus.Tooling.Tests.NoForcePush.MutationTests

/// No-force-push mutation test suite.
/// Tests verify that force-push patterns are correctly detected.

open System
open System.IO
open Expecto
open Circus.Tooling.NoForcePush.StaticPolicy

// Test data for force-push patterns
let private forcePushCommands =
    [ "git push --force origin main"
      "git push -f origin main"
      "git push -uf origin main"
      "git push --force-with-lease origin main"
      "git push --force-if-includes origin main"
      "git push +main:refs/heads/main origin"
      "git push origin :refs/heads/main" ]

let private safePushCommands =
    [ "git push origin main"
      "git fetch origin main"
      "git pull origin main"
      "git clone https://github.com/example/repo.git" ]

[<Tests>]
let tests =
    testList
        "NoForcePush Mutation"
        [ test "force push commands are detected" {
              for cmd in forcePushCommands do
                  let detected =
                      containsForcePush cmd
                      || containsLeadingPlusRefspec cmd
                      || containsEmptySourceDelete cmd

                  Expect.isTrue detected (sprintf "should detect: %s" cmd)
          }
          test "safe push commands are not flagged" {
              for cmd in safePushCommands do
                  let detected = containsForcePush cmd
                  Expect.isFalse detected (sprintf "should not detect: %s" cmd)
          }
          test "force option detection is case insensitive" {
              Expect.isTrue (containsForcePush "git push --FORCE origin main") "uppercase --FORCE"
              Expect.isTrue (containsForcePush "git push -F origin main") "uppercase -F"
          }
          test "gh api force field is detected" {
              Expect.isTrue (containsGhForce "gh api --field force=true") "gh force field"
          }
          test "gh api delete ref is detected" {
              // ghDeleteRefPattern = gh\s+api\s+.*(?:ref|git)\s+.*(?:delete|remove)
              Expect.isTrue (containsGhDeleteRef "gh api repos owner ref delete") "gh delete ref"
          }
          test "curl delete ref is detected" {
              // curlDeleteRefPattern = curl\s+.*DELETE\s+.*(?:ref|git)(?:\s|$)
              Expect.isTrue (containsCurlDeleteRef "curl DELETE https://example.com/ref") "curl delete ref"
          } ]
