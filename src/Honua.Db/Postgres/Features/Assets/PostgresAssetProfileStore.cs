// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Data;
using System.Data.Common;
using System.Text.Json;
using Honua.Core.Features.Assets;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Db.Postgres.Features.Infrastructure;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Db.Postgres.Features.Assets;

internal sealed class PostgresAssetProfileStore : IAssetProfileStore
{
    private const string Columns = """
        profile_id, service_id, layer_id, name, geometry_type, asset_id_field, condition_field, status_field,
        last_inspected_field, last_serviced_field, condition_labels::text, inspection_form_id, service_form_id,
        state, version, created_by, created_at, updated_by, updated_at
        """;

    private const string UniqueViolation = "23505";

    private readonly IAdoNetDatabaseConnectionProvider _connectionProvider;
    private readonly PostgresAssetCommandReceipts _receipts = new();
    private readonly string _table = SchemaSearchPath.QualifyTable("asset_profiles", "honua");

    public PostgresAssetProfileStore(IAdoNetDatabaseConnectionProvider connectionProvider)
    {
        _connectionProvider = connectionProvider ?? throw new ArgumentNullException(nameof(connectionProvider));
    }

    public async Task<AssetProfileListResult> ListAsync(AssetProfileQuery query, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var command = new NpgsqlCommand(
            $"""
            SELECT {Columns} FROM {_table}
            WHERE service_id = @service_id AND (@after::uuid IS NULL OR profile_id > @after)
            ORDER BY profile_id
            LIMIT @limit
            """,
            connection);
        command.Parameters.AddWithValue("service_id", NpgsqlDbType.Text, query.ServiceId);
        command.Parameters.AddWithValue("after", NpgsqlDbType.Uuid, (object?)query.After ?? DBNull.Value);
        command.Parameters.AddWithValue("limit", NpgsqlDbType.Integer, query.Limit + 1);

        var items = new List<AssetProfile>();
        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(Read(reader));
            }
        }

        var hasMore = PostgresAssetReading.TrimToPage(items, query.Limit);

        return new AssetProfileListResult
        {
            Items = items,
            NextCursor = hasMore ? items[^1].ProfileId.ToString("D") : null,
        };
    }

    public async Task<AssetProfile?> GetAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadAsync(connection, profileId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult?> FindReceiptAsync(AssetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await _receipts.FindAsync(connection, null, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult> CreateAsync(AssetProfile profile, AssetCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(command);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (await _receipts.LockAndFindAsync(transaction, command, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        try
        {
            await InsertAsync(connection, transaction, profile, cancellationToken).ConfigureAwait(false);
        }
        catch (PostgresException ex) when (ex.SqlState == UniqueViolation)
        {
            return new AssetCommandResult(AssetCommandStatus.VersionConflict, null, "The layer already has an asset profile.");
        }

        return await AcceptAsync(transaction, profile, command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<AssetCommandResult> UpdateAsync(
        AssetProfile profile,
        long expectedVersion,
        AssetCommand command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(profile);
        ArgumentNullException.ThrowIfNull(command);

        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.ReadCommitted, cancellationToken).ConfigureAwait(false);
        if (await _receipts.LockAndFindAsync(transaction, command, cancellationToken).ConfigureAwait(false) is { } replay)
        {
            return replay;
        }

        var current = await LockAsync(transaction, profile.ProfileId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return AssetCommandResult.NotFound;
        }

        if (current.Version != expectedVersion)
        {
            return AssetCommandResult.VersionConflict(command.CommandId, current.Version, "asset profile");
        }

        var updated = profile with
        {
            Version = current.Version + 1,
            CreatedBy = current.CreatedBy,
            CreatedAt = current.CreatedAt,
        };
        await UpdateRowAsync(connection, transaction, updated, cancellationToken).ConfigureAwait(false);
        return await AcceptAsync(transaction, updated, command, cancellationToken).ConfigureAwait(false);
    }

    private async Task<AssetCommandResult> AcceptAsync(
        NpgsqlTransaction transaction,
        AssetProfile profile,
        AssetCommand command,
        CancellationToken cancellationToken)
    {
        var response = JsonSerializer.Serialize(
            new AssetProfileCommandResponse { Outcome = AssetCommandOutcome.Accepted, CommandId = command.CommandId, Profile = profile },
            AssetsJsonContext.Default.AssetProfileCommandResponse);
        var statusCode = (short)(profile.Version == 1 ? 201 : 200);
        await _receipts.RecordAsync(
            transaction,
            new AssetCommandReceipt(command, AssetCommandSubject.AssetProfile, profile.ProfileId, statusCode, response),
            cancellationToken).ConfigureAwait(false);
        await transaction.CommitSafelyAsync(cancellationToken).ConfigureAwait(false);
        return new AssetCommandResult(AssetCommandStatus.Accepted, response);
    }

    private Task<AssetProfile?> ReadAsync(NpgsqlConnection connection, Guid profileId, CancellationToken cancellationToken)
        => SelectOneAsync(new NpgsqlCommand($"SELECT {Columns} FROM {_table} WHERE profile_id = @profile_id", connection), profileId, cancellationToken);

    private Task<AssetProfile?> LockAsync(NpgsqlTransaction transaction, Guid profileId, CancellationToken cancellationToken)
        => SelectOneAsync(
            new NpgsqlCommand($"SELECT {Columns} FROM {_table} WHERE profile_id = @profile_id FOR NO KEY UPDATE", transaction.Connection, transaction),
            profileId,
            cancellationToken);

    private static async Task<AssetProfile?> SelectOneAsync(NpgsqlCommand command, Guid profileId, CancellationToken cancellationToken)
    {
        await using (command)
        {
            command.Parameters.AddWithValue("profile_id", NpgsqlDbType.Uuid, profileId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
        }
    }

    private async Task InsertAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            INSERT INTO {_table} ({Columns.Replace("::text", string.Empty, StringComparison.Ordinal)})
            VALUES (@profile_id, @service_id, @layer_id, @name, @geometry_type, @asset_id_field, @condition_field,
                @status_field, @last_inspected_field, @last_serviced_field, @condition_labels::jsonb,
                @inspection_form_id, @service_form_id, @state, @version, @created_by, @created_at, @updated_by,
                @updated_at)
            """,
            connection,
            transaction);
        Bind(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task UpdateRowAsync(
        NpgsqlConnection connection,
        NpgsqlTransaction transaction,
        AssetProfile profile,
        CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand(
            $"""
            UPDATE {_table} SET name = @name, geometry_type = @geometry_type, asset_id_field = @asset_id_field, condition_field = @condition_field,
                status_field = @status_field, last_inspected_field = @last_inspected_field,
                last_serviced_field = @last_serviced_field, condition_labels = @condition_labels::jsonb,
                inspection_form_id = @inspection_form_id, service_form_id = @service_form_id, state = @state,
                version = @version, updated_by = @updated_by, updated_at = @updated_at
            WHERE profile_id = @profile_id
            """,
            connection,
            transaction);
        Bind(command, profile);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void Bind(NpgsqlCommand command, AssetProfile profile)
    {
        var parameters = command.Parameters;
        parameters.AddWithValue("profile_id", NpgsqlDbType.Uuid, profile.ProfileId);
        parameters.AddWithValue("service_id", NpgsqlDbType.Text, profile.ServiceId);
        parameters.AddWithValue("layer_id", NpgsqlDbType.Integer, profile.LayerId);
        parameters.AddWithValue("name", NpgsqlDbType.Text, profile.Name);
        parameters.AddWithValue("geometry_type", NpgsqlDbType.Text, profile.GeometryType);
        parameters.AddWithValue("asset_id_field", NpgsqlDbType.Text, profile.Fields.AssetId!);
        parameters.AddWithValue("condition_field", NpgsqlDbType.Text, Nullable(profile.Fields.Condition));
        parameters.AddWithValue("status_field", NpgsqlDbType.Text, Nullable(profile.Fields.Status));
        parameters.AddWithValue("last_inspected_field", NpgsqlDbType.Text, Nullable(profile.Fields.LastInspected));
        parameters.AddWithValue("last_serviced_field", NpgsqlDbType.Text, Nullable(profile.Fields.LastServiced));
        parameters.AddWithValue("condition_labels", NpgsqlDbType.Text, JsonSerializer.Serialize(profile.ConditionLabels, AssetsJsonContext.Default.IReadOnlyListString));
        parameters.AddWithValue("inspection_form_id", NpgsqlDbType.Text, Nullable(profile.InspectionFormId));
        parameters.AddWithValue("service_form_id", NpgsqlDbType.Text, Nullable(profile.ServiceFormId));
        parameters.AddWithValue("state", NpgsqlDbType.Text, profile.State);
        parameters.AddWithValue("version", NpgsqlDbType.Bigint, profile.Version);
        parameters.AddWithValue("created_by", NpgsqlDbType.Text, profile.CreatedBy);
        parameters.AddWithValue("created_at", NpgsqlDbType.TimestampTz, profile.CreatedAt.UtcDateTime);
        parameters.AddWithValue("updated_by", NpgsqlDbType.Text, profile.UpdatedBy);
        parameters.AddWithValue("updated_at", NpgsqlDbType.TimestampTz, profile.UpdatedAt.UtcDateTime);
    }

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

    private static AssetProfile Read(DbDataReader reader) => new()
    {
        ProfileId = reader.GetGuid(0),
        ServiceId = reader.GetString(1),
        LayerId = reader.GetInt32(2),
        Name = reader.GetString(3),
        GeometryType = reader.GetString(4),
        Fields = new AssetProfileFieldMap
        {
            AssetId = reader.GetString(5),
            Condition = PostgresAssetReading.OptionalString(reader, 6),
            Status = PostgresAssetReading.OptionalString(reader, 7),
            LastInspected = PostgresAssetReading.OptionalString(reader, 8),
            LastServiced = PostgresAssetReading.OptionalString(reader, 9),
        },
        ConditionLabels = JsonSerializer.Deserialize(reader.GetString(10), AssetsJsonContext.Default.IReadOnlyListString) ?? [],
        InspectionFormId = PostgresAssetReading.OptionalString(reader, 11),
        ServiceFormId = PostgresAssetReading.OptionalString(reader, 12),
        State = reader.GetString(13),
        Version = reader.GetInt64(14),
        CreatedBy = reader.GetString(15),
        CreatedAt = PostgresAssetReading.RequiredTime(reader, 16),
        UpdatedBy = reader.GetString(17),
        UpdatedAt = PostgresAssetReading.RequiredTime(reader, 18),
    };
}
