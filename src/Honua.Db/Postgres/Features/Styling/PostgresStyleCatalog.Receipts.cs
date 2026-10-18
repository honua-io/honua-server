// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Honua.Core.Features.Styling.Domain;
using Npgsql;

namespace Honua.Db.Postgres.Features.Styling;

internal sealed partial class PostgresStyleCatalog
{
    public async Task<StyleMutationReceipt?> GetMutationReceiptAsync(StyleMutationIdentity identity,
        CancellationToken cancellationToken = default)
    {
        ValidateIdentity(identity);
        await using var connection = await _connectionProvider.OpenNpgsqlConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await ReadMutationReceiptAsync(connection, null, identity, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StyleMutationReceipt?> ReadMutationReceiptAsync(NpgsqlConnection connection,
        NpgsqlTransaction? transaction, StyleMutationIdentity identity, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"SELECT identity_hash, result_version, result_content_hash, committed_at FROM {_mutationReceiptsTable} WHERE operation_id = @operationId", connection, transaction);
        command.CommandTimeout = 30;
        command.Parameters.AddWithValue("operationId", identity.OperationId);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false)) return null;
        if (!string.Equals(reader.GetString(0), IdentityHash(identity), StringComparison.Ordinal))
            throw new InvalidOperationException("The operation already has a different immutable style mutation identity.");
        return new(identity, reader.GetInt32(1), reader.GetString(2), reader.GetFieldValue<DateTimeOffset>(3));
    }

    private async Task<StyleMutationReceipt> InsertMutationReceiptAsync(NpgsqlConnection connection,
        NpgsqlTransaction transaction, StyleMutationIdentity identity, int resultVersion, CancellationToken cancellationToken)
    {
        await using var command = new NpgsqlCommand($"""
            INSERT INTO {_mutationReceiptsTable}
              (operation_id, proposal_id, identity_hash, request_hash, actor_id, tenant_id, schema_name,
               target_id, style_id, expected_version, result_version, result_content_hash)
            VALUES (@operationId, @proposalId, @identityHash, @requestHash, @actorId, @tenantId, @schemaName,
               @targetId, @styleId, @expectedVersion, @resultVersion, @resultHash)
            RETURNING committed_at
            """, connection, transaction);
        command.Parameters.AddWithValue("operationId", identity.OperationId);
        command.Parameters.AddWithValue("proposalId", identity.ProposalId);
        command.Parameters.AddWithValue("identityHash", IdentityHash(identity));
        command.Parameters.AddWithValue("requestHash", identity.RequestHash);
        command.Parameters.AddWithValue("actorId", identity.ActorId);
        command.Parameters.AddWithValue("tenantId", identity.TenantId);
        command.Parameters.AddWithValue("schemaName", identity.SchemaName);
        command.Parameters.AddWithValue("targetId", identity.TargetId);
        command.Parameters.AddWithValue("styleId", identity.StyleId);
        command.Parameters.AddWithValue("expectedVersion", identity.ExpectedVersion);
        command.Parameters.AddWithValue("resultVersion", resultVersion);
        command.Parameters.AddWithValue("resultHash", identity.CandidateHash);
        var committedAt = (DateTime) (await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
        return new(identity, resultVersion, identity.CandidateHash, new DateTimeOffset(committedAt));
    }

    private static string IdentityHash(StyleMutationIdentity identity)
    {
        string[] fields = [identity.OperationId, identity.ProposalId, identity.RequestHash, identity.ActorId,
            identity.TenantId, identity.SchemaName, identity.TargetId, identity.StyleId,
            identity.ExpectedVersion.ToString(CultureInfo.InvariantCulture), identity.ExpectedContentHash,
            identity.ExpectedImpactHash, identity.CandidateHash];
        var canonical = string.Concat(fields.Select(value => value.Length.ToString(CultureInfo.InvariantCulture) + ":" + value));
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private static void ValidateIdentity(StyleMutationIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        string[] values = [identity.OperationId, identity.ProposalId, identity.RequestHash, identity.ActorId,
            identity.TenantId, identity.SchemaName, identity.TargetId, identity.StyleId,
            identity.ExpectedContentHash, identity.ExpectedImpactHash, identity.CandidateHash];
        if (identity.ExpectedVersion < 0 || values.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 1024))
            throw new ArgumentException("A complete bounded canonical style execution identity is required.", nameof(identity));
        foreach (var hash in new[] { identity.RequestHash, identity.ExpectedContentHash, identity.ExpectedImpactHash, identity.CandidateHash })
            if (hash.Length != 64 || hash.Any(character => character is not (>= '0' and <= '9') and not (>= 'a' and <= 'f')))
                throw new ArgumentException("Canonical hashes must be SHA-256 lowercase hexadecimal.", nameof(identity));
    }
}
