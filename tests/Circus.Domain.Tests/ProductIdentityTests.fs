module Circus.Domain.Tests.ProductIdentityTests

open Expecto
open Circus.Domain

let tests =
    testList
        "ProductIdentity"
        [ test "current product name is 'The Circus'" {
              let identity = ProductIdentity.current
              let name = ProductName.value identity.Name
              Expect.equal name "The Circus" "Product name should be 'The Circus'"
          }

          test "current tagline is 'Team-scale Leamas'" {
              let identity = ProductIdentity.current
              let tagline = ProductTagline.value identity.Tagline
              Expect.equal tagline "Team-scale Leamas" "Tagline should be 'Team-scale Leamas'"
          }

          test "current description is the canonical product description" {
              let identity = ProductIdentity.current
              let description = ProductDescription.value identity.Description

              Expect.equal
                  description
                  "The team-scale coordination, evidence, and governance platform for Leamas."
                  "Description should match canonical value"
          }

          test "value accessors preserve canonical values" {
              let identity = ProductIdentity.current
              Expect.equal (ProductName.value identity.Name) "The Circus" "Name accessor should work"
              Expect.equal (ProductTagline.value identity.Tagline) "Team-scale Leamas" "Tagline accessor should work"

              Expect.equal
                  (ProductDescription.value identity.Description)
                  "The team-scale coordination, evidence, and governance platform for Leamas."
                  "Description accessor should work"
          } ]
