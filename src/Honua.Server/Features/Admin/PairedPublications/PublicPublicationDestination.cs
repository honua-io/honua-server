// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Publishing.Descriptors;
using Honua.Infrastructure.SharedCaching;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed class PublicPublicationDestination : IPublicPublicationDestination
{
    internal const string RootCatalogKey = "data/catalog.json";
    internal const string Sha256MetadataKey = "sha256";
    private const string JsonContentType = "application/json";

    private readonly ICloudFileStorage _storage;
    private readonly Uri _origin;

    public PublicPublicationDestination(ICloudFileStorage storage, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ValidateOrigin(origin);
        _storage = storage;
        _origin = origin;
    }

    public Uri Origin => _origin;

    internal static void ValidateOrigin(Uri origin)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri || origin.Scheme != Uri.UriSchemeHttps ||
            !string.IsNullOrEmpty(origin.UserInfo) || origin.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(origin.Query) || !string.IsNullOrEmpty(origin.Fragment))
        {
            throw new ArgumentException("Public data BaseUrl must be an HTTPS origin without credentials, path, query or fragment.", nameof(origin));
        }
    }

    public async Task<Uri> PutAsync(PublicPublicationArtifact artifact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(artifact);
        var key = ObjectKey(artifact.Key);
        ArgumentNullException.ThrowIfNull(artifact.Content);
        ArgumentException.ThrowIfNullOrWhiteSpace(artifact.ContentType);
        ArgumentOutOfRangeException.ThrowIfNegative(artifact.SizeBytes ?? 0);

        if (!string.IsNullOrWhiteSpace(artifact.Sha256) &&
            await HasMatchingChecksumAsync(key, artifact.Sha256, cancellationToken).ConfigureAwait(false))
        {
            return new Uri(_origin, key);
        }

        var metadata = MetadataFor(artifact.Provenance, artifact.Sha256);
        var result = await _storage.UploadAsync(new FileUploadRequest
        {
            Content = artifact.Content,
            FileName = artifact.Key.FileName,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes ?? (artifact.Content.CanSeek ? artifact.Content.Length - artifact.Content.Position : null),
            ObjectKeyOverride = key,
            CachePolicy = artifact.CachePolicy,
            Metadata = metadata,
            TimeToLive = null
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException("Writing the public publication artifact failed.");
        }

        return new Uri(_origin, key);
    }

    public async Task<Uri> PutRootCatalogAsync(Stream content, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        var result = await _storage.UploadAsync(new FileUploadRequest
        {
            Content = content,
            FileName = "catalog.json",
            ContentType = JsonContentType,
            SizeBytes = content.CanSeek ? content.Length - content.Position : null,
            ObjectKeyOverride = RootCatalogKey,
            CachePolicy = StoredObjectCachePolicy.PublicReplacedInPlace,
            Metadata = ImmutableDictionary<string, string>.Empty,
            TimeToLive = null
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException("Writing the public export root catalog failed.");
        }

        return new Uri(_origin, RootCatalogKey);
    }

    public async Task DeleteAsync(PublicPublicationArtifactKey key, CancellationToken cancellationToken)
    {
        var objectKey = ObjectKey(key);
        await DeleteConfirmedAsync(objectKey, cancellationToken).ConfigureAwait(false);
    }

    public async Task DeletePublicationAsync(string publicationId, CancellationToken cancellationToken)
    {
        if (!PairedPublishRequestRules.IsPublicationId(publicationId))
        {
            throw new ArgumentException("A valid publication identifier is required.", nameof(publicationId));
        }

        var prefix = PublicationPrefix(publicationId);
        var files = await _storage.ListFilesAsync(prefix, maxResults: 1000, includeMetadata: false, cancellationToken).ConfigureAwait(false);
        foreach (var file in files)
        {
            var objectKey = !string.IsNullOrWhiteSpace(file.StoragePath) ? file.StoragePath : file.FileId;
            if (string.IsNullOrWhiteSpace(objectKey) || !objectKey.StartsWith(prefix, StringComparison.Ordinal))
            {
                continue;
            }

            await DeleteConfirmedAsync(objectKey, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static string PublicationPrefix(string publicationId) => $"data/publications/{publicationId}/";

    internal static string VersionKey(string publicationId, string version, string fileName)
        => $"data/publications/{publicationId}/{version}/{fileName}";

    private async Task<bool> HasMatchingChecksumAsync(string key, string sha256, CancellationToken cancellationToken)
    {
        var existing = await _storage.GetMetadataAsync(key, cancellationToken).ConfigureAwait(false);
        return existing is not null &&
               existing.Metadata.Any(pair =>
                   string.Equals(pair.Key, Sha256MetadataKey, StringComparison.OrdinalIgnoreCase) &&
                   string.Equals(pair.Value, sha256, StringComparison.OrdinalIgnoreCase));
    }

    private async Task DeleteConfirmedAsync(string objectKey, CancellationToken cancellationToken)
    {
        if (!await _storage.DeleteAsync(objectKey, cancellationToken).ConfigureAwait(false) &&
            await _storage.ExistsAsync(objectKey, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Deleting the public publication artifact failed.");
        }
    }

    private static ImmutableDictionary<string, string> MetadataFor(PublicPublicationProvenance? provenance, string? sha256)
    {
        var metadata = ImmutableDictionary<string, string>.Empty;
        if (!string.IsNullOrWhiteSpace(sha256))
        {
            metadata = metadata.Add(Sha256MetadataKey, sha256.Trim().ToLowerInvariant());
        }

        if (provenance is null)
        {
            return metadata;
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(provenance.ServiceId);
        ArgumentOutOfRangeException.ThrowIfNegative(provenance.LayerIndex);
        ArgumentOutOfRangeException.ThrowIfNegative(provenance.MetadataRevision);
        var layer = provenance.LayerIndex.ToString(CultureInfo.InvariantCulture);
        var tags = CdnCacheTags.ForResponse(new(provenance.ServiceId, layer), new PathString("/rest/services"), provenance.ServiceId);
        return metadata
            .Add("cache-tag", string.Join(',', tags))
            .Add("metadata-revision", provenance.MetadataRevision.ToString(CultureInfo.InvariantCulture));
    }

    private static string ObjectKey(PublicPublicationArtifactKey key)
    {
        ArgumentNullException.ThrowIfNull(key);
        if (!PairedPublishRequestRules.IsPublicationId(key.PublicationId) ||
            string.IsNullOrEmpty(key.Version) || key.Version.Length > 128 ||
            !key.Version.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_'))
        {
            throw new ArgumentException("A public artifact requires a valid publication and generation identifier.", nameof(key));
        }

        if (!IsAllowedFileName(key.PublicationId, key.FileName))
        {
            throw new ArgumentException("Only paired publication generation artifacts may be copied to public data storage.", nameof(key));
        }

        return VersionKey(key.PublicationId, key.Version, key.FileName);
    }

    private static bool IsAllowedFileName(string publicationId, string fileName)
        => fileName == $"{publicationId}.pmtiles" ||
           fileName == $"{publicationId}.parquet" ||
           fileName == ExportCollectionDocument.FileName ||
           ExportCompanionFiles.FileNames.Contains(fileName);
}
