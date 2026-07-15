module Circus.Contracts.Tests.Support.Fixtures

open System
open System.IO
open System.Text
open Circus.Domain

/// Helpers for loading human-reviewed JSON fixtures copied beside the test executable.
module Fixtures =
    /// Resolve the fixtures directory relative to the assembly output path.
    /// Uses Path.GetFullPath to normalize the path across platforms.
    let private fixturesRoot () : string =
        Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "fixtures", "events"))

    /// Load `relativePath` (relative to `fixturesRoot`) as UTF-8 text.
    /// All consumers pass paths relative to the `fixtures/events/` directory.
    let readFixture (relativePath: string) : string =
        let full = Path.Combine(fixturesRoot (), relativePath)
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

    /// List all files under the given subdirectory relative to fixturesRoot.
    let private listUnder (relativeDir: string) : string list =
        let root = fixturesRoot ()
        let dir = Path.Combine(root, relativeDir)

        if Directory.Exists dir then
            Directory.GetFiles(dir, "*.json")
            |> Array.map (fun fullPath -> Path.GetRelativePath(root, fullPath))
            |> Array.toList
        else
            []

    /// List of all valid fixture filenames (relative to fixturesRoot).
    let allValid () = listUnder "valid"

    /// List of all invalid-envelope fixture filenames (relative to fixturesRoot).
    let allInvalidEnvelope () = listUnder "invalid-envelope"

    /// List of all invalid-started fixture filenames (relative to fixturesRoot).
    let allInvalidStarted () = listUnder "invalid-started"

    /// List of all invalid-finished fixture filenames (relative to fixturesRoot).
    let allInvalidFinished () = listUnder "invalid-finished"

    /// List of all unknown-event fixture filenames (relative to fixturesRoot).
    let allUnknown () = listUnder "unknown"

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
            | InvalidKnownPayload(_, n) -> Some(NonEmptyList.toList n)
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
