// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Admin.PairedPublications;

internal enum PublicExportCanaryState
{
    Pending,
    Readable,
    Unreadable
}

internal sealed record PromotedExportRecord
{
    public required string PublicationId { get; init; }

    public required string Version { get; init; }

    public required string ServiceId { get; init; }

    public required string ResourceId { get; init; }

    public int StorageLayerId { get; init; }

    public string? Title { get; init; }

    public required string CollectionUrl { get; init; }

    public required string CatalogUrl { get; init; }

    public DateTimeOffset PromotedAt { get; init; }

    public PublicExportCanaryState CanaryState { get; init; } = PublicExportCanaryState.Pending;

    public string? CanaryDetail { get; init; }
}

internal enum PublicExportRevocationState
{
    Requested,
    ObjectsDeleted,
    CatalogRewritten,
    InvalidationCreated,
    Completed
}

internal sealed record PublicExportRevocationRecord
{
    public required string RevocationId { get; init; }

    public required string PublicationId { get; init; }

    public required string Reason { get; init; }

    public required string ChangeId { get; init; }

    public PublicExportRevocationState State { get; init; } = PublicExportRevocationState.Requested;

    public string? InvalidationId { get; init; }

    public string? DistributionId { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public int AttemptCount { get; init; }

    public DateTimeOffset? NextAttemptAt { get; init; }

    public string? LastError { get; init; }
}

internal interface IPublicExportStateStore
{
    Task SavePromotedAsync(PromotedExportRecord record, CancellationToken cancellationToken);

    Task<PromotedExportRecord?> GetPromotedAsync(string publicationId, CancellationToken cancellationToken);

    Task DeletePromotedAsync(string publicationId, CancellationToken cancellationToken);

    Task<IReadOnlyList<PromotedExportRecord>> ListPromotedAsync(CancellationToken cancellationToken);

    Task SaveRevocationAsync(PublicExportRevocationRecord record, CancellationToken cancellationToken);

    Task<IReadOnlyList<PublicExportRevocationRecord>> ListPendingRevocationsAsync(CancellationToken cancellationToken);
}

internal sealed class CloudStoragePublicExportStateStore(
    ICloudFileStorage storage,
    IOptions<CloudStorageOptions> options) : IPublicExportStateStore
{
    private const string JsonContentType = "application/json";
    internal const int MaxStateRecords = 1000;

    private string RootPrefix
    {
        get
        {
            var root = PMTilesPublishKeyRoot.Resolve(options.Value);
            return string.IsNullOrEmpty(root) ? "publications/_public" : $"{root}/publications/_public";
        }
    }

    private string PromotedPrefix => $"{RootPrefix}/promoted/";

    private string RevocationPrefix => $"{RootPrefix}/revocations/";

    private string CompletedRevocationPrefix => $"{RootPrefix}/revocations-completed/";

    private string PromotedKey(string publicationId) => $"{PromotedPrefix}{publicationId}.json";

    private string RevocationKey(string revocationId) => $"{RevocationPrefix}{revocationId}.json";

    private string CompletedRevocationKey(string revocationId) => $"{CompletedRevocationPrefix}{revocationId}.json";

    public Task SavePromotedAsync(PromotedExportRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, PublicExportStateJsonContext.Default.PromotedExportRecord);
        return PutJsonBytesAsync(PromotedKey(record.PublicationId), payload, cancellationToken);
    }

    public Task<PromotedExportRecord?> GetPromotedAsync(string publicationId, CancellationToken cancellationToken)
        => ReadJsonAsync(PromotedKey(publicationId),
            PublicExportStateJsonContext.Default.PromotedExportRecord, cancellationToken);

