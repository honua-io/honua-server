// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;

namespace Honua.FileStorage;

// Test and browse for object storage connections. Kept apart from the database connection drivers.
internal sealed class AwsObjectStoreConnector(AwsObjectStoreClientFactory clients) : IObjectStoreConnector
{
    internal const int PageSize = 200;

    // Header sniffing is bounded: at most this many objects per page, each read with a ranged GET.
    internal const int MaxSniffedObjects = 25;

    private const int SniffParallelism = 8;

    public async Task<ObjectStorageTestResult> TestAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        try
        {
            using var lease = await clients.LeaseAsync(connection, cancellationToken).ConfigureAwait(false);
            var location = await RunAsync(lease, connection, client => client.GetBucketLocationAsync(
                new GetBucketLocationRequest { BucketName = connection.Bucket },
                cancellationToken)).ConfigureAwait(false);
            var actualRegion = BucketRegion(location.Location?.Value);
            if (!string.Equals(actualRegion, connection.Region, StringComparison.Ordinal))
            {
                return Unhealthy($"The bucket is in {actualRegion}, not {connection.Region}.");
            }

            await RunAsync(lease, connection, client => client.ListObjectsV2Async(
                new ListObjectsV2Request { BucketName = connection.Bucket, Prefix = connection.Prefix, MaxKeys = 1 },
                cancellationToken)).ConfigureAwait(false);
            return new ObjectStorageTestResult { IsHealthy = true, Message = "Connected. The role can list the connection prefix." };
        }
        catch (ImportObjectStoreException ex)
        {
            return Unhealthy(ex.Message);
        }
    }

    public async Task<ObjectStorageBrowseResult> BrowseAsync(ObjectStorageBrowseRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        var connection = request.Connection;
        using var lease = await clients.LeaseAsync(connection, cancellationToken).ConfigureAwait(false);
        var page = await RunAsync(lease, connection, client => client.ListObjectsV2Async(
            new ListObjectsV2Request
            {
                BucketName = connection.Bucket,
                Prefix = request.Prefix,
                Delimiter = "/",
                MaxKeys = PageSize,
            },
            cancellationToken)).ConfigureAwait(false);

        var objects = (page.S3Objects ?? [])
            .Where(item => item.Key != request.Prefix && ObjectStoragePolicy.IsKeyUnderPrefix(connection.Prefix, item.Key))
            .ToList();
        var kinds = await SniffAsync(new SniffTarget(lease.Client, connection.Bucket), objects, cancellationToken).ConfigureAwait(false);
        return new ObjectStorageBrowseResult
        {
            Bucket = connection.Bucket,
            Prefix = request.Prefix,
            Folders = (page.CommonPrefixes ?? []).Where(folder => folder.StartsWith(connection.Prefix, StringComparison.Ordinal)).ToList(),
            Objects = objects.Select((item, index) => new ObjectStorageBrowseEntry
            {
                Key = item.Key,
                Bytes = item.Size,
                Kind = kinds[index],
                LastModified = item.LastModified is { } modified ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)) : null,
            }).ToList(),
            Truncated = page.IsTruncated == true,
        };
    }

    private static async Task<string[]> SniffAsync(
        SniffTarget target,
        List<S3Object> objects,
        CancellationToken cancellationToken)
    {
        var kinds = Enumerable.Repeat(ObjectStorageObjectKinds.Other, objects.Count).ToArray();
        var targets = Enumerable.Range(0, objects.Count).Where(index => objects[index].Size >= 4).Take(MaxSniffedObjects);
        await Parallel.ForEachAsync(
            targets,
            new ParallelOptions { MaxDegreeOfParallelism = SniffParallelism, CancellationToken = cancellationToken },
            async (index, token) => kinds[index] = await SniffOneAsync(target, objects[index].Key, token).ConfigureAwait(false))
            .ConfigureAwait(false);
        return kinds;
    }

    private static async Task<string> SniffOneAsync(SniffTarget target, string key, CancellationToken cancellationToken)
    {
        try
        {
            using var response = await target.Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = target.Bucket,
                Key = key,
                ByteRange = new ByteRange(0, ObjectStorageObjectKinds.SniffBytes - 1),
            }, cancellationToken).ConfigureAwait(false);
            var buffer = new byte[ObjectStorageObjectKinds.SniffBytes];
            var read = await response.ResponseStream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false, cancellationToken)
                .ConfigureAwait(false);
            return ObjectStorageObjectKinds.Detect(buffer.AsSpan(0, read));
        }
        catch (Exception ex) when (ex is AmazonS3Exception or AmazonClientException or IOException)
        {
            // An unreadable object is still listed; its kind is simply unknown.
            return ObjectStorageObjectKinds.Other;
        }
    }

    private static async Task<T> RunAsync<T>(AwsObjectStoreLease lease, ObjectStorageConnection connection, Func<IAmazonS3, Task<T>> call)
    {
        try
        {
            return await call(lease.Client).ConfigureAwait(false);
        }
        catch (AmazonS3Exception ex)
        {
            throw await MapAsync(lease.Client, connection, ex).ConfigureAwait(false);
        }
        catch (AmazonClientException)
        {
            throw ImportObjectStoreException.Forbidden("Object storage could not be reached. Try again later.");
        }
    }

    // GetBucketLocation answers empty for us-east-1 and the legacy "EU" for eu-west-1.
    internal static string BucketRegion(string? location) => location switch
    {
        null or "" => "us-east-1",
        "EU" => "eu-west-1",
        _ => location,
    };

    // Requester-pays buckets answer 403; the bucket payment setting tells them apart when the role allows reading it.
    private static async Task<ImportObjectStoreException> MapAsync(IAmazonS3 client, ObjectStorageConnection connection, AmazonS3Exception exception)
    {
        if (AwsObjectStoreErrors.IsNotFound(exception))
        {
            return ImportObjectStoreException.NotFound("The bucket was not found.");
        }

        if (AwsObjectStoreErrors.IsRequesterPaysSignal(exception) || await IsRequesterPaysAsync(client, connection.Bucket).ConfigureAwait(false))
        {
            return ImportObjectStoreException.RequesterPays();
        }

        return ImportObjectStoreException.Forbidden("The connection role cannot list this bucket prefix.");
    }

    private static async Task<bool> IsRequesterPaysAsync(IAmazonS3 client, string bucket)
    {
        try
        {
            var payment = await client.GetBucketRequestPaymentAsync(new GetBucketRequestPaymentRequest { BucketName = bucket })
                .ConfigureAwait(false);
            return string.Equals(payment.Payer, "Requester", StringComparison.OrdinalIgnoreCase);
        }
        catch (AmazonS3Exception)
        {
            // The role may not allow reading the payment setting; the caller reports a plain refusal.
            return false;
        }
    }

    private static ObjectStorageTestResult Unhealthy(string message) => new() { IsHealthy = false, Message = message };

    private sealed record SniffTarget(IAmazonS3 Client, string Bucket);
}
