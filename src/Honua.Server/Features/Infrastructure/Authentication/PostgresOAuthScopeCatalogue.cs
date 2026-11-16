// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.ControlPlane;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Infrastructure.Authentication;

internal sealed class PostgresOAuthScopeCatalogue(JobStoreDatabase database) : IOAuthScopeCatalogue
{
    private readonly string _table = database.Table("oauth_scope_catalogue");

    public async Task<IReadOnlyList<OAuthScopeDefinition>> ListAsync(CancellationToken cancellationToken)
        => (await ReadAsync(null, cancellationToken).ConfigureAwait(false))
            .OrderBy(definition => definition.Scope, StringComparer.OrdinalIgnoreCase).ToArray();

    public async Task<OAuthScopeDefinition?> GetAsync(string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return string.IsNullOrWhiteSpace(scope) ? null
            : (await ReadAsync([OrdinalIgnoreCaseKey.Create(scope.Trim())], cancellationToken).ConfigureAwait(false)).SingleOrDefault();
    }

    public async Task<OAuthScopeDefinition> DefineAsync(OAuthScopeDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);
        cancellationToken.ThrowIfCancellationRequested();
        var normalized = OAuthScopePolicy.Normalize(definition);
        var body = JsonSerializer.Serialize(normalized, OAuthScopeJsonContext.Default.OAuthScopeDefinition);
        await database.ExecuteAsync(async (connection, token) =>
        {
            await using var command = new NpgsqlCommand($"""
                INSERT INTO {_table} (scope_key, body) VALUES (@key, @body)
                ON CONFLICT (scope_key) DO UPDATE SET body = EXCLUDED.body
                """, connection);
            command.Parameters.AddWithValue("key", OrdinalIgnoreCaseKey.Create(normalized.Scope));
            command.Parameters.AddWithValue("body", NpgsqlDbType.Jsonb, body);
            await command.ExecuteNonQueryAsync(token).ConfigureAwait(false);
        }, StatementRepeat.NotAfterSend, cancellationToken).ConfigureAwait(false);
        return normalized;
    }

    public Task<OAuthScopeDefinition?> DeleteAsync(string scope, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(scope))
        {
            return Task.FromResult<OAuthScopeDefinition?>(null);
        }
        return database.ExecuteAsync<OAuthScopeDefinition?>(async (connection, _, token) =>
        {
            await using var command = new NpgsqlCommand($"DELETE FROM {_table} WHERE scope_key = @key RETURNING body", connection);
            command.Parameters.AddWithValue("key", OrdinalIgnoreCaseKey.Create(scope.Trim()));
            return await command.ExecuteScalarAsync(token).ConfigureAwait(false) is string json ? Deserialize(json) : null;
        }, StatementRepeat.NotAfterSend, cancellationToken);
    }

    public async Task<OAuthScopeGrant> ResolveGrantAsync(string? requestedScope, IReadOnlyList<string> allowedScopes,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var keys = OAuthScopePolicy.Candidates(requestedScope, allowedScopes)
            .Select(OrdinalIgnoreCaseKey.Create).Distinct(StringComparer.Ordinal).ToArray();
        var definitions = keys.Length == 0 ? [] : await ReadAsync(keys, cancellationToken).ConfigureAwait(false);
        return OAuthScopePolicy.Resolve(requestedScope, allowedScopes,
            definitions.ToDictionary(definition => definition.Scope, StringComparer.OrdinalIgnoreCase));
    }

    private Task<List<OAuthScopeDefinition>> ReadAsync(string[]? keys, CancellationToken cancellationToken)
        => database.ExecuteAsync(async (connection, _, token) =>
        {
            var filter = keys is null ? string.Empty : " WHERE scope_key = ANY(@keys)";
            await using var command = new NpgsqlCommand($"SELECT body FROM {_table}{filter}", connection);
            if (keys is not null)
            {
                command.Parameters.AddWithValue("keys", keys);
            }
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            var definitions = new List<OAuthScopeDefinition>();
            while (await reader.ReadAsync(token).ConfigureAwait(false))
            {
                definitions.Add(Deserialize(reader.GetString(0)));
            }
            return definitions;
        }, StatementRepeat.SafeToRepeat, cancellationToken);

    private static OAuthScopeDefinition Deserialize(string json)
        => JsonSerializer.Deserialize(json, OAuthScopeJsonContext.Default.OAuthScopeDefinition)
            ?? throw new JsonException("OAuth scope definition is missing.");
}

[JsonSerializable(typeof(OAuthScopeDefinition))]
internal sealed partial class OAuthScopeJsonContext : JsonSerializerContext;
