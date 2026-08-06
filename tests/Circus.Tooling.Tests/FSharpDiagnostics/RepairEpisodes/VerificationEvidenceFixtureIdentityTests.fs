module Circus.Tooling.Tests.FSharpDiagnostics.RepairEpisodes.VerificationEvidenceFixtureIdentityTests

// =============================================================================
// Verification Evidence Fixture Identity Tests
//
// ACT-CIRCUS-FSHARP-DIAGNOSTIC-VERIFICATION-EVIDENCE-ALIAS-CONTRACT-CLOSURE01-CORRECTION03:
// Spec §15 — deterministic fixture evidence-ID authority.
//
//   framing:
//     domain_separator: verification-evidence-alias-fixture-v1
//     separator: NUL
//     digest: SHA-256
//     encoding: lowercase hexadecimal
//     length: 64
//
// Required assertions:
//   1. output length equals 64
//   2. output contains only [0-9a-f]
//   3. same test-case key yields the same ID
//   4. all distinct keys used by this focused suite yield distinct IDs
//
// These tests do NOT claim mathematical collision impossibility; they only
// prove uniqueness across the test population.
// =============================================================================

open Expecto

open VerificationEvidenceAliasFixture

// -----------------------------------------------------------------------------
// Spec §15 — fixture evidence-ID authority
// -----------------------------------------------------------------------------

[<Tests>]
let fixtureIdentityTests =
    testList "fixture evidence-ID authority" [
        // 1. output length equals 64
        test "evidenceId output length equals 64" {
            let id1 = evidenceId "focused-suite-key-a"
            let id2 = evidenceId "focused-suite-key-b"
            let id3 = evidenceId "focused-suite-key-c"
            Expect.equal id1.Length 64 "length must be 64"
            Expect.equal id2.Length 64 "length must be 64"
            Expect.equal id3.Length 64 "length must be 64"
        }
        // 2. output contains only [0-9a-f]
        test "evidenceId output contains only lowercase hex characters" {
            let id = evidenceId "focused-suite-key-hex"
            let allHex =
                id
                |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
            Expect.isTrue allHex "all characters must be lowercase hex"
        }
        // 3. same test-case key yields the same ID
        test "evidenceId is deterministic for a given key" {
            let a = evidenceId "deterministic-key"
            let b = evidenceId "deterministic-key"
            let c = evidenceId "deterministic-key"
            Expect.equal a b "first vs second call must match"
            Expect.equal b c "second vs third call must match"
        }
        // 4. all distinct keys used by this focused suite yield distinct IDs
        test "all distinct focused-suite keys produce distinct IDs" {
            // Mirror the test-case keys used by the matrix tests so that the
            // uniqueness property is exercised over the actual focused
            // population, not over an arbitrary set of strings.
            let keys: string list =
                [
                    // kind matrix
                    "kind-canonical-only"
                    "kind-alias-only"
                    "kind-both-equal"
                    "kind-both-diff"
                    "kind-cw-av"
                    "kind-cv-aw"
                    "kind-both-wrong"
                    // status matrix
                    "status-canonical-only"
                    "status-alias-only"
                    "status-both-equal"
                    "status-both-diff"
                    "status-cw-av"
                    "status-cv-aw"
                    "status-both-wrong"
                    // command matrix
                    "cmd-canonical-only"
                    "cmd-alias-only"
                    "cmd-both-equal"
                    "cmd-both-diff"
                    "cmd-cw-av"
                    "cmd-cv-aw"
                    "cmd-both-wrong"
                    // exit_code matrix
                    "ec-canonical-only"
                    "ec-alias-only"
                    "ec-both-equal"
                    "ec-both-diff"
                    "ec-cw-av"
                    "ec-cv-aw"
                    "ec-both-wrong"
                    "ec-canon-frac"
                    "ec-alias-frac"
                    "ec-both-frac"
                    "ec-overrange"
                    "ec-neg"
                    // precedence matrix
                    "prec-kind-status"
                    "prec-status-cmd"
                    "prec-cmd-ec"
                    "prec-all"
                    "prec-all-reorder"
                    // raw-duplicate matrix
                    "dup-canon-2"
                    "dup-alias-2"
                    "dup-canon-3"
                    "dup-multi"
                    "dup-multi-shuffled"
                    "dup-case-sensitive"
                ]
            let ids = keys |> List.map evidenceId

            // 1. every key yields a 64-char hex string
            for k in keys do
                let id = evidenceId k
                Expect.equal id.Length 64 (sprintf "length for key %s" k)
                let allHex =
                    id
                    |> Seq.forall (fun c -> (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f'))
                Expect.isTrue allHex (sprintf "hex for key %s" k)

            // 2. all IDs across the focused population are distinct
            let uniqueIds = ids |> List.distinct
            Expect.equal ids.Length uniqueIds.Length "every distinct key must produce a distinct ID"
        }
    ]
