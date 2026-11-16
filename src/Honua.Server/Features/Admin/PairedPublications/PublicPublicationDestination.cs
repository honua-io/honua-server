// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;

namespace Honua.Server.Features.Admin.PairedPublications;

internal sealed class PublicPublicationDestination : IPublicPublicationDestination
{
    private readonly ICloudFileStorage _storage;
    private readonly Uri _origin;

    public PublicPublicationDestination(ICloudFileStorage storage, Uri origin)
    {
        ArgumentNullException.ThrowIfNull(storage);
        ValidateOrigin(origin);
        _storage = storage;
        _origin = origin;
    }

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
        var result = await _storage.UploadAsync(new FileUploadRequest
        {
            Content = artifact.Content,
            FileName = artifact.Key.FileName,
            ContentType = artifact.ContentType,
            SizeBytes = artifact.SizeBytes ?? (artifact.Content.CanSeek ? artifact.Content.Length - artifact.Content.Position : null),
            ObjectKeyOverride = key,
            CachePolicy = StoredObjectCachePolicy.PublicReplacedInPlace,
            TimeToLive = null
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException("Writing the public publication artifact failed.");
        }

        return new Uri(_origin, key);
    }

    public async Task DeleteAsync(PublicPublicationArtifactKey key, CancellationToken cancellationToken)
    {
        if (!await _storage.DeleteAsync(ObjectKey(key), cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("Deleting the public publication artifact failed.");
        }
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

        if (key.FileName != $"{key.PublicationId}.pmtiles" && key.FileName != $"{key.PublicationId}.parquet" &&
            key.FileName is not ("descriptor.json" or "item.json"))
        {
            throw new ArgumentException("Only paired publication generation artifacts may be copied to public data storage.", nameof(key));
        }

        return $"data/publications/{key.PublicationId}/{key.Version}/{key.FileName}";
    }
}
