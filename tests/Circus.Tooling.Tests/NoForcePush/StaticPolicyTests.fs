module Circus.Tooling.Tests.NoForcePush.StaticPolicyTests

open Expecto
open Circus.Tooling.NoForcePush.StaticPolicy

[<Tests>]
let tests =
    testList
        "NoForcePush StaticPolicy"
        [ test "detects long force option" {
              let result = containsForcePush "git push --force origin main"
              Expect.isTrue result "contains --force"
          }
          test "detects short force option" {
              let result = containsForcePush "git push -f origin main"
              Expect.isTrue result "contains -f"
          }
          test "detects bundled short force option" {
              let result = containsForcePush "git push -uf origin main"
              Expect.isTrue result "contains -uf"
          }
          test "detects force-with-lease" {
              let result = containsForcePush "git push --force-with-lease origin main"
              Expect.isTrue result "contains --force-with-lease"
          }
          test "detects force-if-includes" {
              let result = containsForcePush "git push --force-if-includes origin main"
              Expect.isTrue result "contains --force-if-includes"
          }
          test "does not flag safe push" {
              let result = containsForcePush "git push origin main"
              Expect.isFalse result "safe push not flagged"
          }
          test "detects leading plus refspec" {
              let result = containsLeadingPlusRefspec "git push +main:refs/heads/main"
              Expect.isTrue result "contains leading plus"
          }
          test "detects remote delete" {
              let result = containsRemoteDelete "git push --delete origin main"
              Expect.isTrue result "contains --delete"
          }
          test "detects short delete" {
              let result = containsRemoteDelete "git push -d origin main"
              Expect.isTrue result "contains -d"
          }
          test "detects empty source delete" {
              let result = containsEmptySourceDelete "git push :refs/heads/main"
              Expect.isTrue result "contains empty source delete"
          }
          test "detects mirror option" {
              let result = containsMirrorOrPrune "git push --mirror origin"
              Expect.isTrue result "contains --mirror"
          }
          test "detects prune option" {
              let result = containsMirrorOrPrune "git push --prune origin"
              Expect.isTrue result "contains --prune"
          }
          test "detects no-verify" {
              let result = containsNoVerify "git push --no-verify origin main"
              Expect.isTrue result "contains --no-verify"
          }
          test "detects send-pack" {
              let result = containsSendPack "git send-pack origin main"
              Expect.isTrue result "contains send-pack"
          }
          test "detects hook bypass" {
              let result = containsHookBypass "git push --no-verify origin"
              Expect.isTrue result "contains --no-verify hook bypass"
          }
          test "detects dynamic args" {
              let result = containsDynamicArgs "git push $branch main"
              Expect.isTrue result "contains dynamic args"
          }
          test "detects eval indirection" {
              let result = containsEvalIndirection "eval git push origin"
              Expect.isTrue result "contains eval indirection"
          }
          test "detects gh force" {
              let result = containsGhForce "gh api --field force=true"
              Expect.isTrue result "contains gh force"
          }
          test "detects gh delete ref" {
              // Pattern: gh\s+api\s+.*(?:ref|git)\s+.*(?:delete|remove)
              let result = containsGhDeleteRef "gh api repos owner ref delete"
              Expect.isTrue result "contains gh delete ref"
          }
          test "detects curl delete ref" {
              // Pattern: curl\s+.*DELETE\s+.*(?:ref|git)(?:\s|$)
              let result = containsCurlDeleteRef "curl DELETE https://example.com/ref"
              Expect.isTrue result "contains curl delete ref"
          } ]
