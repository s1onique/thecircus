namespace Circus.Persistence.Postgres

open Npgsql

/// Host-supplied PostgreSQL settings.  Only the connection string is honoured
/// in production; the retry count and back-off are owned by the single retry
/// authority inside `IngestEventService`.
type PostgresConfiguration = { ConnectionString: string }

module PostgresConfiguration =
    let defaultConfiguration (connectionString: string) : PostgresConfiguration =
        { ConnectionString = connectionString }

    /// Build exactly one NpgsqlDataSource.  The caller must own and dispose
    /// it; nothing here starts a connection.
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
