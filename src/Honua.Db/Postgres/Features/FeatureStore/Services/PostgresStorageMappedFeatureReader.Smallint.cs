// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.RegularExpressions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;

namespace Honua.Db.Postgres.Features.FeatureStore.Services;

internal sealed partial class PostgresStorageMappedFeatureReader
{
    private sealed record SmallintComparison(string FieldName, string Operator, object Value);

    private SmallintComparison? TryGetSmallintComparison(FeatureQuery query)
    {
        if (!_mapping.IsSourceBacked || !string.IsNullOrWhiteSpace(_mapping.AttributesColumn) ||
            string.IsNullOrWhiteSpace(_mapping.SchemaName) || query.Distinct ||
            query.VersionContext is { IsDefault: false } ||
            query.SqlFilter is not { Parameters.Count: 1 } filter ||
            filter.Parameters[0] is not (int or long))
        {
            return null;
        }

        // Match the complete canonical predicate, never an operand inside arithmetic,
        // an explicit cast, a function, IN/BETWEEN, or a compound expression.
        var text = filter.Sql.Trim();
        var match = SmallintComparisonRegex().Match(text);
        if (!match.Success && text.StartsWith('(') && text.EndsWith(')'))
        {
            match = SmallintComparisonRegex().Match(text[1..^1].Trim());
        }

        if (!match.Success)
        {
            return null;
        }

        var name = match.Groups["field"].Value.Replace("''", "'", StringComparison.Ordinal);
        var field = _resource.SchemaFields.FirstOrDefault(candidate =>
            candidate.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (field is null || TryResolveFieldType(field.Name) != MetadataV2FieldType.Integer ||
            !_mapping.ProviderOptions.TryGetValue(PostgresColumnTypeHints.SmallintColumnPrefix + field.Name, out var hint) ||
            !bool.TryParse(hint, out var isSmallint) || !isSmallint)
        {
            return null;
        }

        return new SmallintComparison(field.Name, match.Groups["op"].Value, filter.Parameters[0]!);
    }

    private string BuildSmallintTypeGuard(SmallintComparison comparison, bool lockRelation)
    {
        var column = ValidateAndQuoteIdentifier(comparison.FieldName);
        var typedValue = lockRelation
            ? $"(SELECT {column} FROM {_qualifiedTableName} LIMIT 0)"
            : $"(NULL::{_qualifiedTableName}).{column}";
        // Preserve the canonical operator's meaning even when a source explicitly puts
        // user-defined operators ahead of pg_catalog in its search path.
        return $"pg_catalog.pg_typeof({typedValue}) OPERATOR(pg_catalog.=) 'pg_catalog.int2'::pg_catalog.regtype" +
               " AND (pg_catalog.current_schemas(true))[1] OPERATOR(pg_catalog.=) 'pg_catalog'";
    }

    private NpgsqlBatch CreateSmallintReadBatch(NpgsqlConnection connection, SqlBuilder sql, bool useSerialPlan)
    {
        var batch = useSerialPlan
            ? CreateSerialSpatialReadBatch(connection, sql)
            : new NpgsqlBatch(connection) { EnableErrorBarriers = false };
        if (!useSerialPlan)
        {
            PostgresSqlSafety.ValidateReadOnlySingleStatement(sql.ToString());
            var queryCommand = new NpgsqlBatchCommand(sql.ToString());
            foreach (var value in sql.Parameters)
            {
                queryCommand.Parameters.AddWithValue(NormalizeParameterValue(value));
            }

            batch.BatchCommands.Add(queryCommand);
        }

        // LIMIT 0 reads no source rows but acquires the source relation lock. Without
        // error barriers, PostgreSQL holds that lock through the following statement
        // in the same implicit transaction/roundtrip. External DDL cannot change the
        // physical type between verification and execution. Domains are not int2.
        batch.BatchCommands.Insert(0, new NpgsqlBatchCommand(
            "SELECT " + BuildSmallintTypeGuard(sql.SmallintComparison!, lockRelation: true)));
        return batch;
    }

    private async Task<FeatureReadSession> OpenFeatureReadSessionAsync(
        FeatureQuery query,
        bool probeLimit,
        bool allowSerialPlan,
        CancellationToken cancellationToken)
    {
        var sql = BuildFeatureSelect(query, probeLimit);
        var session = new FeatureReadSession(await OpenConnectionAsync(cancellationToken).ConfigureAwait(false));
        try
        {
            var connection = session.Connection;
            var useSerialPlan = allowSerialPlan && connection.Transaction is null && ShouldUseSerialSpatialPlan(query);
            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    if (sql.SmallintComparison is not null && connection.Transaction is null &&
                        System.Transactions.Transaction.Current is null)
                    {
                        session.Batch = CreateSmallintReadBatch(connection, sql, useSerialPlan);
                        var guardedReader = await session.Batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        session.Reader = guardedReader;
                        if (!await guardedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("The smallint type-verification batch returned no verdict.");
                        }

                        if (guardedReader.GetBoolean(0))
                        {
                            await AdvanceToSmallintFeaturesAsync(guardedReader, useSerialPlan, cancellationToken).ConfigureAwait(false);
                            session.NativeAttributes = BindNativeAttributeDecoder(sql, guardedReader);
                            return session;
                        }

                        // The second statement's one-time guard suppresses row/RLS/volatile
                        // expression execution on stale hints. A boolean/text column can still
                        // fail operator resolution at parse time, after the first false verdict.
                        // Drain that batch before the single canonical retry. Never replay
                        // feature execution, cancellation or an existing transaction. Cached
                        // result metadata revalidation is handled separately below.
                        try
                        {
                            await AdvanceToSmallintFeaturesAsync(guardedReader, useSerialPlan, cancellationToken).ConfigureAwait(false);
                            if (await guardedReader.ReadAsync(cancellationToken).ConfigureAwait(false))
                            {
                                throw new InvalidOperationException("A rejected smallint query unexpectedly returned features.");
                            }
                        }
                        catch (PostgresException exception) when (
                            exception.SqlState == PostgresErrorCodes.UndefinedFunction && !cancellationToken.IsCancellationRequested)
                        {
                            // PostgreSQL has rolled back the implicit batch transaction.
                        }

                        await session.DisposeQueryAsync().ConfigureAwait(false);
                    }

                    if (sql.SmallintComparison is not null)
                    {
                        sql = BuildFeatureSelectCore(query, probeLimit, comparison: null);
                    }

                    if (useSerialPlan)
                    {
                        session.Batch = CreateSerialSpatialReadBatch(connection, sql);
                        session.Reader = await session.Batch.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                        if (!await session.Reader.NextResultAsync(cancellationToken).ConfigureAwait(false))
                        {
                            throw new InvalidOperationException("The scoped planner batch did not return feature query results.");
                        }
                    }
                    else
                    {
                        session.Command = CreateReadCommand(connection, sql);
                        session.Reader = await session.Command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
                    }

                    session.NativeAttributes = BindNativeAttributeDecoder(sql, session.Reader!);
                    return session;
                }
                catch (PostgresException exception) when (
                    attempt == 0 && sql.NativeAttributeNames.Length > 0 &&
                    connection.Transaction is null && System.Transactions.Transaction.Current is null &&
                    !cancellationToken.IsCancellationRequested && IsNativeCachedResultTypeChange(exception))
                {
                    // This exact top-level revalidation error occurs before the feature
                    // executor starts. Npgsql invalidates the rejected prepared statement.
                    // Finish the implicit rollback before one stable-JSON retry on the same
                    // lease and already secured query; never replay a nested function error,
                    // a caller's transaction, cancellation or an exposed feature row.
                    await session.DisposeQueryAsync().ConfigureAwait(false);
                    cancellationToken.ThrowIfCancellationRequested();
                    sql = BuildFeatureSelectCore(query, probeLimit, comparison: null, useNativeAttributes: false);
                }
            }
        }
        catch
        {
            // Initialization owns every partial resource until it returns the session.
            // Failures, including cancellation before the verdict, release the full lease.
            await session.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private sealed class FeatureReadSession(NpgsqlConnectionLease connection) : IAsyncDisposable
    {
        public NpgsqlConnectionLease Connection { get; } = connection;
        public NpgsqlCommand? Command { get; set; }
        public NpgsqlBatch? Batch { get; set; }
        public NpgsqlDataReader? Reader { get; set; }
        public NativeAttributeDecoder? NativeAttributes { get; set; }

        public async ValueTask DisposeQueryAsync()
        {
            var reader = Reader;
            var batch = Batch;
            var command = Command;
            Reader = null;
            NativeAttributes = null;
            Batch = null;
            Command = null;
            try
            {
                if (reader is not null)
                {
                    await reader.DisposeAsync().ConfigureAwait(false);
                }
            }
            finally
            {
                try
                {
                    if (batch is not null)
                    {
                        await batch.DisposeAsync().ConfigureAwait(false);
                    }
                }
                finally
                {
                    if (command is not null)
                    {
                        await command.DisposeAsync().ConfigureAwait(false);
                    }
                }
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await DisposeQueryAsync().ConfigureAwait(false);
            }
            finally
            {
                await Connection.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private static async Task AdvanceToSmallintFeaturesAsync(
        NpgsqlDataReader reader, bool useSerialPlan, CancellationToken cancellationToken)
    {
        if (!await reader.NextResultAsync(cancellationToken).ConfigureAwait(false) ||
            (useSerialPlan && !await reader.NextResultAsync(cancellationToken).ConfigureAwait(false)))
        {
            throw new InvalidOperationException("The smallint batch did not return feature query results.");
        }
    }

    [GeneratedRegex(
        @"^NULLIF\(\s*(?:""attributes""|attributes)\s*->>\s*'(?<field>(?:''|[^'])+)'\s*,\s*''\s*\)\s*::\s*integer\s*(?<op>>=|<=|!=|<>|=|>|<)\s*@p0$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SmallintComparisonRegex();
}
