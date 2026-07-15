namespace Circus.Persistence.Postgres

open Npgsql

/// Configuration for connecting to PostgreSQL.
type PostgresConfiguration =
    { ConnectionString: string
      MaximumRetries: int
      SerializationRetryLimit: int }

module PostgresConfiguration =
    /// Default configuration using a connection string.
    let defaultConfiguration (connectionString: string) : PostgresConfiguration =
        { ConnectionString = connectionString
          MaximumRetries = 3
          SerializationRetryLimit = 3 }

    /// Create a NpgsqlDataSource from the configuration.
    /// The data source owns connection pooling and is safe to share across requests.
    let createDataSource (config: PostgresConfiguration) : NpgsqlDataSource =
        let builder = NpgsqlDataSourceBuilder config.ConnectionString
        builder.Build()

/// SQLSTATE codes used for classification.
module SqlStates =
    [<Literal>]
    let SerializationFailure = "40001"

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
