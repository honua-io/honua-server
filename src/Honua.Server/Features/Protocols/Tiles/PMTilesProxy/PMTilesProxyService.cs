// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Microsoft.Extensions.Options;
using Honua.Server.Features.Infrastructure.FileStorage;

namespace Honua.Server.Features.Protocols.Tiles.PMTilesProxy;

/// <summary>
/// Resolves PMTiles range-proxy requests against the configured cloud storage
/// provider. Used by the public range-proxy endpoint to stream byte ranges from
/// private object storage without exposing signed URLs to browsers.
/// </summary>
internal sealed class PMTilesProxyService
{
    internal const long MaxDirectFullStreamBytes = ArtifactByteReader.MaxDirectFullStreamBytes;

    internal const string PMTilesContentType = "application/vnd.pmtiles";
    internal const string OperationMetadataKey = "operation";
    internal const string PublishOperationValue = "publish";

    private readonly ICloudFileStorage _cloudStorage;
    private readonly ArtifactByteReader _byteReader;
    private readonly CloudStorageOptions _storageOptions;

    public PMTilesProxyService(
        ICloudFileStorage cloudStorage,
        IEnumerable<ICloudRangeReader> rangeReaders,
        IEnumerable<ICloudNotFoundClassifier> notFoundClassifiers,
        IOptions<CloudStorageOptions> storageOptions)
    {
        _cloudStorage = cloudStorage ?? throw new ArgumentNullException(nameof(cloudStorage));
        _byteReader = new ArtifactByteReader(cloudStorage, rangeReaders, notFoundClassifiers, storageOptions);
        _storageOptions = storageOptions?.Value ?? throw new ArgumentNullException(nameof(storageOptions));
    }

    public async Task<PMTilesProxyResolution> ResolveAsync(string artifactId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(artifactId))
        {
            return PMTilesProxyResolution.NotFound();
        }

        var metadata = await _cloudStorage.GetMetadataAsync(artifactId, cancellationToken).ConfigureAwait(false);
        if (metadata is null)
        {
            return PMTilesProxyResolution.NotFound();
        }

        if (!IsPublishedPMTilesArtifact(metadata))
        {
            return PMTilesProxyResolution.NotFound();
        }

        return PMTilesProxyResolution.Found(metadata);
    }

    /// <summary>
    /// Binds a resolved archive to the source the proxy authorizes it against.
    /// A bound archive is checked against its source layer's policy by the
    /// caller. An unbound archive is served without a layer check only when
    /// <see cref="PMTilesPublishOptions.AllowAnonymousUnboundArchives"/> is
    /// set. Anything else, including a binding that no longer resolves, is
    /// not found.
    /// </summary>
    public PMTilesSourceBinding BindSource(MetadataV2GraphSnapshot snapshot, CloudFile metadata)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(metadata);

        var source = PMTilesProxySourceResolver.Resolve(snapshot, metadata.Metadata);
        if (source is not null)
        {
            return PMTilesSourceBinding.Bound(source);
        }

        if (!PMTilesProxySourceResolver.IsUnbound(metadata.Metadata))
        {
            return PMTilesSourceBinding.NotFound(PMTilesSourceBindingKind.SourceNotResolved);
        }

        return _storageOptions.PMTilesPublish?.AllowAnonymousUnboundArchives == true
            ? PMTilesSourceBinding.OperatorProvisioned
            : PMTilesSourceBinding.NotFound(PMTilesSourceBindingKind.UnboundRefused);
    }

    private bool IsPublishedPMTilesArtifact(CloudFile metadata)
    {
        if (!string.Equals(metadata.ContentType, PMTilesContentType, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!metadata.Metadata.TryGetValue(OperationMetadataKey, out var operationTag) ||
            !string.Equals(operationTag, PublishOperationValue, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var publishPrefix = NormalizeKeySegment(_storageOptions.PMTilesPublish?.KeyPrefix);
        if (string.IsNullOrEmpty(publishPrefix))
        {
            return true;
        }

        var providerPrefix = NormalizeKeySegment(_storageOptions.Provider switch
        {
            CloudStorageProvider.AwsS3 => _storageOptions.AwsS3?.KeyPrefix,
            CloudStorageProvider.AzureBlob => _storageOptions.AzureBlob?.BlobPrefix,
            _ => null
        });

        var key = metadata.StoragePath ?? metadata.FileId;
        var expectedPrefix = string.IsNullOrEmpty(providerPrefix)
            ? publishPrefix + "/"
            : providerPrefix + "/" + publishPrefix + "/";
        return key.StartsWith(expectedPrefix, StringComparison.Ordinal);
    }

    private static string NormalizeKeySegment(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Trim().Trim('/');
    }

    public Task<ArtifactRangeResult> ReadRangeAsync(CloudFile metadata, string? rangeHeader, CancellationToken cancellationToken)
        => _byteReader.ReadRangeAsync(metadata, rangeHeader, cancellationToken);

    public Task<ArtifactRangeResult> ReadFullAsync(CloudFile metadata, CancellationToken cancellationToken)
        => _byteReader.ReadFullAsync(metadata, cancellationToken);

    internal static bool TryParseSingleByteRange(string header, long totalSize, out long start, out long end)
        => ArtifactByteReader.TryParseSingleByteRange(header, totalSize, out start, out end);

    internal static bool IsProviderNotFound(Exception exception, IReadOnlyList<ICloudNotFoundClassifier> classifiers)
        => ArtifactByteReader.IsProviderNotFound(exception, classifiers);

}

internal enum PMTilesSourceBindingKind
{
    /// <summary>The archive names a source that resolves in the current graph.</summary>
    Bound,

    /// <summary>The archive names no source and unbound archives are allowed.</summary>
    OperatorProvisioned,

    /// <summary>The archive names a source that is retired, rebound, ambiguous or unknown.</summary>
    SourceNotResolved,

    /// <summary>The archive names no source and unbound archives are not allowed.</summary>
    UnboundRefused
}

internal readonly record struct PMTilesSourceBinding(PMTilesSourceBindingKind Kind, PMTilesPublishedSource? Source)
{
    public bool IsServable => Kind is PMTilesSourceBindingKind.Bound or PMTilesSourceBindingKind.OperatorProvisioned;

    public static PMTilesSourceBinding OperatorProvisioned => new(PMTilesSourceBindingKind.OperatorProvisioned, null);

    public static PMTilesSourceBinding Bound(PMTilesPublishedSource source) => new(PMTilesSourceBindingKind.Bound, source);

    public static PMTilesSourceBinding NotFound(PMTilesSourceBindingKind kind) => new(kind, null);
}

internal readonly record struct PMTilesProxyResolution(bool Exists, CloudFile? Metadata)
{
    public static PMTilesProxyResolution NotFound() => new(false, null);
    public static PMTilesProxyResolution Found(CloudFile metadata) => new(true, metadata);
}
