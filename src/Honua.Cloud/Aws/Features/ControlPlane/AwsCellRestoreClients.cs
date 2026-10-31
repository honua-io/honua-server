// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Amazon;
using Amazon.RDS;
using Amazon.RDS.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Honua.Cloud.Aws.Features.ControlPlane;

/// <summary>Snapshot fields the server restore gate needs. No endpoint password and no raw error text.</summary>
/// <param name="SnapshotId">Snapshot identifier.</param>
/// <param name="Status">Provider status.</param>
/// <param name="SchemaVersion">Value of the <c>Schema</c> tag, when it is an integer.</param>
/// <param name="CellTagMatches">Whether the snapshot is tagged for this cell prefix.</param>
internal sealed record AwsRdsSnapshotView(string SnapshotId, string Status, int? SchemaVersion, bool CellTagMatches);

/// <summary>New instance the provider reported. <see cref="Endpoint"/> is set only when status is available.</summary>
/// <param name="Available">Whether the instance is available and has an address.</param>
/// <param name="Endpoint">Hostname, when <see cref="Available"/> is true.</param>
internal sealed record AwsRdsInstanceView(bool Available, string? Endpoint);

/// <summary>One restore of a cell snapshot into a new instance. Failed instances are left in place.</summary>
/// <param name="SnapshotId">Snapshot identifier.</param>
/// <param name="NewInstanceId">Identifier that already passed the cell prefix check.</param>
/// <param name="CellPrefix">Prefix stored on the <c>Cell</c> tag.</param>
/// <param name="SubnetGroupName">DB subnet group.</param>
/// <param name="EnvironmentTag"><c>cert</c> or <c>demo</c>.</param>
/// <param name="WaitSeconds">How long to poll before giving up.</param>
/// <param name="SecurityGroupIds">Cell-authorized VPC security groups.</param>
internal sealed record AwsRdsRestoreCommand(
    string SnapshotId,
    string NewInstanceId,
    string CellPrefix,
    string SubnetGroupName,
    string EnvironmentTag,
    int WaitSeconds,
    IReadOnlyList<string> SecurityGroupIds);

/// <summary>
/// RDS snapshot describe and restore. The client is created on first use so a Compose host
/// without AWS credentials can still start. This type never deletes an instance.
/// </summary>
internal sealed class AwsRdsCellRestoreClient : IDisposable
{
    private readonly Lazy<IAmazonRDS> _client;
    private bool _disposed;

    public AwsRdsCellRestoreClient(IAmazonRDS? client = null)
    {
        _client = client is null
            ? new Lazy<IAmazonRDS>(CreateClient)
            : new Lazy<IAmazonRDS>(() => client);
    }

