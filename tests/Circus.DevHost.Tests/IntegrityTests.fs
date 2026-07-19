module Circus.DevHost.Tests.IntegrityTests

open Expecto
open Circus.DevHost.Integrity

let tests =
    testList
        "Integrity"
        [ test "sha256OfString matches a fixed fixture" {
              Expect.equal
                  (sha256OfString "circus")
                  "acd8d61b6983eb358899e61e41cedc3b8cdfde68479dcc5710b539df4a13b00e"
                  "The SHA-256 implementation must match the published fixture"
          }

          test "constantTimeEqualHex covers equality, difference, length, and case" {
              Expect.isTrue (constantTimeEqualHex "00aaff" "00aaff") "Equal hashes"
              Expect.isFalse (constantTimeEqualHex "00aaff" "00aafe") "Different hashes"
              Expect.isFalse (constantTimeEqualHex "00aaff" "00aa") "Different lengths"
              Expect.isFalse (constantTimeEqualHex "00aaff" "00AAFF") "Case-only difference"
          } ]
