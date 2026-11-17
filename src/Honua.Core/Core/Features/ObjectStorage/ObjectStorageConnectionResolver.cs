// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;
using Microsoft.Extensions.Options;

namespace Honua.Core.Features.ObjectStorage;

public enum ObjectStorageResolveFailure
{
    None,
    ConnectionNotFound,
    InvalidKey,
}

public sealed record ObjectStorageResolution
{
    public ObjectStorageConnection? Connection { get; init; }

    public string? Key { get; init; }

    public ObjectStorageResolveFailure Failure { get; init; }

    public string Message { get; init; } = string.Empty;

    public bool Succeeded => Failure == ObjectStorageResolveFailure.None;

    public string SourceUri => Connection is null ? string.Empty : $"s3://{Connection.Bucket}/{Key}";
}

// Resolves (connection id, key) for imports and exports. Every call reads the store, so a deleted
// connection or a key outside its prefix is refused on every task, including job-time re-checks.
public interface IObjectStorageConnectionResolver
{
    Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken);

    Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken);

    Task<ObjectStorageResolution> ResolveObjectAsync(Guid connectionId, string? key, CancellationToken cancellationToken);
}

public sealed class ObjectStorageConnectionResolver(
    IOptionsMonitor<CloudStorageOptions> storageOptions,
    IObjectStorageConnectionStore? store = null) : IObjectStorageConnectionResolver
{
    public async Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
    {
        var builtIns = ObjectStorageBuiltInConnections.From(storageOptions.CurrentValue);
        if (store is null)
        {
            return builtIns;
        }

        var stored = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. builtIns, .. stored];
    }

    public async Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var builtIn = ObjectStorageBuiltInConnections.From(storageOptions.CurrentValue)
            .FirstOrDefault(connection => connection.Id == connectionId);
        if (builtIn is not null || store is null)
        {
            return builtIn;
        }

        return await store.GetAsync(connectionId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ObjectStorageResolution> ResolveObjectAsync(
        Guid connectionId,
        string? key,
        CancellationToken cancellationToken)
    {
        var connection = await FindAsync(connectionId, cancellationToken).ConfigureAwait(false);
        if (connection is null)
        {
            return new ObjectStorageResolution
            {
                Failure = ObjectStorageResolveFailure.ConnectionNotFound,
                Message = "The connection was not found.",
            };
        }

        if (!ObjectStoragePolicy.IsKeyUnderPrefix(connection.Prefix, key))
        {
            return new ObjectStorageResolution
            {
                Connection = connection,
                Failure = ObjectStorageResolveFailure.InvalidKey,
                Message = "The key must be a plain object key under the connection prefix.",
            };
        }

        return new ObjectStorageResolution
        {
            Connection = connection,
            Key = key,
        };
    }
}

// The installation's own data bucket and public publish prefix, shown as read-only connections.
// Backup, audit and configuration storage is never exposed this way.
public static class ObjectStorageBuiltInConnections
{
    public const string DataRole = "data";

    public const string PublicRole = "public";

    public static IReadOnlyList<ObjectStorageConnection> From(CloudStorageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        var s3 = options.AwsS3;
        if (options.Provider != CloudStorageProvider.AwsS3
            || s3 is null
            || !ObjectStoragePolicy.IsValidBucketName(s3.BucketName)
            || !ObjectStoragePolicy.IsKnownRegion(s3.Region)
            || ObjectStoragePolicy.NormalizePrefix(s3.KeyPrefix) is not { } dataPrefix)
        {
            return [];
        }

        var data = BuiltIn(DataRole, s3, dataPrefix);
        if (options.PMTilesPublish.UrlStrategy != PMTilesUrlStrategy.PublicUrl
            || ObjectStoragePolicy.NormalizePrefix(dataPrefix + options.PMTilesPublish.KeyPrefix) is not { } publicPrefix
            || publicPrefix == dataPrefix)
        {
            return [data];
        }

        return [data, BuiltIn(PublicRole, s3, publicPrefix)];
    }

    private static ObjectStorageConnection BuiltIn(string role, AwsS3Options s3, string prefix) => new()
    {
        Id = ObjectStoragePolicy.BuiltInId(role, s3.BucketName, prefix),
        Name = role == DataRole ? "Installation data" : "Installation public files",
        Description = "Built in; read-only.",
        Bucket = s3.BucketName,
        Prefix = prefix,
        Region = s3.Region,
        BuiltIn = true,
        CreatedBy = "system",
    };
}
