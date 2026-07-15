namespace Circus.Persistence.Postgres

open Npgsql

/// Host-supplied PostgreSQL settings.  The connection string is never
/// constructed from defaults and is never logged by this library.
type PostgresConfiguration =
    { ConnectionString: string
      MaximumRetries: int
      RetryDelayMilliseconds: int }

module PostgresConfiguration =
    let defaultConfiguration (connectionString: string) : PostgresConfiguration =
        { ConnectionString = connectionString
          MaximumRetries = 3
          RetryDelayMilliseconds = 25 }

    let createDataSource (config: PostgresConfiguration) : NpgsqlDataSource =
        if System.String.IsNullOrWhiteSpace config.ConnectionString then
            invalidArg "connectionString" "CIRCUS_DATABASE_URL must not be empty"

        let builder = NpgsqlDataSourceBuilder(config.ConnectionString)
        builder.Build()

module SqlStates =
    [<Literal>]
    let SerializationFailure = "40001"

    [<Literal>]
    let DeadlockDetected = "40P01"

    [<Literal>]
    let UniqueViolation = "23505"

    [<Literal>]
    let QueryCanceled = "57014"

    [<Literal>]
    let ConnectionException = "08000"

    [<Literal>]
    let ConnectionDoesNotExist = "08003"

    [<Literal>]
    let ConnectionFailure = "08006"
