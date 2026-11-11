// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography;
using Honua.ControlPlane;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Operations.Domain;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Operations;

// The table holds only a protected envelope. The interface is synchronous, so the commands are too.
internal sealed class PostgresOperationSecretStore(
    NpgsqlDataSource dataSource,
    JobStoreDatabase database,
    IDataProtectionProvider dataProtectionProvider) : IOperationSecretStore, IExpiredRowSweep
{
    private const string AnonymousPrincipal = "anonymous";
    private static readonly TimeSpan _defaultLifetime = TimeSpan.FromMinutes(15);

    private readonly string _secrets = database.Table("operation_secrets");
    private readonly IDataProtector _protector = dataProtectionProvider.CreateProtector("Honua.OperationSecrets.v1");

    public bool IsAvailable => true;

    public OperationSecretReference Store(OperationSecretBinding binding, string name, string value, TimeSpan? ttl = null)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.OperationInstanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(binding.OperationId);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(value);

        var reference = new OperationSecretReference
        {
            ReferenceId = $"opsecret-{Guid.NewGuid():N}",
            Name = name,
        };
        using var command = dataSource.CreateCommand(
            $"""
            INSERT INTO {_secrets}
                (reference_id, operation_instance_id, operation_id, principal_id, tenant_id, name, protected_value, expires_at)
            VALUES
                (@reference_id, @operation_instance_id, @operation_id, @principal_id, @tenant_id, @name, @protected_value,
                 now() + @lifetime)
            """);
        AddBindingParameters(command, reference.ReferenceId, binding);
        command.Parameters.Add(new NpgsqlParameter("name", NpgsqlDbType.Text) { Value = name });
        command.Parameters.Add(new NpgsqlParameter("protected_value", NpgsqlDbType.Text) { Value = _protector.Protect(value) });
        command.Parameters.Add(new NpgsqlParameter("lifetime", NpgsqlDbType.Interval) { Value = ttl ?? _defaultLifetime });
        command.ExecuteNonQuery();
        return reference;
    }

    public string? Consume(OperationSecretReference reference, OperationSecretBinding binding)
    {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(binding);

        using var command = dataSource.CreateCommand(
            $"""
            DELETE FROM {_secrets}
            WHERE reference_id = @reference_id AND operation_instance_id = @operation_instance_id
              AND operation_id = @operation_id AND principal_id = @principal_id AND tenant_id = @tenant_id
              AND expires_at > now()
            RETURNING protected_value
            """);
        AddBindingParameters(command, reference.ReferenceId, binding);
        if (command.ExecuteScalar() is not string protectedValue)
        {
            return null;
        }

        try
        {
            return _protector.Unprotect(protectedValue);
        }
        catch (CryptographicException)
        {
            // The row is already deleted, so a key-ring mismatch cannot become a second retrieval.
            return null;
        }
    }

    public async Task<int> DeleteExpiredAsync(int batchSize, CancellationToken cancellationToken)
    {
        await using var command = dataSource.CreateCommand(
            $"""
            WITH expired AS (
                SELECT reference_id FROM {_secrets}
                WHERE expires_at <= now()
                ORDER BY expires_at LIMIT @batch_size FOR UPDATE SKIP LOCKED)
            DELETE FROM {_secrets} AS secret USING expired WHERE secret.reference_id = expired.reference_id
            """);
        command.Parameters.Add(new NpgsqlParameter("batch_size", NpgsqlDbType.Integer) { Value = batchSize });
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static void AddBindingParameters(NpgsqlCommand command, string referenceId, OperationSecretBinding binding)
    {
        command.Parameters.Add(new NpgsqlParameter("reference_id", NpgsqlDbType.Text) { Value = referenceId });
        command.Parameters.Add(new NpgsqlParameter("operation_instance_id", NpgsqlDbType.Text) { Value = binding.OperationInstanceId });
        command.Parameters.Add(new NpgsqlParameter("operation_id", NpgsqlDbType.Text) { Value = binding.OperationId });
        command.Parameters.Add(new NpgsqlParameter("principal_id", NpgsqlDbType.Text) { Value = binding.PrincipalId ?? AnonymousPrincipal });
        command.Parameters.Add(new NpgsqlParameter("tenant_id", NpgsqlDbType.Text) { Value = binding.TenantId ?? string.Empty });
    }
}
