// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Honua.Cloud.Aws.Features;
using Honua.Core.Features.Import.ObjectStore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

/// <summary>
/// Customer-bucket reader. Uses the host identity (the SDK default chain) and, when the
/// request names an allow-listed role, assumes that role with the configured external id.
/// </summary>
internal sealed partial class AwsImportObjectStoreReader : IImportObjectStoreReader, IDisposable
{
    internal const string RoleSessionName = "honua-import";

    private readonly IOptionsMonitor<ImportObjectStoreOptions> _options;
    private readonly ILogger<AwsImportObjectStoreReader>? _logger;
    private readonly bool _ownsClients;
    private readonly Lock _gate = new();
    private IAmazonS3? _client;
    private IAmazonSecurityTokenService? _sts;
    private AmazonS3Config? _baseConfig;
    private bool _disposed;

    /// <summary>Creates a reader that builds AWS clients on first use.</summary>
    public AwsImportObjectStoreReader(
        IOptionsMonitor<ImportObjectStoreOptions> options,
        ILogger<AwsImportObjectStoreReader> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        _ownsClients = true;
    }

    /// <summary>Creates a reader over clients the caller owns (emulator tests).</summary>
    internal AwsImportObjectStoreReader(
        IAmazonS3 client,
        IAmazonSecurityTokenService? sts,
        IOptionsMonitor<ImportObjectStoreOptions> options,
        ILogger<AwsImportObjectStoreReader>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        _client = client;
        _sts = sts;
        _options = options;
        _logger = logger;
        _ownsClients = false;
        _baseConfig = client is AmazonS3Client s3 ? (AmazonS3Config)s3.Config : new AmazonS3Config();
    }

    /// <summary>Builds the AssumeRole request. The external id is sent only when configured.</summary>
    internal static AssumeRoleRequest BuildAssumeRoleRequest(string roleArn, string? externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(roleArn);
        var request = new AssumeRoleRequest
        {
            RoleArn = roleArn,
            RoleSessionName = RoleSessionName,
            DurationSeconds = 900,
        };
        if (!string.IsNullOrWhiteSpace(externalId))
        {
            request.ExternalId = externalId;
        }

        return request;
    }

