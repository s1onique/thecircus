namespace Circus.Domain

open System

/// Preserved, semantically valid JSON text. Carries no borrowed JsonElement
/// or other live resource. Used to retain unknown extension values and the
/// `data` payload of unrecognized events without retaining a disposed
/// `JsonDocument`.
type RawJson = private RawJson of string

module RawJson =
    /// Extract the underlying JSON text.
    let value (RawJson text) = text

    /// Construct a RawJson from a string that has already been validated as
    /// semantically valid JSON.
    let private wrap (text: string) : RawJson = RawJson text

    /// Project a known-valid RawJson value.
    let unsafeOfString (text: string) : RawJson = wrap text

/// CloudEvents producer-defined event identifier. Any non-empty string up
/// to 128 characters; CloudEvents intentionally does not constrain IDs to
/// UUIDs.
type EventId = private EventId of string

module EventId =
    /// Extract the underlying string.
    let value (EventId v) = v

    /// Maximum length of an event identifier in characters.
    let maxLength = 128

    /// Attempt to construct an EventId from a candidate string.
    let tryCreate (text: string) : EventId option =
        if String.IsNullOrEmpty text then None
        elif text.Length > maxLength then None
        else Some(EventId text)

/// CloudEvents source - a URI reference identifying the producer.
type EventSource = private EventSource of string

module EventSource =
    let value (EventSource v) = v

    /// Maximum length of a source URI reference in characters.
    let maxLength = 512

    /// Attempt to construct an EventSource. Must be non-empty and within the
    /// documented length limit. The Circus examples use URN syntax such as
    /// `urn:leamas:instance:builder-07`, but the contract accepts any
    /// non-empty URI reference.
    let tryCreate (text: string) : EventSource option =
        if String.IsNullOrEmpty text then None
        elif text.Length > maxLength then None
        else Some(EventSource text)

/// CloudEvents type identifier (e.g. `io.leamas.execution.started.v1`).
type EventType = private EventType of string

module EventType =
    let value (EventType v) = v

    /// Maximum length of an event type in characters.
    let maxLength = 255

    let tryCreate (text: string) : EventType option =
        if String.IsNullOrEmpty text then None
        elif text.Length > maxLength then None
        else Some(EventType text)

/// Circus instance identifier - the local Leamas agent name.
type InstanceId = private InstanceId of string

module InstanceId =
    let value (InstanceId v) = v

    /// Maximum length of an instance identifier in characters.
    let maxLength = 128

    let tryCreate (text: string) : InstanceId option =
        if String.IsNullOrEmpty text then None
        elif text.Length > maxLength then None
        else Some(InstanceId text)

/// Circus epoch identifier. UUIDs identify the time window during which a
/// given Leamas instance is producing a contiguous event sequence.
type EpochId = private EpochId of Guid

module EpochId =
    let value (EpochId v) = v

    /// Construct an EpochId from a UUID value, treating `Guid.Empty` as
    /// invalid because it carries no information.
    let tryCreate (value: Guid) : EpochId option =
        if value = Guid.Empty then None else Some(EpochId value)

/// Circus sequence number for an instance within an epoch. Zero or greater
/// but bounded by `Int64.MaxValue` for forward compatibility with future
/// overflow handling.
type EventSequence = private EventSequence of int64

module EventSequence =
    let value (EventSequence v) = v

    let tryCreate (value: int64) : EventSequence option =
        if value < 0L then None else Some(EventSequence value)

/// Run identifier (UUID). Identifies a single Leamas execution attempt.
type RunId = private RunId of Guid

module RunId =
    let value (RunId v) = v

    let tryCreate (value: Guid) : RunId option =
        if value = Guid.Empty then None else Some(RunId value)

/// Repository reference string identifying the repository that was the
/// subject of an execution.
type RepositoryRef = private RepositoryRef of string

module RepositoryRef =
    let value (RepositoryRef v) = v

    /// Lower bound (inclusive) for the length of a repository reference.
    let minLength = 1

    /// Upper bound (inclusive) for the length of a repository reference.
    let maxLength = 256

    let tryCreate (text: string) : RepositoryRef option =
        if String.IsNullOrEmpty text then None
        elif text.Length < minLength then None
        elif text.Length > maxLength then None
        else Some(RepositoryRef text)

/// Optional Act identifier. When present, the value is between 1 and 256
/// characters inclusive.
type ActId = private ActId of string

module ActId =
    let value (ActId v) = v

    /// Lower bound (inclusive) for the length of an Act identifier.
    let minLength = 1

    /// Upper bound (inclusive) for the length of an Act identifier.
    let maxLength = 256

    let tryCreate (text: string) : ActId option =
        if String.IsNullOrEmpty text then None
        elif text.Length < minLength then None
        elif text.Length > maxLength then None
        else Some(ActId text)

/// Leamas version string identifying the producing agent version.
type LeamasVersion = private LeamasVersion of string

module LeamasVersion =
    let value (LeamasVersion v) = v

    /// Lower bound (inclusive) for the length of a Leamas version string.
    let minLength = 1

    /// Upper bound (inclusive) for the length of a Leamas version string.
    let maxLength = 128

    let tryCreate (text: string) : LeamasVersion option =
        if String.IsNullOrEmpty text then None
        elif text.Length < minLength then None
        elif text.Length > maxLength then None
        else Some(LeamasVersion text)
