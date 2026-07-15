module Circus.Application.Tests.RetryPolicyTests

open System.Threading.Tasks
open Expecto
open Circus.Application
open Circus.Persistence.Postgres

let private noDelay (_: int) = Task.FromResult(())

let tests =
    testList
        "Bounded retry policy"
        [ test "retry success reruns the complete operation and stops at success" {
              let mutable attempts = 0
              let mutable delays = 0

              let delay attempt =
                  delays <- delays + 1
                  Task.FromResult(())

              let operation attempt =
                  attempts <- attempts + 1

                  Task.FromResult(
                      if attempt < 3 then
                          RetryableFailure
                      else
                          RetrySucceeded "committed"
                  )

              match
                  RetryPolicy.execute 5 delay operation
                  |> fun task -> task.GetAwaiter().GetResult()
              with
              | Ok value ->
                  Expect.equal value "committed" "Successful retry result"
                  Expect.equal attempts 3 "Exactly three complete attempts"
                  Expect.equal delays 2 "No delay after success"
              | Error failure -> failwithf "Expected success, got %A" failure
          }

          test "retry exhaustion uses exactly the configured maximum" {
              let mutable attempts = 0

              let operation _ =
                  attempts <- attempts + 1
                  Task.FromResult RetryableFailure

              match
                  RetryPolicy.execute 3 noDelay operation
                  |> fun task -> task.GetAwaiter().GetResult()
              with
              | Error SerializationRetriesExhausted -> Expect.equal attempts 3 "No hidden fourth attempt"
              | other -> failwithf "Expected typed exhaustion, got %A" other
          }

          test "permanent failures are never retried" {
              let mutable attempts = 0

              let operation _ =
                  attempts <- attempts + 1
                  Task.FromResult(PermanentFailure ProjectionInvariantFailed)

              match
                  RetryPolicy.execute 5 noDelay operation
                  |> fun task -> task.GetAwaiter().GetResult()
              with
              | Error ProjectionInvariantFailed -> Expect.equal attempts 1 "Invariant failure is not retried"
              | other -> failwithf "Expected permanent failure, got %A" other
          } ]
