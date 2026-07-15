namespace Circus.Persistence.Postgres

open System
open System.Threading.Tasks
open Npgsql
open Circus.Application
open Circus.Domain

/// Journal repository provides journal operations using the shared SQL definitions.
type JournalRepository =
    { TryInsert: NpgsqlConnection -> NpgsqlTransaction -> JournalCandidate -> Task<int64 option>
      LookupByIdentity: NpgsqlConnection -> NpgsqlTransaction -> string -> string -> Task<JournalEntry option>
      LookupByStreamPosition: NpgsqlConnection -> NpgsqlTransaction -> string -> System.Guid -> int64 -> Task<JournalEntry option>
      LookupByPosition: NpgsqlConnection -> NpgsqlTransaction -> int64 -> Task<JournalEntry option>
      CheckEnvelopeEqual: NpgsqlConnection -> NpgsqlTransaction -> string -> string -> string -> Task<bool> }

module JournalRepository =

    let create (_dataSource: NpgsqlDataSource) : JournalRepository =
        { TryInsert = JournalSqlExec.tryInsert
          LookupByIdentity = JournalSqlExec.lookupByIdentity
          LookupByStreamPosition = JournalSqlExec.lookupByStreamPosition
          LookupByPosition = JournalSqlExec.lookupByPosition
          CheckEnvelopeEqual = JournalSqlExec.checkEnvelopeEqual }
