module Circus.Contracts.Tests.Support.Fixtures

open System
open System.IO
open System.Text
open Circus.Domain

/// Embedded-resource helpers for loading the human-reviewed JSON fixtures
/// committed to `tests/fixtures/events/`.
module Fixtures =
    /// Resolve the absolute filesystem path of `tests/fixtures/events/`
    /// by walking up from the test assembly's directory until the repo
    /// root is identified. The resolver is purely filesystem-based so the
    /// suite never depends on the developer's machine-specific layout.
    let eventsDirectory () : string =
        let candidates =
            [
                Path.Combine(AppContext.BaseDirectory, "fixtures", "events")
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "fixtures", "events")
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "fixtures", "events")
                Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "fixtures", "events")
            ]

        candidates
        |> List.tryFind (fun p -> Directory.Exists p)
        |> Option.defaultValue (List.head candidates)

    /// Load `relativePath` (relative to `eventsDirectory`) as UTF-8 text.
    let readFixture (relativePath: string) : string =
        let full = Path.Combine(eventsDirectory (), relativePath)
        File.ReadAllText(full, Encoding.UTF8)

    /// Wrap the UTF-8 bytes of a fixture as a `ReadOnlyMemory<byte>`
    /// without triggering the `byte[]` → `ReadOnlyMemory<byte>` implicit
    /// conversion warning. The contract decoder's entry-point requires
    /// exactly this type.
    let bytes (relativePath: string) : ReadOnlyMemory<byte> =
        ReadOnlyMemory(Encoding.UTF8.GetBytes(readFixture relativePath))

    /// Construct a `ReadOnlyMemory<byte>` directly from raw UTF-8 bytes
    /// (used by inline JSON literals in test bodies).
    let inlineBytes (text: string) : ReadOnlyMemory<byte> =
        ReadOnlyMemory(Encoding.UTF8.GetBytes text)

    /// List of all committed fixture paths grouped by category.
    let allValid () =
        Directory.GetFiles(Path.Combine(eventsDirectory (), "valid"), "*.json")
        |> Array.toList

    let allInvalidEnvelope () =
        Directory.GetFiles(Path.Combine(eventsDirectory (), "invalid-envelope"), "*.json")
        |> Array.toList

    let allInvalidStarted () =
        Directory.GetFiles(Path.Combine(eventsDirectory (), "invalid-started"), "*.json")
        |> Array.toList

    let allInvalidFinished () =
        Directory.GetFiles(Path.Combine(eventsDirectory (), "invalid-finished"), "*.json")
        |> Array.toList

    let allUnknown () =
        Directory.GetFiles(Path.Combine(eventsDirectory (), "unknown"), "*.json")
        |> Array.toList

/// Helpers for asserting against the contract violation union without
/// scattering match expressions across the test modules.
module Assertions =
    open Circus.Contracts

    /// Materialise the `NonEmptyList<ContractViolation>` for assertions.
    let contractViolations (result: ValidationResult<'v>) : ContractViolation list =
        match result with
        | Ok _ -> []
        | Error e -> NonEmptyList.toList e

    /// Materialise the payload violations inside an
    /// `InvalidKnownPayload` violation (or empty if not present).
    let payloadViolations (violations: ContractViolation list) : PayloadViolation list =
        violations
        |> List.choose (function
            | InvalidKnownPayload (_, n) -> Some(NonEmptyList.toList n)
            | _ -> None)
        |> List.concat

    /// True iff any of the contract violations is `InvalidKnownPayload`.
    let hasInvalidKnownPayload (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | InvalidKnownPayload _ -> true
            | _ -> false)

    /// True iff any of the contract violations is `MalformedJson`.
    let hasMalformedJson (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | MalformedJson _ -> true
            | _ -> false)

    /// True iff any of the contract violations is `BodyTooLarge`.
    let hasBodyTooLarge (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | BodyTooLarge _ -> true
            | _ -> false)

    /// True iff any of the contract violations is `RootMustBeObject`.
    let hasRootMustBeObject (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | RootMustBeObject -> true
            | _ -> false)

    /// True iff any of the contract violations is `UnsupportedSpecVersion`.
    let hasUnsupportedSpecVersion (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | UnsupportedSpecVersion _ -> true
            | _ -> false)

    /// True iff any of the contract violations is `SubjectRunIdMismatch`.
    let hasSubjectRunIdMismatch (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | SubjectRunIdMismatch -> true
            | _ -> false)

    /// True iff at least one contract violation is `InvalidExtensionName`.
    let hasInvalidExtensionName (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | InvalidExtensionName _ -> true
            | _ -> false)

    /// True iff at least one envelope violation names the given field.
    let hasMissingField (fieldName: string) (violations: ContractViolation list) : bool =
        violations
        |> List.exists (function
            | MissingField n -> n = fieldName
            | _ -> false)
