// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Honua.ControlPlane;
using Honua.Core.Features.Metadata.Abstractions;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal sealed record ConfigurationCollection<T>(string Kind, JsonTypeInfo<T> Type);
internal sealed record ConfigurationDocument<T>(string Key, long Revision, T Value);
internal sealed record ConfigurationSnapshot<T>(long Generation, IReadOnlyList<ConfigurationDocument<T>> Documents);
internal sealed record ConfigurationWrite<T>(string Key, long ExpectedRevision, T? Value);

internal sealed class PostgresConfigurationDocuments(JobStoreDatabase database, IDataProtectionProvider protection) : IConfigurationGenerationSource
{
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
            long? generation = null;
            var protector = Protector(collection.Kind);
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                generation = reader.GetInt64(0);
                if (!reader.IsDBNull(1))
                {
                    var value = JsonSerializer.Deserialize(protector.Unprotect(reader.GetFieldValue<byte[]>(3)), collection.Type)
                        ?? throw new InvalidOperationException("Configuration document is empty.");
                    documents.Add(new(reader.GetString(1), reader.GetInt64(2), value));
                }
            }
            return new ConfigurationSnapshot<T>(generation ?? throw new InvalidOperationException("Configuration generation is unavailable."), documents);
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    public Task<bool> WriteAsync<T>(ConfigurationCollection<T> collection, ConfigurationWrite<T> write, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            var statement = write.Value is null
                ? $"DELETE FROM {_documents} WHERE kind = @kind AND key = @key AND revision = @revision"
                : write.ExpectedRevision == 0
                    ? $"INSERT INTO {_documents} (kind, key, payload) VALUES (@kind, @key, @payload) ON CONFLICT DO NOTHING"
                    : $"UPDATE {_documents} SET payload = @payload, revision = revision + 1 WHERE kind = @kind AND key = @key AND revision = @revision";
            await using var command = new NpgsqlCommand(statement, connection);
            command.Parameters.AddWithValue("kind", collection.Kind);
            command.Parameters.AddWithValue("key", write.Key);
            command.Parameters.AddWithValue("revision", write.ExpectedRevision);
            if (write.Value is not null)
            {
                command.Parameters.Add(new NpgsqlParameter("payload", NpgsqlDbType.Bytea)
                {
                    Value = Protector(collection.Kind).Protect(JsonSerializer.SerializeToUtf8Bytes(write.Value, collection.Type))
                });
            }
            return await command.ExecuteNonQueryAsync(token).ConfigureAwait(false) == 1;
        }, StatementRepeat.NotAfterSend, cancellationToken);

    private IDataProtector Protector(string kind) => protection.CreateProtector("Honua.ConfigurationDocuments", kind);
}
