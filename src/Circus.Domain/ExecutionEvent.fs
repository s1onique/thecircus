namespace Circus.Domain

/// The four mutually exclusive terminal states an execution can reach.
type ExecutionOutcome =
    | Succeeded
    | Failed
    | Cancelled
    | TimedOut

module ExecutionOutcome =
    /// Lower-case canonical wire form used in the `outcome` payload field.
    let toWire (outcome: ExecutionOutcome) : string =
        match outcome with
        | Succeeded -> "succeeded"
        | Failed -> "failed"
        | Cancelled -> "cancelled"
        | TimedOut -> "timed_out"

    /// Attempt to parse the canonical wire form. Unknown strings yield
    /// `None` so the caller can produce a typed validation violation
    /// rather than map to an "Unknown" outcome state.
    let tryFromWire (text: string) : ExecutionOutcome option =
        match text with
        | "succeeded" -> Some Succeeded
        | "failed" -> Some Failed
        | "cancelled" -> Some Cancelled
        | "timed_out" -> Some TimedOut
        | _ -> None

/// Counts of verification checks performed during an execution.
type CheckCounts =
    { Passed: int
      Failed: int
      Skipped: int }

module CheckCounts =
    /// Construct a CheckCounts record from raw integers after validating
    /// each count is within the documented 0..1_000_000 inclusive range.
    let tryCreate (passed: int) (failed: int) (skipped: int) : CheckCounts option =
        let bound = 1_000_000

        let valid value = value >= 0 && value <= bound

        if valid passed && valid failed && valid skipped then
            Some
                { Passed = passed
                  Failed = failed
                  Skipped = skipped }
        else
            None

/// Payload of an `io.leamas.execution.started.v1` event.
type ExecutionStarted =
    { RunId: RunId
      Repository: RepositoryRef
      ActId: ActId option
      LeamasVersion: LeamasVersion
      GitRevision: string option
      StartedBy: string option }

/// Payload of an `io.leamas.execution.finished.v1` event.
type ExecutionFinished =
    { RunId: RunId
      Outcome: ExecutionOutcome
      DurationMilliseconds: int64
      Summary: string option
      Checks: CheckCounts }

/// A structurally valid envelope whose `type` is not recognised by the
/// current Circus contract. The original event type and raw `data` payload
/// are preserved so future contract versions can promote the event without
/// losing information.
type UnrecognizedExecutionEvent =
    { EventType: string
      Data: RawJson option }

/// The three shape variants the Circus contract can produce from a
/// validated envelope.
type ExecutionEvent =
    | ExecutionStartedEvent of ExecutionStarted
    | ExecutionFinishedEvent of ExecutionFinished
    | UnrecognizedEvent of UnrecognizedExecutionEvent
