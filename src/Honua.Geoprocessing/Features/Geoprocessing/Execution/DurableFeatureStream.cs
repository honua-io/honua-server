// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Infrastructure.Security;
using NetTopologySuite.Features;

namespace Honua.Geoprocessing.Execution;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
internal sealed record DurableFeatureStream
{
    internal const string Prefix = "honua-feature-stream:v2;";
    internal const long MaximumBytes = 256L * 1024 * 1024;
    internal const int MaximumLineBytes = 8 * 1024 * 1024;

    public required string JobId { get; init; }
    public required int Attempt { get; init; }
    public required string ObjectKey { get; init; }
    public required string StoreReference { get; init; }
    public required CloudStorageProvider Provider { get; init; }
    public required long Count { get; init; }
    public required long Bytes { get; init; }
    public required string Sha256 { get; init; }

    public string Serialize() => Prefix + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, DurableFeatureStreamJsonContext.Default.DurableFeatureStream));

    public static bool TryParse(string? reference, out DurableFeatureStream? descriptor)
    {
        descriptor = null;
        if (reference is null || reference.Length > 8192 || !reference.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            descriptor = JsonSerializer.Deserialize(Convert.FromBase64String(reference[Prefix.Length..]), DurableFeatureStreamJsonContext.Default.DurableFeatureStream);
            return descriptor is { Attempt: > 0, Count: >= 0, Bytes: >= 0 and <= MaximumBytes, Sha256.Length: 64 }
                && descriptor.Sha256.All(char.IsAsciiHexDigit)
                && !string.IsNullOrWhiteSpace(descriptor.JobId)
                && !string.IsNullOrWhiteSpace(descriptor.ObjectKey)
                && !string.IsNullOrWhiteSpace(descriptor.StoreReference);
        }
        catch (Exception exception) when (exception is JsonException or FormatException)
        {
            return false;
        }
    }

    public static async IAsyncEnumerable<IFeature> ReadAsync(
        string reference,
        ExecutionJobRecord authorizedJob,
        IExecutionJobStore jobs,
        IGeoprocessingOutputObjectStore store,
        GeoprocessingOutputStagingOptions options,
        long maximumBytes = MaximumBytes,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!TryParse(reference, out var descriptor) || descriptor!.Bytes > maximumBytes
            || descriptor.Provider != store.Provider || descriptor.StoreReference != store.StoreReference
            || !options.Enabled || options.StoreReference != store.StoreReference
            || !GeoprocessingOutputObjectKeys.TryParse(options.KeyPrefix, descriptor.ObjectKey, out var producerId, out var attempt)
            || producerId != descriptor.JobId || attempt != descriptor.Attempt)
        {
            throw new InvalidOperationException("The shared feature stream identity or configured store does not match.");
        }

        var producer = await jobs.GetAsync(descriptor.JobId, cancellationToken).ConfigureAwait(false);
        var owner = authorizedJob.Audit.SubmitterSecurityContext;
        if (producer is null || producer.AttemptCount != descriptor.Attempt
            || (producer.Status != ExecutionJobStatus.Succeeded
                && (producer.Status != ExecutionJobStatus.Running || producer.OperationId != authorizedJob.OperationId))
            || !producer.ArtifactReferences.Contains(reference, StringComparer.Ordinal)
            || !JobOwnershipSecurity.MatchesSubmitter(producer.Audit, owner?.OwnerActorId, owner?.TenantId))
        {
            throw new InvalidOperationException("The feature stream is not a published output owned by this job's submitter.");
        }

        var info = await store.GetInfoAsync(descriptor.ObjectKey, cancellationToken).ConfigureAwait(false);
        if (info is null || info.SizeBytes != descriptor.Bytes
            || !await store.TryAcquireReadLeaseAsync(descriptor.ObjectKey, options.ReadLeaseDuration, cancellationToken).ConfigureAwait(false))
        {
            throw new InvalidOperationException("The shared feature stream is unavailable.");
        }

        await using var source = await store.OpenReadAsync(descriptor.ObjectKey, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("The shared feature stream is unavailable.");
        await using var verified = new FileStream(Path.Combine(Path.GetTempPath(), Path.GetRandomFileName()),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 65536, FileOptions.Asynchronous | FileOptions.DeleteOnClose);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[65536];
        long total = 0;
        var lineBytes = 0;
        long count = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            total += read;
            if (total > descriptor.Bytes)
            {
                throw new InvalidOperationException("The feature stream exceeds its immutable byte receipt.");
            }

            for (var index = 0; index < read; index++)
            {
                if (buffer[index] == (byte)'\n')
                {
                    count++;
                    lineBytes = 0;
                }
                else if (++lineBytes > MaximumLineBytes)
                {
                    throw new InvalidOperationException("A feature stream row exceeds its byte limit.");
                }
            }

            hash.AppendData(buffer, 0, read);
            await verified.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
        }

        if (total != descriptor.Bytes || lineBytes != 0 || count != descriptor.Count
            || !string.Equals(Convert.ToHexStringLower(hash.GetHashAndReset()), descriptor.Sha256, StringComparison.Ordinal))
        {
            throw new InvalidOperationException("The feature stream bytes do not match the published immutable receipt.");
        }

        verified.Position = 0;
        using var lines = new StreamReader(verified, Encoding.UTF8, leaveOpen: true);
        var reader = GeoJsonArtifactCodec.CreateReader();
        while (await lines.ReadLineAsync(cancellationToken).ConfigureAwait(false) is { } line)
        {
            var feature = FeatureStreamArtifact.DeserializeLine(reader, line)
                ?? throw new InvalidOperationException("The feature stream contains an invalid row.");
            yield return feature;
        }
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DurableFeatureStream))]
internal sealed partial class DurableFeatureStreamJsonContext : JsonSerializerContext;
