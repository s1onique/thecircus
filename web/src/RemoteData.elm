module RemoteData exposing (RemoteData(..), fromResult, map)

{-| Closed type representing the four states of remote data.

The four states are mutually exclusive: a value is exactly one of
`NotAsked`, `Loading`, `Failure`, or `Success`. No boolean combination is
permitted.

-}


type RemoteData error value
    = NotAsked
    | Loading
    | Failure error
    | Success value


{-| Convert a `Result` into the matching remote-data state.
-}
fromResult : Result error value -> RemoteData error value
fromResult result =
    case result of
        Ok value ->
            Success value

        Err error ->
            Failure error


{-| Map the success value of a `RemoteData`, leaving the other states alone.
-}
map : (a -> b) -> RemoteData error a -> RemoteData error b
map f remote =
    case remote of
        Success value ->
            Success (f value)

        NotAsked ->
            NotAsked

        Loading ->
            Loading

        Failure error ->
            Failure error