    public async Task DeletePromotedAsync(string publicationId, CancellationToken cancellationToken)
    {
        var key = PromotedKey(publicationId);
        if (!await storage.DeleteAsync(key, cancellationToken).ConfigureAwait(false) &&
            await storage.ExistsAsync(key, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException($"Deleting promoted export record '{publicationId}' failed.");
        }
    }

    public async Task<IReadOnlyList<PromotedExportRecord>> ListPromotedAsync(CancellationToken cancellationToken)
    {
        var files = await ListAllOrFailClosedAsync(PromotedPrefix, cancellationToken).ConfigureAwait(false);
        var records = new List<PromotedExportRecord>();
        foreach (var file in files)
        {
            var key = !string.IsNullOrWhiteSpace(file.StoragePath) ? file.StoragePath : file.FileId;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(PromotedPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var record = await ReadJsonAsync(key,
                PublicExportStateJsonContext.Default.PromotedExportRecord, cancellationToken).ConfigureAwait(false);
            if (record is not null)
            {
                records.Add(record);
            }
        }

        return records;
    }

    public async Task SaveRevocationAsync(PublicExportRevocationRecord record, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(record);
        var payload = JsonSerializer.SerializeToUtf8Bytes(record, PublicExportStateJsonContext.Default.PublicExportRevocationRecord);
        if (record.State == PublicExportRevocationState.Completed)
        {
            await PutJsonBytesAsync(CompletedRevocationKey(record.RevocationId), payload, cancellationToken).ConfigureAwait(false);
            var activeKey = RevocationKey(record.RevocationId);
            if (!await storage.DeleteAsync(activeKey, cancellationToken).ConfigureAwait(false) &&
                await storage.ExistsAsync(activeKey, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidOperationException($"Archiving completed revocation '{record.RevocationId}' failed.");
            }
            return;
        }

        await PutJsonBytesAsync(RevocationKey(record.RevocationId), payload, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PublicExportRevocationRecord>> ListPendingRevocationsAsync(CancellationToken cancellationToken)
    {
        var files = await ListAllOrFailClosedAsync(RevocationPrefix, cancellationToken).ConfigureAwait(false);
        var records = new List<PublicExportRevocationRecord>();
        foreach (var file in files)
        {
            var key = !string.IsNullOrWhiteSpace(file.StoragePath) ? file.StoragePath : file.FileId;
            if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(RevocationPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var record = await ReadJsonAsync(key,
                PublicExportStateJsonContext.Default.PublicExportRevocationRecord, cancellationToken).ConfigureAwait(false);
            if (record is not null && record.State != PublicExportRevocationState.Completed)
            {
                records.Add(record);
            }
        }

        return records;
    }

    private async Task<IReadOnlyList<CloudFile>> ListAllOrFailClosedAsync(
        string prefix,
        CancellationToken cancellationToken)
    {
        var files = await storage.ListFilesAsync(prefix, maxResults: MaxStateRecords, includeMetadata: false, cancellationToken)
            .ConfigureAwait(false);
        if (files.Count >= MaxStateRecords)
        {
            throw new InvalidOperationException(
                $"Public export state under '{prefix}' reached the {MaxStateRecords}-record listing limit and cannot be enumerated safely.");
        }

        return files;
    }

    private async Task PutJsonBytesAsync(
        string key,
        byte[] payload,
        CancellationToken cancellationToken)
    {
        using var body = new MemoryStream(payload, writable: false);
        var result = await storage.UploadAsync(new FileUploadRequest
        {
            Content = body,
            FileName = Path.GetFileName(key),
            ContentType = JsonContentType,
            CachePolicy = StoredObjectCachePolicy.NotShared,
            SizeBytes = body.Length,
            ObjectKeyOverride = key,
            Metadata = ImmutableDictionary<string, string>.Empty,
            TimeToLive = null
        }, cancellationToken).ConfigureAwait(false);
        if (!result.Success)
        {
            throw new InvalidOperationException($"Writing public export state '{Path.GetFileName(key)}' failed.");
        }
    }

    private async Task<T?> ReadJsonAsync<T>(
        string key,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> typeInfo,
        CancellationToken cancellationToken)
        where T : class
    {
        var stream = await storage.DownloadAsync(key, cancellationToken).ConfigureAwait(false);
        if (stream is null)
        {
            return null;
        }

        await using (stream.ConfigureAwait(false))
        {
            return await JsonSerializer.DeserializeAsync(stream, typeInfo, cancellationToken).ConfigureAwait(false);
        }
    }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    WriteIndented = true)]
[JsonSerializable(typeof(PromotedExportRecord))]
[JsonSerializable(typeof(PublicExportRevocationRecord))]
internal sealed partial class PublicExportStateJsonContext : JsonSerializerContext;
