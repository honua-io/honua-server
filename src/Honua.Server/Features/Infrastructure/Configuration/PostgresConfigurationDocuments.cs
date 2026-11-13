// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.ControlPlane;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed record ConfigurationCollection<T>(string Kind, JsonTypeInfo<T> Type);
internal sealed record ConfigurationDocument<T>(string Key, long Revision, T Value);
internal sealed record ConfigurationSnapshot<T>(long Generation, IReadOnlyList<ConfigurationDocument<T>> Documents)
{
    public IReadOnlyList<string> UnreadableKeys { get; init; } = [];
}
internal sealed record ConfigurationWrite<T>(string Key, long ExpectedRevision, T? Value);
internal sealed record ConfigurationChange(string Kind, string Key, long ExpectedRevision, byte[]? Payload);

internal sealed partial class PostgresConfigurationDocuments(
    JobStoreDatabase database, IDataProtectionProvider protection, ILogger<PostgresConfigurationDocuments>? logger = null)
    : IConfigurationGenerationSource
{
    private readonly ILogger _logger = logger ?? (ILogger)NullLogger.Instance;
    private readonly string _documents = database.Table("configuration_documents");
    private readonly string _generation = database.Table("configuration_generation");

    public Task<long> GetGenerationAsync(CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"SELECT generation FROM {_generation} WHERE singleton", connection);
            return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Configuration generation is unavailable."));
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<ConfigurationSnapshot<T>> ReadAsync<T>(ConfigurationCollection<T> collection, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                SELECT generation.generation, document.key, document.revision, document.payload
                FROM {_generation} generation LEFT JOIN {_documents} document ON document.kind = @kind
                WHERE generation.singleton ORDER BY document.key
                """, connection);
            command.Parameters.AddWithValue("kind", collection.Kind);
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var documents = new List<ConfigurationDocument<T>>();
            var unreadable = new List<string>();
            long? generation = null;
            var protector = Protector(collection.Kind);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                generation = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    var key = reader.GetString(1);
                    // An unreadable document is left out (fails closed for that entry) so it cannot take down every request.
                    try
                    {
                        var value = JsonSerializer.Deserialize(protector.Unprotect(reader.GetFieldValue<byte[]>(3)), collection.Type)
                            ?? throw new JsonException("Configuration document is empty.");
                        documents.Add(new(key, reader.GetInt64(2), value));
                    }
                    catch (Exception exception) when (exception is CryptographicException or JsonException)
                    {
                        DocumentUnreadable(_logger, collection.Kind, key, exception);
                        unreadable.Add(key);
                    }
                }
            }
            return new ConfigurationSnapshot<T>(generation ?? throw new InvalidOperationException("Configuration generation is unavailable."), documents)
            {
                UnreadableKeys = unreadable
            };
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<bool> WriteAsync<T>(ConfigurationCollection<T> collection, ConfigurationWrite<T> write, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            await LockGenerationAsync(connection, token).ConfigureAwait(false);
            await using var command = CreateWriteCommand(connection, PrepareWrite(collection, write));
            var written = await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return written;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    public ConfigurationChange PrepareWrite<T>(ConfigurationCollection<T> collection, ConfigurationWrite<T> write)
        => new(collection.Kind, write.Key, write.ExpectedRevision, write.Value is null ? null
            : Protector(collection.Kind).Protect(JsonSerializer.SerializeToUtf8Bytes(write.Value, collection.Type)));

    // Lock the generation before checking the read snapshot: concurrent admin writes either precede
    // this reconciliation or force it to re-read. Profiles and their ownership manifest commit together.
    public Task<bool> WriteBatchAsync(long expectedGeneration, IReadOnlyList<ConfigurationChange> changes, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            await using var transaction = await connection.BeginTransactionAsync(token).ConfigureAwait(false);
            if (await LockGenerationAsync(connection, token).ConfigureAwait(false) != expectedGeneration) { return false; }
            foreach (var change in changes)
            {
                await using var command = CreateWriteCommand(connection, change);
                if (await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) != 1) { return false; }
            }
            await transaction.CommitAsync(token).ConfigureAwait(false);
            return true;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    private async Task<long> LockGenerationAsync(NpgsqlConnection connection, CancellationToken token)
    {
        await using var command = new NpgsqlCommand($"SELECT generation FROM {_generation} WHERE singleton FOR UPDATE", connection);
        return (long)(await command.ExecuteScalarAsync(token).ConfigureAwait(false)
            ?? throw new InvalidOperationException("Configuration generation is unavailable."));
    }

    private NpgsqlCommand CreateWriteCommand(NpgsqlConnection connection, ConfigurationChange change)
    {
        var statement = change.Payload is null
            ? $"DELETE FROM {_documents} WHERE kind = @kind AND key = @key AND revision = @revision"
            : change.ExpectedRevision == 0
                ? $"INSERT INTO {_documents} (kind, key, payload) VALUES (@kind, @key, @payload) ON CONFLICT DO NOTHING"
                : $"UPDATE {_documents} SET payload = @payload, revision = revision + 1 WHERE kind = @kind AND key = @key AND revision = @revision";
        var command = new NpgsqlCommand(statement, connection);
        command.Parameters.AddWithValue("kind", change.Kind);
        command.Parameters.AddWithValue("key", change.Key);
        command.Parameters.AddWithValue("revision", change.ExpectedRevision);
        if (change.Payload is not null) { command.Parameters.AddWithValue("payload", NpgsqlDbType.Bytea, change.Payload); }
        return command;
    }

    private IDataProtector Protector(string kind) => protection.CreateProtector("Honua.ConfigurationDocuments", kind);

    [LoggerMessage(Level = LogLevel.Error, Message = "Configuration document {Kind}/{Key} could not be read and is ignored.")]
    private static partial void DocumentUnreadable(ILogger logger, string kind, string key, Exception exception);
}
