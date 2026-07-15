namespace Circus.Domain

/// Project-local non-empty list used to collect independent validation
/// errors. Always carries at least one element so the success and error
/// paths are unambiguous.
type NonEmptyList<'value> =
    private
        { Head: 'value
          Tail: 'value list }

module NonEmptyList =
    /// Construct a non-empty list from a single value.
    let singleton (value: 'value) : NonEmptyList<'value> = { Head = value; Tail = [] }

    /// Lift an optional value into a non-empty list, returning None when
    /// there is no value.
    let ofOption (value: 'value option) : NonEmptyList<'value> option =
        match value with
        | Some v -> Some(singleton v)
        | None -> None

    /// Materialise the values into a regular F# list.
    let toList (nel: NonEmptyList<'value>) : 'value list = nel.Head :: nel.Tail

    /// Construct a non-empty list from a head element and a tail list.
    /// The tail list may be empty; the resulting structure is always non-empty.
    let cons (head: 'value) (tail: 'value list) : NonEmptyList<'value> = { Head = head; Tail = tail }

    /// Concatenate two non-empty lists, preserving all values.
    let concat (a: NonEmptyList<'value>) (b: NonEmptyList<'value>) : NonEmptyList<'value> =
        { Head = a.Head
          Tail = a.Tail @ toList b }

    /// Combine two optional non-empty lists. Concatenates when both are
    /// present; returns whichever side is present when only one is. Used
    /// when accumulating independent validation failures.
    let combine (a: NonEmptyList<'value> option) (b: NonEmptyList<'value> option) : NonEmptyList<'value> option =
        match a, b with
        | Some a', Some b' -> Some(concat a' b')
        | Some a', None -> Some a'
        | None, Some b' -> Some b'
        | None, None -> None

    /// Transform every value while preserving the non-empty shape.
    let map (f: 'value -> 'b) (nel: NonEmptyList<'value>) : NonEmptyList<'b> =
        { Head = f nel.Head
          Tail = List.map f nel.Tail }