    /// <inheritdoc />
    public async Task<ImportObjectHead> HeadAsync(ImportObjectLocation location, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        var lease = await LeaseAsync(location, cancellationToken).ConfigureAwait(false);
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
            if (_logger is not null)
            {
                Log.HostIdentityUnavailable(_logger, location.Bucket);
            }

            throw ImportObjectStoreException.Forbidden("The host identity cannot read the object.");
        }
        finally
        {
            lease.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<Stream> OpenReadAsync(
        ImportObjectLocation location,
        string etag,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentException.ThrowIfNullOrWhiteSpace(etag);
        var lease = await LeaseAsync(location, cancellationToken).ConfigureAwait(false);
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
            lease.Dispose();
            throw MapS3(ex, location);
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (_disposed || !_ownsClients)
        {
            _disposed = true;
            return;
        }

        _disposed = true;
        _client?.Dispose();
        _sts?.Dispose();
    }

    private async Task<ClientLease> LeaseAsync(ImportObjectLocation location, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(location.AssumeRoleArn))
        {
            return new ClientLease(Client, dispose: false);
        }

        var role = FindRole(location.AssumeRoleArn);
        if (role is null)
        {
            throw ImportObjectStoreException.Forbidden("assumeRoleArn is not allow-listed.");
        }

        AssumeRoleResponse response;
        try
        {
            response = await Sts.AssumeRoleAsync(BuildAssumeRoleRequest(role.RoleArn.Trim(), role.ExternalId), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (AmazonSecurityTokenServiceException ex)
        {
            if (_logger is not null)
            {
                Log.AssumeRoleFailed(_logger, role.RoleArn, ex.ErrorCode);
            }

            throw ImportObjectStoreException.Forbidden("The host identity cannot assume the requested role.");
        }
        catch (AmazonClientException)
        {
            if (_logger is not null)
            {
                Log.HostIdentityUnavailable(_logger, location.Bucket);
            }

            throw ImportObjectStoreException.Forbidden("The host identity cannot read the object.");
        }

        var credentials = response.Credentials
            ?? throw ImportObjectStoreException.Forbidden("The host identity cannot assume the requested role.");
        var session = new SessionAWSCredentials(
            credentials.AccessKeyId,
            credentials.SecretAccessKey,
            credentials.SessionToken);
        var client = new AmazonS3Client(session, CloneConfig(BaseConfig));
        return new ClientLease(client, dispose: true);
    }

    private ImportObjectStoreRoleOptions? FindRole(string roleArn)
        => _options.CurrentValue.AssumeRoles.FirstOrDefault(role =>
            string.Equals(role.RoleArn?.Trim(), roleArn, StringComparison.Ordinal));

    private IAmazonS3 Client
    {
        get
        {
            if (_client is not null)
            {
                return _client;
            }

            lock (_gate)
            {
                return _client ??= CreateS3Client();
            }
        }
    }

    private IAmazonSecurityTokenService Sts
    {
        get
        {
            if (_sts is not null)
            {
                return _sts;
            }

            lock (_gate)
            {
                if (_sts is not null)
                {
                    return _sts;
                }

                if (!_ownsClients)
                {
                    throw ImportObjectStoreException.Forbidden("The host identity cannot assume the requested role.");
                }

                return _sts = CreateStsClient();
            }
        }
    }

    private AmazonS3Config BaseConfig
    {
        get
        {
            if (_baseConfig is not null)
            {
                return _baseConfig;
            }

            _ = Client;
            return _baseConfig ?? new AmazonS3Config();
        }
    }

    private AmazonS3Client CreateS3Client()
    {
        var config = CreateS3Config();
        _baseConfig = config;
        return new AmazonS3Client(config);
    }

    private AmazonSecurityTokenServiceClient CreateStsClient()
    {
        var s3 = CreateS3Config();
        var config = new AmazonSecurityTokenServiceConfig();
        if (s3.RegionEndpoint is not null)
        {
            config.RegionEndpoint = s3.RegionEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(s3.ServiceURL))
        {
            config.ServiceURL = s3.ServiceURL;
            config.AuthenticationRegion = s3.AuthenticationRegion;
        }

        return new AmazonSecurityTokenServiceClient(config);
    }

    private AmazonS3Config CreateS3Config()
    {
        var options = _options.CurrentValue;
        var region = options.Region;
        if (string.IsNullOrWhiteSpace(region) && !string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            region = "us-east-1";
        }

        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle || !string.IsNullOrWhiteSpace(options.ServiceUrl),
        };
        AwsClientEndpoint.Apply(config, region, options.ServiceUrl);
        return config;
    }

    private static AmazonS3Config CloneConfig(AmazonS3Config source)
    {
        var config = new AmazonS3Config { ForcePathStyle = source.ForcePathStyle };
        if (source.RegionEndpoint is not null)
        {
            config.RegionEndpoint = source.RegionEndpoint;
        }

        if (!string.IsNullOrWhiteSpace(source.ServiceURL))
        {
            config.ServiceURL = source.ServiceURL;
            config.AuthenticationRegion = source.AuthenticationRegion;
        }

        return config;
    }

    private ImportObjectStoreException MapS3(AmazonS3Exception exception, ImportObjectLocation location)
    {
        if (_logger is not null)
        {
            Log.ReadFailed(_logger, location.Bucket, exception.ErrorCode, (int)exception.StatusCode);
        }
        if (exception.StatusCode == HttpStatusCode.PreconditionFailed
            || string.Equals(exception.ErrorCode, "PreconditionFailed", StringComparison.OrdinalIgnoreCase))
        {
            return ImportObjectStoreException.Changed("The object changed after the preflight ETag was recorded.");
        }

        if (IsNotFound(exception))
        {
            return ImportObjectStoreException.NotFound("The object was not found.");
        }

        return ImportObjectStoreException.Forbidden("The host identity cannot read the object.");
    }

    private static bool IsNotFound(AmazonS3Exception exception)
        => exception.StatusCode == HttpStatusCode.NotFound
           || string.Equals(exception.ErrorCode, "NoSuchKey", StringComparison.OrdinalIgnoreCase)
           || string.Equals(exception.ErrorCode, "NoSuchBucket", StringComparison.OrdinalIgnoreCase)
           || string.Equals(exception.ErrorCode, "NotFound", StringComparison.OrdinalIgnoreCase);

    private readonly struct ClientLease(IAmazonS3 client, bool dispose)
    {
        public IAmazonS3 Client { get; } = client;

        public void Dispose()
        {
            if (dispose)
            {
                Client.Dispose();
            }
        }

        public IAmazonS3? Detach() => dispose ? Client : null;
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

        [LoggerMessage(EventId = 4811, Level = LogLevel.Warning, Message = "AssumeRole failed for {RoleArn} with {ErrorCode}")]
        public static partial void AssumeRoleFailed(ILogger logger, string roleArn, string? errorCode);

        [LoggerMessage(EventId = 4812, Level = LogLevel.Warning, Message = "Object store host identity is unavailable for bucket {Bucket}")]
        public static partial void HostIdentityUnavailable(ILogger logger, string bucket);
    }
}

/// <summary>Registers the customer-bucket reader. Options binding stays in the server composition.</summary>
internal static class AwsImportObjectStoreServiceCollectionExtensions
{
    /// <summary>Registers <see cref="IImportObjectStoreReader"/>.</summary>
    public static IServiceCollection AddAwsImportObjectStore(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.AddSingleton<IImportObjectStoreReader, AwsImportObjectStoreReader>();
        return services;
    }
}
