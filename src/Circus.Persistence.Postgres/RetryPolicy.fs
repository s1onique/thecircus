namespace Circus.Persistence.Postgres

open System.Threading.Tasks
open Circus.Application

type RetryOperationResult<'value> =
    | RetrySucceeded of 'value
    | RetryableFailure
    | PermanentFailure of PersistenceFailure

/// Small, deterministic retry authority used by the PostgreSQL adapter.  The
/// operation supplied to it must represent one complete transaction attempt;
/// callers never reuse a failed connection or transaction.
module RetryPolicy =
    let execute
        (maximumAttempts: int)
        (delay: int -> Task<unit>)
        (operation: int -> Task<RetryOperationResult<'value>>)
        : Task<Result<'value, PersistenceFailure>> =
        if maximumAttempts < 1 then
            invalidArg "maximumAttempts" "must be at least one"

        let rec loop attempt =
            task {
                let! result = operation attempt

                match result with
                | RetrySucceeded value -> return Ok value
                | PermanentFailure failure -> return Error failure
                | RetryableFailure when attempt < maximumAttempts ->
                    do! delay attempt
                    return! loop (attempt + 1)
                | RetryableFailure -> return Error SerializationRetriesExhausted
            }

        loop 1
