namespace Circus.Application

open System
open Circus.Contracts
open Circus.Domain

/// The state of a run projection.
type RunProjectionState =
    | StartedOnly
    | FinishedWithoutStart
    | Completed
    | Conflicted

/// Counts of checks in an execution result.
type CheckCountsDto =
    { Passed: int
      Failed: int
      Skipped: int }

/// The terminal outcome of an execution.
type OutcomeDto =
    | Succeeded
    | Failed
    | Cancelled
    | TimedOut

/// Run projection derived from journal events.
type RunProjection =
    { RunId: RunId
      State: RunProjectionState

      StartedEvent: JournalPosition option
      FinishedEvent: JournalPosition option

      Repository: RepositoryRef option
      ActId: ActId option
      LeamasVersion: LeamasVersion option
      GitRevision: string option
      StartedBy: string option
      StartedAt: DateTimeOffset option

      Outcome: ExecutionOutcome option
      FinishedAt: DateTimeOffset option
      DurationMilliseconds: int64 option
      Summary: string option
      Checks: CheckCounts option

      FirstJournalPosition: JournalPosition
      LastJournalPosition: JournalPosition
      ConflictCount: int
      Version: int64 }

module RunProjection =

    /// Initial version number for a new projection.
    let initialVersion = 1L

    /// Create a projection from the first started event.
    let createFirstStarted
        (started: ExecutionStarted)
        (runId: RunId)
        (observedAt: DateTimeOffset)
        (position: JournalPosition)
        : RunProjection =
        { RunId = runId
          State = StartedOnly
          StartedEvent = Some position
          FinishedEvent = None
          Repository = Some started.Repository
          ActId = started.ActId
          LeamasVersion = Some started.LeamasVersion
          GitRevision = started.GitRevision
          StartedBy = started.StartedBy
          StartedAt = Some observedAt
          Outcome = None
          FinishedAt = None
          DurationMilliseconds = None
          Summary = None
          Checks = None
          FirstJournalPosition = position
          LastJournalPosition = position
          ConflictCount = 0
          Version = initialVersion }

    /// Create a projection from the first finished event (no prior started).
    let createFirstFinished
        (finished: ExecutionFinished)
        (runId: RunId)
        (observedAt: DateTimeOffset)
        (position: JournalPosition)
        : RunProjection =
        { RunId = runId
          State = FinishedWithoutStart
          StartedEvent = None
          FinishedEvent = Some position
          Repository = None
          ActId = None
          LeamasVersion = None
          GitRevision = None
          StartedBy = None
          StartedAt = None
          Outcome = Some finished.Outcome
          FinishedAt = Some observedAt
          DurationMilliseconds = Some finished.DurationMilliseconds
          Summary = finished.Summary
          Checks = Some finished.Checks
          FirstJournalPosition = position
          LastJournalPosition = position
          ConflictCount = 0
          Version = initialVersion }

    /// Determine new state after a started event arrives.
    /// FinishedWithoutStart + first started = Completed.
    /// StartedOnly + first started = Conflict (handled in match).
    let private startedTargetState (current: RunProjectionState) : RunProjectionState =
        match current with
        | FinishedWithoutStart -> Completed
        | StartedOnly -> StartedOnly
        | Completed -> Completed
        | Conflicted -> Conflicted

    /// Apply an ExecutionStartedEvent to a projection.
    /// Once Conflicted, always Conflicted (monotonic state).
    /// FinishedWithoutStart + first started = Completed.
    let applyStarted
        (started: ExecutionStarted)
        (observedAt: DateTimeOffset)
        (position: JournalPosition)
        (current: RunProjection)
        : RunProjection =
        match current.State with
        | Conflicted ->
            // Monotonic: conflict state never reverts
            { current with
                ConflictCount = current.ConflictCount + 1
                LastJournalPosition = position
                Version = current.Version + 1L }
        | _ ->
            match current.StartedEvent with
            | None ->
                // First started event - become authoritative
                { current with
                    State = startedTargetState current.State
                    StartedEvent = Some position
                    Repository = Some started.Repository
                    ActId = started.ActId
                    LeamasVersion = Some started.LeamasVersion
                    GitRevision = started.GitRevision
                    StartedBy = started.StartedBy
                    StartedAt = Some observedAt
                    LastJournalPosition = position
                    Version = current.Version + 1L }
            | Some _ ->
                // Second started event - mark conflict
                { current with
                    State = Conflicted
                    ConflictCount = current.ConflictCount + 1
                    LastJournalPosition = position
                    Version = current.Version + 1L }

    /// Determine new state after a finished event arrives.
    let private finishedTargetState (current: RunProjectionState) : RunProjectionState =
        match current with
        | StartedOnly -> Completed
        | FinishedWithoutStart -> FinishedWithoutStart
        | Completed -> Completed
        | Conflicted -> Conflicted

    /// Apply an ExecutionFinishedEvent to a projection.
    /// Once Conflicted, always Conflicted (monotonic state).
    /// StartedOnly + first finished = Completed.
    /// FinishedWithoutStart + first finished = FinishedWithoutStart (no started authority).
    let applyFinished
        (finished: ExecutionFinished)
        (observedAt: DateTimeOffset)
        (position: JournalPosition)
        (current: RunProjection)
        : RunProjection =
        match current.State with
        | Conflicted ->
            // Monotonic: conflict state never reverts
            { current with
                ConflictCount = current.ConflictCount + 1
                LastJournalPosition = position
                Version = current.Version + 1L }
        | _ ->
            match current.FinishedEvent with
            | None ->
                // First finished event - determine new state
                { current with
                    State = finishedTargetState current.State
                    FinishedEvent = Some position
                    Outcome = Some finished.Outcome
                    FinishedAt = Some observedAt
                    DurationMilliseconds = Some finished.DurationMilliseconds
                    Summary = finished.Summary
                    Checks = Some finished.Checks
                    LastJournalPosition = position
                    Version = current.Version + 1L }
            | Some _ ->
                // Second finished event - mark conflict
                { current with
                    State = Conflicted
                    ConflictCount = current.ConflictCount + 1
                    LastJournalPosition = position
                    Version = current.Version + 1L }

    /// Apply a validated event to a run projection.
    /// Unknown event types do not create or mutate a projection.
    /// Replays do not call this function (they are classified before reaching here).
    let applyEvent
        (current: RunProjection option)
        (journalPosition: JournalPosition)
        (event: ValidatedEvent)
        : RunProjection option =

        // Unknown event types are journaled but do not affect projection
        if not (JournalDecision.isKnownEventType (EventType.value event.EventType)) then
            current

        else
            let runId = event.RunId
            let observedAt = event.ObservedAt

            match event.Event with
            | ExecutionStartedEvent started ->
                match current with
                | None ->
                    // First event is a started - create directly
                    Some(createFirstStarted started runId observedAt journalPosition)
                | Some proj -> Some(applyStarted started observedAt journalPosition proj)
            | ExecutionFinishedEvent finished ->
                match current with
                | None ->
                    // First event is a finished - create directly
                    Some(createFirstFinished finished runId observedAt journalPosition)
                | Some proj -> Some(applyFinished finished observedAt journalPosition proj)
            | UnrecognizedEvent _ ->
                // Already filtered above
                current

    /// Rebuild all run projections from a list of (journal position, validated event) pairs.
    /// The list must be in ascending journal_position order.
    let rebuild (events: (JournalPosition * ValidatedEvent) list) : Map<RunId, RunProjection> =
        let folder (acc: Map<RunId, RunProjection>) (positionAndEvent: JournalPosition * ValidatedEvent) =
            let position, event = positionAndEvent
            let runId = event.RunId
            let current = Map.tryFind runId acc
            let updated = applyEvent current position event

            match updated with
            | Some proj -> Map.add runId proj acc
            | None -> acc

        List.fold folder Map.empty events
