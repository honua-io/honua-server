// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

#if !HONUA_EXCLUDE_AWS
using System.Data.Common;
using Honua.Cloud.Aws.Features.ControlPlane;
using Honua.Core.Features.ControlPlane.Restore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Npgsql;
using Honua.Db.Postgres.Features.Security.ConnectionSecretResolvers;

namespace Honua.ControlPlane;

internal sealed class AwsRdsSnapshotRestoreAdapter : IRdsSnapshotRestore, IDisposable
{
    private readonly AwsRdsCellRestoreClient _client = new();

    public async Task<RdsSnapshotEvidence?> DescribeAsync(string snapshotId, string cellPrefix, CancellationToken cancellationToken)
    {
        var view = await _client.DescribeAsync(snapshotId, cellPrefix, cancellationToken).ConfigureAwait(false);
        if (view is null)
        {
            return null;
        }

        return new RdsSnapshotEvidence
        {
            SnapshotId = view.SnapshotId,
            CellPrefix = cellPrefix,
            Status = view.Status,
            SchemaVersion = view.SchemaVersion,
            CellTagMatches = view.CellTagMatches,
        };
    }

    public async Task<RdsNewInstance> RestoreNewAsync(RdsRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!BackupEvidenceVerifier.InstanceInCell(request.NewInstanceId, request.CellPrefix))
        {
            return new RdsNewInstance(false, null, PreMigrationRestoreCodes.ScopeViolation);
        }

        var view = await _client.RestoreNewAsync(
                new AwsRdsRestoreCommand(
                    request.SnapshotId,
                    request.NewInstanceId,
                    request.CellPrefix,
                    request.SubnetGroupName,
                    request.EnvironmentTag,
                    request.WaitSeconds,
                    request.SecurityGroupIds),
                cancellationToken)
            .ConfigureAwait(false);
        return view.Available && !string.IsNullOrWhiteSpace(view.Endpoint)
            ? new RdsNewInstance(true, view.Endpoint, null)
            : new RdsNewInstance(false, null, PreMigrationRestoreCodes.ProviderFailed);
    }

    public async Task<RdsRestoreSubmission> BeginAsync(RdsRestoreRequest request, CancellationToken cancellationToken)
    {
        if (!BackupEvidenceVerifier.InstanceInCell(request.NewInstanceId, request.CellPrefix))
            return new RdsRestoreSubmission(false, null, PreMigrationRestoreCodes.ScopeViolation);
        var resourceId = await _client.BeginAsync(new AwsRdsRestoreCommand(request.SnapshotId, request.NewInstanceId,
            request.CellPrefix, request.SubnetGroupName, request.EnvironmentTag, request.WaitSeconds, request.SecurityGroupIds), cancellationToken).ConfigureAwait(false);
        return new RdsRestoreSubmission(!string.IsNullOrWhiteSpace(resourceId), resourceId,
            string.IsNullOrWhiteSpace(resourceId) ? PreMigrationRestoreCodes.ProviderFailed : null);
    }

    public async Task<RdsNewInstance> ObserveAsync(string instanceId, string resourceId, CancellationToken cancellationToken)
    {
        var result = await _client.ObserveAsync(instanceId, resourceId, cancellationToken).ConfigureAwait(false);
        return result.Failed ? new RdsNewInstance(false, null, PreMigrationRestoreCodes.ProviderFailed)
            : result.Available ? new RdsNewInstance(true, result.Endpoint, null)
            : new RdsNewInstance(false, null, "restore-pending");
    }

    public void Dispose() => _client.Dispose();
}

internal sealed class AwsSecretHostRetarget(IOptions<RestoreCellOptions> options) : IConnectionSecretRetarget, IDisposable
{
    private readonly AwsSecretStringClient _secrets = new();

    public bool IsConfigured => !string.IsNullOrWhiteSpace(options.Value.ConnectionSecretId);

    public async Task<bool> PointAtAsync(string secretId, string host, CancellationToken cancellationToken)
    {
        var current = await _secrets.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
        if (current is null || !ConnectionStringHostSwap.TrySwapHost(current, host, out var swapped))
        {
            return false;
        }

        if (!await _secrets.PutAsync(secretId, swapped, cancellationToken).ConfigureAwait(false))
        {
            return false;
        }

        var readBack = await _secrets.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
        return readBack is not null
            && string.Equals(ConnectionStringHostSwap.ReadHost(readBack), host, StringComparison.Ordinal);
    }

    public async Task<string?> ReadHostAsync(string secretId, CancellationToken cancellationToken)
    {
        var value = await _secrets.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
        return value is null ? null : ConnectionStringHostSwap.ReadHost(value);
    }

    public void Dispose() => _secrets.Dispose();
}

internal sealed class AwsRestoredConnectionOpener(IServiceProvider services, IOptions<RestoreCellOptions> options) : IRestoredConnectionOpener, IDisposable
{
    private readonly AwsSecretStringClient _secrets = new();

    public async Task<DbConnection?> OpenAsync(string? endpoint, CancellationToken cancellationToken)
    {
        if (endpoint is null)
        {
            var dataSource = services.GetService<NpgsqlDataSource>();
            return dataSource is null
                ? null
                : await dataSource.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        }

        var secretId = options.Value.ConnectionSecretId;
        if (string.IsNullOrWhiteSpace(secretId))
        {
            return null;
        }

        var current = await _secrets.GetAsync(secretId, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return null;
        }

        var secret = current;
        if (endpoint.Length > 0 && !ConnectionStringHostSwap.TrySwapHost(current, endpoint, out secret))
        {
            return null;
        }

        var connectionString = SecretValueExtractor.ExtractValue(secret);
        var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    public void Dispose() => _secrets.Dispose();
}
#endif
