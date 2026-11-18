// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Tiles.PMTiles;

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
    ObjectStorageBuiltInSources builtInSources,
    IObjectStorageConnectionStore? store = null) : IObjectStorageConnectionResolver
{
    private IReadOnlyList<ObjectStorageConnection> BuiltIns() => builtInSources.BuiltIns();

    public async Task<IReadOnlyList<ObjectStorageConnection>> ListAsync(CancellationToken cancellationToken)
    {
        var builtIns = BuiltIns();
        if (store is null)
        {
            return builtIns;
        }

        var stored = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        return [.. builtIns, .. stored];
    }

    public async Task<ObjectStorageConnection?> FindAsync(Guid connectionId, CancellationToken cancellationToken)
    {
        var builtIn = BuiltIns()
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

// The installation's own bucket, shown as read-only connections only when the operator opts in
// (Connections:ObjectStorage:BuiltIn). Each data connection covers one listed folder; folders the server
// writes (imports, exports, audit, geoprocessing outputs, studio deliverables, published tiles, scene assets)
// are refused.
public static class ObjectStorageBuiltInConnections
{
    public const string DataRole = "data";

    public const string PublicRole = "public";

    // Server-written folders. Each is refused both under FileStorage:AwsS3:KeyPrefix and at the bucket root,
    // because some writers (COG artifacts, tile exports) address the bucket root directly. Configured prefixes
    // (PMTiles, geoprocessing outputs, the audit sink) are added.
    private static readonly string[] ServerFolders =
    [
        "imports/", "exports/", "publications/", "audit/", "gp/", "studio-deliverables/", "custom-code/", "pmtiles/",
        "mapserver/", "imageserver/", "temporary-files/", "attachments/", "cog/", "tile-exports/", "scenes/",
    ];

    public static IReadOnlyList<ObjectStorageConnection> From(CloudStorageOptions storage, ObjectStorageBuiltInOptions builtIn)
        => From(storage, builtIn, configuredServerFolders: []);

    public static IReadOnlyList<ObjectStorageConnection> From(
        CloudStorageOptions storage,
        ObjectStorageBuiltInOptions builtIn,
        IEnumerable<string> configuredServerFolders)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ArgumentNullException.ThrowIfNull(builtIn);
        ArgumentNullException.ThrowIfNull(configuredServerFolders);
        if (!IsEligible(storage, builtIn) || ObjectStoragePolicy.NormalizePrefix(storage.AwsS3!.KeyPrefix) is not { } root)
        {
            return [];
        }

        if (Reserved(root, ServerFolders.Concat(configuredServerFolders).Append(storage.PMTilesPublish.KeyPrefix)) is not { } reserved)
        {
            return [];
        }

        var template = Template(storage.AwsS3, builtIn.ExpectedBucketOwner!);
        var connections = builtIn.DataPrefixes
            .Select(ObjectStoragePolicy.NormalizePrefix)
            .Where(folder => !string.IsNullOrEmpty(folder) && !reserved.Exists(server => Overlaps(server, root + folder)))
            .Select(folder => root + folder)
            .Distinct(StringComparer.Ordinal)
            .Select(prefix => BuiltIn(template, DataRole, prefix))
            .ToList();
        if (builtIn.PublicFiles
            && storage.PMTilesPublish.UrlStrategy == PMTilesUrlStrategy.PublicUrl
            && ObjectStoragePolicy.NormalizePrefix(storage.PMTilesPublish.KeyPrefix) is { Length: > 0 } publicFolder)
        {
            connections.Add(BuiltIn(template, PublicRole, root + publicFolder));
        }

        return connections;
    }

    // Built-ins use the task identity against real AWS S3 only: an endpoint override or static keys could make
    // the same bucket name resolve to a bucket someone else owns.
    private static bool IsEligible(CloudStorageOptions storage, ObjectStorageBuiltInOptions builtIn)
        => builtIn.Enabled
            && ObjectStoragePolicy.IsValidAccountId(builtIn.ExpectedBucketOwner)
            && storage.Provider == CloudStorageProvider.AwsS3
            && storage.AwsS3 is { } s3
            && ObjectStoragePolicy.IsValidBucketName(s3.BucketName)
            && ObjectStoragePolicy.IsKnownRegion(s3.Region)
            && string.IsNullOrWhiteSpace(s3.ServiceUrl)
            && string.IsNullOrWhiteSpace(s3.AccessKeyId)
            && string.IsNullOrWhiteSpace(s3.SecretAccessKey);

    // Each server folder under the key prefix and at the bucket root; null when one cannot be compared safely,
    // which turns built-ins off rather than leaving that folder uncovered.
    private static List<string>? Reserved(string root, IEnumerable<string> folders)
    {
        var normalized = folders.Select(ObjectStoragePolicy.NormalizePrefix).ToList();
        return normalized.Contains(null)
            ? null
            : normalized.Where(folder => folder!.Length > 0).SelectMany(folder => new[] { root + folder, folder! }).ToList();
    }

    private static bool Overlaps(string first, string second)
        => first.StartsWith(second, StringComparison.Ordinal) || second.StartsWith(first, StringComparison.Ordinal);

    private static ObjectStorageConnection Template(AwsS3Options s3, string owner) => new()
    {
        Id = Guid.Empty,
        Name = string.Empty,
        Description = "Built in; read-only.",
        Bucket = s3.BucketName,
        Region = s3.Region,
        ExpectedBucketOwner = owner,
        BuiltIn = true,
        CreatedBy = "system",
    };

    private static ObjectStorageConnection BuiltIn(ObjectStorageConnection template, string role, string prefix) => template with
    {
        Id = ObjectStoragePolicy.BuiltInId(role, template.Bucket, prefix),
        Name = role == DataRole ? $"Installation data ({prefix.TrimEnd('/')})" : "Installation public files",
        Prefix = prefix,
    };
}
