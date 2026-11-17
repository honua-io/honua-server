// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.FileStorage;

internal sealed partial class AwsImportObjectStoreReader(
    AwsObjectStoreClientFactory clients,
    ILogger<AwsImportObjectStoreReader>? logger = null) : IImportObjectStoreReader
{
    public async Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        using var lease = await clients.LeaseAsync(location.Connection, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await lease.Client.GetObjectMetadataAsync(location.Bucket, location.Key, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(response.ETag))
            {
                throw ImportObjectStoreException.Changed("The object did not return an ETag.");
            }

            return new ImportObjectHead
            {
                SizeBytes = response.ContentLength,
                ETag = response.ETag,
                ContentType = response.Headers.ContentType,
            };
        }
        catch (AmazonS3Exception ex)
        {
            throw MapS3(ex, location);
        }
        catch (AmazonClientException)
        {
            if (logger is not null)
            {
                Log.HostIdentityUnavailable(logger, location.Bucket);
            }

            throw ImportObjectStoreException.Forbidden("The host identity cannot read the object.");
        }
    }

    public async Task<Stream> OpenReadAsync(
        ImportObjectLocation location,
        string etag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        using var lease = await clients.LeaseAsync(location.Connection, cancellationToken).ConfigureAwait(false);
        try
        {
            var response = await lease.Client.GetObjectAsync(new GetObjectRequest
            {
                BucketName = location.Bucket,
                Key = location.Key,
                EtagToMatch = etag,
            }, cancellationToken).ConfigureAwait(false);
            return new ResponseBody(response, lease.Detach());
        }
        catch (AmazonS3Exception ex)
        {
            throw MapS3(ex, location);
        }
        catch (AmazonClientException)
        {
            throw ImportObjectStoreException.Forbidden("Object storage could not be reached. Try again later.");
        }
    }

    private ImportObjectStoreException MapS3(AmazonS3Exception exception, ImportObjectLocation location)
    {
        if (logger is not null)
        {
            Log.ReadFailed(logger, location.Bucket, exception.ErrorCode, (int)exception.StatusCode);
        }
        if (exception.StatusCode == HttpStatusCode.PreconditionFailed
            || string.Equals(exception.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase))
        {
            return ImportObjectStoreException.Changed("The object changed after the preflight ETag was recorded.");
        }

        if (AwsObjectStoreErrors.IsNotFound(exception))
        {
            return ImportObjectStoreException.NotFound("The object was not found.");
        }

        return AwsObjectStoreErrors.IsRequesterPaysSignal(exception)
            ? ImportObjectStoreException.RequesterPays()
            : ImportObjectStoreException.Forbidden("The connection cannot read the object.");
    }

    private sealed class ResponseBody : Stream
    {
        private readonly GetObjectResponse _response;
        private readonly IAmazonS3? _sessionClient;
        private readonly Stream _inner;
        private bool _disposed;

        public ResponseBody(GetObjectResponse response, IAmazonS3? sessionClient)
        {
            _response = response;
            _sessionClient = sessionClient;
            _inner = response.ResponseStream;
        }

        public override bool CanRead => _inner.CanRead;

        public override bool CanSeek => _inner.CanSeek;

        public override bool CanWrite => false;

        public override long Length => _inner.Length;

        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count) => _inner.Read(buffer, offset, count);

        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => _inner.ReadAsync(buffer, offset, count, cancellationToken);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => _inner.ReadAsync(buffer, cancellationToken);

        public override async ValueTask DisposeAsync()
        {
            try
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;
                await _inner.DisposeAsync().ConfigureAwait(false);
                _response.Dispose();
                _sessionClient?.Dispose();
            }
            finally
            {
                await base.DisposeAsync().ConfigureAwait(false);
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (!_disposed)
            {
                _disposed = true;
                if (disposing)
                {
                    _inner.Dispose();
                    _response.Dispose();
                    _sessionClient?.Dispose();
                }
            }

            base.Dispose(disposing);
        }
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4810, Level = LogLevel.Warning, Message = "Object store read failed for bucket {Bucket} with {ErrorCode} status {StatusCode}")]
        public static partial void ReadFailed(ILogger logger, string bucket, string? errorCode, int statusCode);

        [LoggerMessage(EventId = 4812, Level = LogLevel.Warning, Message = "Object store host identity is unavailable for bucket {Bucket}")]
        public static partial void HostIdentityUnavailable(ILogger logger, string bucket);
    }
}

internal static class AwsImportObjectStoreServiceCollectionExtensions
{
    public static IServiceCollection AddAwsImportObjectStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<AwsObjectStoreClientFactory>();
        services.AddSingleton<IImportObjectStoreReader, AwsImportObjectStoreReader>();
        services.AddSingleton<IObjectStoreConnector, AwsObjectStoreConnector>();
        return services;
    }
}
