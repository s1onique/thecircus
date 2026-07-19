module Circus.DevHost.ExitCodes

/// Conventional process exit codes for `circus-dev`.
module Code =
    /// Command completed successfully — every required capability is verified
    /// and there are no contract violations.
    let success = 0

    /// One or more required capabilities failed. The host may be re-usable
    /// after running `circus-dev bootstrap`.
    let capabilityFailure = 1

    /// Invocation error, repository identity mismatch, unsupported host,
    /// or malformed authority contract. The program never silently fells a
    /// required failure into this bucket.
    let contractError = 2

open Domain

let ofClass (c: ExitClass) : int =
    match c with
    | Success -> Code.success
    | CapabilityFailure -> Code.capabilityFailure
    | ContractError -> Code.contractError