    /// <summary>Describes one snapshot. Null when it does not exist or the call fails.</summary>
    public async Task<AwsRdsSnapshotView?> DescribeAsync(
        string snapshotId,
        string cellPrefix,
        CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.Value.DescribeDBSnapshotsAsync(
                    new DescribeDBSnapshotsRequest { DBSnapshotIdentifier = snapshotId },
                    cancellationToken)
                .ConfigureAwait(false);
            var snapshot = response.DBSnapshots?.FirstOrDefault();
            if (snapshot is null || string.IsNullOrWhiteSpace(snapshot.Status))
            {
                return null;
            }

            var tags = snapshot.TagList ?? [];
            if (tags.Count == 0 && !string.IsNullOrWhiteSpace(snapshot.DBSnapshotArn))
            {
                tags = await ListTagsAsync(snapshot.DBSnapshotArn, cancellationToken).ConfigureAwait(false);
            }

            return new AwsRdsSnapshotView(
                snapshotId,
                snapshot.Status,
                ReadSchema(tags),
                CellMatches(tags, cellPrefix, snapshotId));
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Restores a new instance and polls until it is available, failed, or the wait expires.</summary>
    public async Task<AwsRdsInstanceView> RestoreNewAsync(AwsRdsRestoreCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        try
        {
            await _client.Value.RestoreDBInstanceFromDBSnapshotAsync(
                    new RestoreDBInstanceFromDBSnapshotRequest
                    {
                        DBInstanceIdentifier = command.NewInstanceId,
                        DBSnapshotIdentifier = command.SnapshotId,
                        DBSubnetGroupName = command.SubnetGroupName,
                        VpcSecurityGroupIds = command.SecurityGroupIds.ToList(),
                        PubliclyAccessible = false,
                        CopyTagsToSnapshot = true,
                        Tags =
                        [
                            new Amazon.RDS.Model.Tag { Key = "Environment", Value = command.EnvironmentTag },
                            new Amazon.RDS.Model.Tag { Key = "Cell", Value = command.CellPrefix },
                        ],
                    },
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonRDSException ex) when (string.Equals(ex.ErrorCode, "DBInstanceAlreadyExists", StringComparison.Ordinal))
        {
            // A pre-existing instance is not proof that it came from the approved snapshot.
            return new AwsRdsInstanceView(false, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new AwsRdsInstanceView(false, null);
        }

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(command.WaitSeconds, 1, 3600));
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string? status = null;
            string? address = null;
            try
            {
                var described = await _client.Value.DescribeDBInstancesAsync(
                        new DescribeDBInstancesRequest { DBInstanceIdentifier = command.NewInstanceId },
                        cancellationToken)
                    .ConfigureAwait(false);
                var instance = described.DBInstances?.FirstOrDefault();
                status = instance?.DBInstanceStatus;
                address = instance?.Endpoint?.Address;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                status = null;
            }

            if (string.Equals(status, "available", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(address))
            {
                return new AwsRdsInstanceView(true, address);
            }

            if (status is "failed" or "incompatible-restore" or "incompatible-parameters" or "storage-full")
            {
                return new AwsRdsInstanceView(false, null);
            }

            if (DateTime.UtcNow >= deadline)
            {
                return new AwsRdsInstanceView(false, null);
            }

            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Starts a new restore and returns the provider's immutable resource identity.</summary>
    public async Task<string?> BeginAsync(AwsRdsRestoreCommand command, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.Value.RestoreDBInstanceFromDBSnapshotAsync(
                new RestoreDBInstanceFromDBSnapshotRequest
                {
                    DBInstanceIdentifier = command.NewInstanceId,
                    DBSnapshotIdentifier = command.SnapshotId,
                    DBSubnetGroupName = command.SubnetGroupName,
                    VpcSecurityGroupIds = command.SecurityGroupIds.ToList(),
                    PubliclyAccessible = false,
                    CopyTagsToSnapshot = true,
                    Tags =
                    [
                        new Amazon.RDS.Model.Tag { Key = "Environment", Value = command.EnvironmentTag },
                        new Amazon.RDS.Model.Tag { Key = "Cell", Value = command.CellPrefix },
                    ]
                }, cancellationToken).ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(result.DBInstance?.DbiResourceId) ? null : result.DBInstance.DbiResourceId;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // A pre-existing or unconfirmed instance cannot prove approved provenance.
            return null;
        }
    }

    /// <summary>Observes only the resource identity returned by the original submission.</summary>
    public async Task<(bool Known, bool Available, bool Failed, string? Endpoint)> ObserveAsync(
        string instanceId, string resourceId, CancellationToken cancellationToken)
    {
        try
        {
            var result = await _client.Value.DescribeDBInstancesAsync(
                new DescribeDBInstancesRequest { DBInstanceIdentifier = instanceId }, cancellationToken).ConfigureAwait(false);
            var instance = result.DBInstances?.FirstOrDefault();
            if (instance is null) return (false, false, false, null);
            if (!string.Equals(instance.DbiResourceId, resourceId, StringComparison.Ordinal)) return (true, false, true, null);
            var status = instance.DBInstanceStatus;
            if (status is "failed" or "incompatible-restore" or "incompatible-parameters" or "storage-full")
                return (true, false, true, null);
            return (true, string.Equals(status, "available", StringComparison.Ordinal) && !string.IsNullOrWhiteSpace(instance.Endpoint?.Address), false, instance.Endpoint?.Address);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return (false, false, false, null);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }

        _disposed = true;
    }

    private async Task<List<Amazon.RDS.Model.Tag>> ListTagsAsync(string arn, CancellationToken cancellationToken)
    {
        try
        {
            var listed = await _client.Value.ListTagsForResourceAsync(
                    new ListTagsForResourceRequest { ResourceName = arn },
                    cancellationToken)
                .ConfigureAwait(false);
            return listed.TagList ?? [];
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return [];
        }
    }

    private static int? ReadSchema(IEnumerable<Amazon.RDS.Model.Tag> tags)
    {
        var raw = tags.FirstOrDefault(tag => string.Equals(tag.Key, "Schema", StringComparison.Ordinal))?.Value;
        return int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out var schema) ? schema : null;
    }

    private static bool CellMatches(IEnumerable<Amazon.RDS.Model.Tag> tags, string cellPrefix, string snapshotId)
    {
        var list = tags as IReadOnlyList<Amazon.RDS.Model.Tag> ?? tags.ToArray();
        var cell = list.FirstOrDefault(tag => string.Equals(tag.Key, "Cell", StringComparison.Ordinal))?.Value;
        if (!string.IsNullOrWhiteSpace(cell))
        {
            return string.Equals(cell, cellPrefix, StringComparison.Ordinal);
        }

        var environment = list.FirstOrDefault(tag => string.Equals(tag.Key, "Environment", StringComparison.Ordinal))?.Value;
        return environment is "cert" or "demo"
            && (snapshotId.StartsWith(cellPrefix, StringComparison.Ordinal)
                || snapshotId.StartsWith("rds:" + cellPrefix, StringComparison.Ordinal));
    }

    private static AmazonRDSClient CreateClient()
    {
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        return string.IsNullOrWhiteSpace(region)
            ? new AmazonRDSClient()
            : new AmazonRDSClient(RegionEndpoint.GetBySystemName(region));
    }
}

/// <summary>Reads and replaces a Secrets Manager string. Callers must not log the value.</summary>
internal sealed class AwsSecretStringClient : IDisposable
{
    private readonly Lazy<IAmazonSecretsManager> _client;
    private bool _disposed;

    public AwsSecretStringClient(IAmazonSecretsManager? client = null)
    {
        _client = client is null
            ? new Lazy<IAmazonSecretsManager>(CreateClient)
            : new Lazy<IAmazonSecretsManager>(() => client);
    }

    /// <summary>Returns the secret string, or null when the read fails.</summary>
    public async Task<string?> GetAsync(string secretId, CancellationToken cancellationToken)
    {
        try
        {
            var response = await _client.Value.GetSecretValueAsync(
                    new GetSecretValueRequest { SecretId = secretId },
                    cancellationToken)
                .ConfigureAwait(false);
            return string.IsNullOrWhiteSpace(response.SecretString) ? null : response.SecretString;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    /// <summary>Writes a new secret string. False when the write fails.</summary>
    public async Task<bool> PutAsync(string secretId, string secretString, CancellationToken cancellationToken)
    {
        try
        {
            await _client.Value.PutSecretValueAsync(
                    new PutSecretValueRequest { SecretId = secretId, SecretString = secretString },
                    cancellationToken)
                .ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        if (_client.IsValueCreated)
        {
            _client.Value.Dispose();
        }

        _disposed = true;
    }

    private static AmazonSecretsManagerClient CreateClient()
    {
        var region = Environment.GetEnvironmentVariable("AWS_REGION")
            ?? Environment.GetEnvironmentVariable("AWS_DEFAULT_REGION");
        return string.IsNullOrWhiteSpace(region)
            ? new AmazonSecretsManagerClient()
            : new AmazonSecretsManagerClient(RegionEndpoint.GetBySystemName(region));
    }
}
