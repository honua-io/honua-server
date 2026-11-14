// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Server.Features.Infrastructure.Configuration;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Coordination;
using Honua.Server.Features.Infrastructure.Progress;
using Honua.Server.Features.Operations;
using Honua.Server.Features.Infrastructure.SecurityState;
using Honua.Server.Features.Infrastructure.McpSessions;
using Honua.Server.Features.Studio.Drafts;
using Honua.Server.Tests.Infrastructure;
using Honua.TestKit;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

// The control-plane state tables beside the job substrate, in a schema of their own. Each store
// factory returns a new instance over the same tables, which is what a second node is.
internal sealed class ControlPlaneStateHarness : IAsyncDisposable
{
    private static readonly string[] _migrationResourceNames =
    [
        "Honua.Server.Migrations.031_CreateMetadataV2Snapshot.sql",
        "Honua.Server.Migrations.148_CreateControlPlaneState.sql",
        "Honua.Server.Migrations.149_CreateSecurityState.sql",
        "Honua.Server.Migrations.150_CreateOAuthClientsAndOperationProgress.sql",
        "Honua.Server.Migrations.154_CreatePackageDraftsAndMcpSessions.sql",
        "Honua.Server.Migrations.156_CreateConfigurationDocuments.sql",
        "Honua.Server.Migrations.161_CreateOpsNotificationSchedules.sql"
    ];

    private readonly PostgresJobSubstrateHarness _jobs;

    private ControlPlaneStateHarness(PostgresJobSubstrateHarness jobs)
    {
        _jobs = jobs;
        Leases = new PostgresLeaseStore(jobs.JobStore);
    }

    private readonly IDataProtectionProvider _sharedKeyRing = new EphemeralDataProtectionProvider();

    public ILeaseStore Leases { get; }

    public ControlPlaneRetentionOptions Retention { get; } = new();

    public NpgsqlDataSource DataSource => _jobs.DataSource;

    public static async Task<ControlPlaneStateHarness> CreateAsync(IDatabaseFixture fixture, string testClassName)
    {
        var jobs = await PostgresJobSubstrateHarness.CreateAsync(fixture, testClassName);
        foreach (var migration in _migrationResourceNames)
        {
            await fixture.ExecuteDdlUnderLockAsync(
                ReadMigration(migration).Replace("$HonuaSchema$", $"\"{jobs.Schema}\"", StringComparison.Ordinal));
        }

        return new ControlPlaneStateHarness(jobs);
    }

    public Task ReplayConfigurationMigrationAsync()
        => ExecuteAsync(ReadMigration("Honua.Server.Migrations.156_CreateConfigurationDocuments.sql")
            .Replace("$HonuaSchema$", $"\"{_jobs.Schema}\"", StringComparison.Ordinal));

    public string Table(string tableName) => _jobs.Database.Table(tableName);

    public Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) => _jobs.ExecuteAsync(sql, parameters);

    public Task<T?> ScalarAsync<T>(string sql, params NpgsqlParameter[] parameters) => _jobs.ScalarAsync<T>(sql, parameters);

    public PostgresOperationProposalStore CreateProposalStore()
        => new(new ControlPlaneStorage(_jobs.Database, Leases), Options.Create(Retention), NullLogger<PostgresOperationProposalStore>.Instance);

    public PostgresOpsNotificationStore CreateNotificationStore()
        => new(_jobs.Database, Options.Create(Retention));

    public PostgresOperationInstanceStore CreateInstanceStore()
        => new(_jobs.Database, Leases, Options.Create(Retention));

    public PostgresOperationSecretStore CreateSecretStore(IDataProtectionProvider dataProtectionProvider)
        => new(DataSource, _jobs.Database, dataProtectionProvider);

    public PostgresWorkflowOperationStore CreateWorkflowOperationStore()
        => new(
            new ControlPlaneStorage(_jobs.Database, Leases),
            new WorkflowOperationRetention(Options.Create(Retention), Options.Create(new MetadataReleaseOperationOptions())),
            NullLogger<PostgresWorkflowOperationStore>.Instance);

    public PostgresDataProtectionKeyRepository CreateKeyRepository() => new(DataSource, _jobs.Database);

    // Every node opens what another node protected, as with the shared key ring.
    public PostgresSecurityTokenStore CreateSecurityTokenStore() => new(_jobs.Database, _sharedKeyRing);

    public PostgresTokenReplayStore CreateTokenReplayStore() => new(DataSource, _jobs.Database);

    public PostgresIdempotencyReservationStore CreateIdempotencyReservationStore() => new(_jobs.Database);

    public PostgresConfigurationDocuments CreateConfigurationDocuments() => new(_jobs.Database, _sharedKeyRing);
    public PostgresConfigurationDocuments CreateConfigurationDocuments(IDataProtectionProvider keyRing) => new(_jobs.Database, keyRing);
    public PostgresOidcProviderStore CreateOidcStore() => new(CreateConfigurationDocuments());
    public PostgresRateLimitPolicyStore CreateRateStore() => new(CreateConfigurationDocuments());
    public PostgresClientCertificateTrustStore CreateTrustStore() => new(CreateConfigurationDocuments());

    public PostgresOAuthClientStore CreateOAuthClientStore() => new(_jobs.Database);

    public PostgresUniversalProgressStore CreateProgressStore() => new(_jobs.Database);

    public PostgresPackageDraftBacking CreatePackageDraftBacking() => new(_jobs.Database);

    public PostgresMcpSessionStore CreateMcpSessionStore() => new(_jobs.Database);

    // Every stored column of the approval tables as text, so a leak through any field is seen.
    public async Task<StoredTextScan> ScanApprovalTablesForAsync(string text)
    {
        var rows = string.Join(
            " UNION ALL ",
            new[] { "operation_proposals", "operation_instances", "operation_secrets", "workflow_operations" }
                .Select(table => $"SELECT stored::text AS stored_text FROM {Table(table)} AS stored"));
        await using var command = DataSource.CreateCommand(
            $"SELECT count(*), count(*) FILTER (WHERE position(@text in stored_text) > 0) FROM ({rows}) AS scanned");
        command.Parameters.Add(new NpgsqlParameter("text", text));
        await using var reader = await command.ExecuteReaderAsync();
        await reader.ReadAsync();
        return new StoredTextScan(reader.GetInt64(0), reader.GetInt64(1));
    }

    public ValueTask DisposeAsync() => _jobs.DisposeAsync();

    private static string ReadMigration(string resourceName)
    {
        using var stream = typeof(PostgresFixture).Assembly.GetManifestResourceStream(resourceName)
            ?? throw new InvalidOperationException($"Embedded migration '{resourceName}' was not found.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}

internal readonly record struct StoredTextScan(long RowsScanned, long RowsContainingText);
