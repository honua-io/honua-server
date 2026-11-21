// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Concurrent;
using System.Net;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.SecurityToken;
using Amazon.SecurityToken.Model;
using Honua.Cloud.Aws.Features;
using Honua.Core.Features.Import.ObjectStore;
using Honua.Core.Features.ObjectStorage;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.FileStorage;

// Test seams for the factory: the host-identity client, STS, and the client built from session credentials.
internal sealed record AwsObjectStoreClientSeams
{
    public required IAmazonS3 HostClient { get; init; }

    public IAmazonSecurityTokenService? Sts { get; init; }

    public Func<AWSCredentials, IAmazonS3>? SessionClient { get; init; }

    public ImportObjectStoreOptions Options { get; init; } = new();

    public IHostEnvironment? Environment { get; init; }
}

// Builds S3 clients for one connection. A customer connection always assumes its role with the bound
// external id and an inline read-only session policy; built-ins use the task identity. Session
// credentials live only inside the returned client and are never logged.
internal sealed partial class AwsObjectStoreClientFactory : IDisposable
{
    internal const string RoleSessionName = "honua-connection";

    private readonly IOptionsMonitor<ImportObjectStoreOptions>? _options;
    private readonly AwsObjectStoreClientSeams? _seams;
    private readonly IHostEnvironment? _environment;
    private readonly ILogger? _logger;
    private readonly ConcurrentDictionary<string, IAmazonS3> _hostClients = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private IAmazonSecurityTokenService? _sts;
    private bool _disposed;

    public AwsObjectStoreClientFactory(
        IOptionsMonitor<ImportObjectStoreOptions> options,
        ILogger<AwsObjectStoreClientFactory> logger,
        IHostEnvironment? environment = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _options = options;
        _logger = logger;
        _environment = environment;
    }

    internal AwsObjectStoreClientFactory(AwsObjectStoreClientSeams seams)
    {
        ArgumentNullException.ThrowIfNull(seams);
        _seams = seams;
        _environment = seams.Environment;
        _sts = seams.Sts;
    }

    private ImportObjectStoreOptions Options => _seams?.Options ?? _options!.CurrentValue;

    internal static AssumeRoleRequest BuildAssumeRoleRequest(ObjectStorageConnection connection)
    {
        ArgumentNullException.ThrowIfNull(connection);
        if (!ObjectStoragePolicy.IsValidRoleArn(connection.RoleArn) || string.IsNullOrWhiteSpace(connection.ExternalId))
        {
            throw ImportObjectStoreException.Forbidden("The connection has no usable role binding.");
        }

        return new AssumeRoleRequest
        {
            RoleArn = connection.RoleArn,
            RoleSessionName = RoleSessionName,
            ExternalId = connection.ExternalId,
            DurationSeconds = ObjectStorageSessionPolicy.DurationSeconds,
            Policy = ObjectStorageSessionPolicy.Build(connection.Bucket, connection.Prefix),
        };
    }

    public async Task<AwsObjectStoreLease> LeaseAsync(ObjectStorageConnection connection, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        EnsureEndpointAllowed();
        if (connection.BuiltIn)
        {
            return AwsObjectStoreLease.Borrowed(HostClient(connection.Region));
        }

        var request = BuildAssumeRoleRequest(connection);
        var credentials = await AssumeAsync(request, cancellationToken).ConfigureAwait(false);
        var session = new SessionAWSCredentials(credentials.AccessKeyId, credentials.SecretAccessKey, credentials.SessionToken);
        if (_seams?.SessionClient is { } seam)
        {
            return AwsObjectStoreLease.Owned(seam(session));
        }

        // The lease owns the session client from here; it is disposed only if the hand-over fails.
        AmazonS3Client? client = null;
        try
        {
            client = new AmazonS3Client(session, CreateConfig(connection.Region));
            var lease = AwsObjectStoreLease.Owned(client);
            client = null;
            return lease;
        }
        finally
        {
            client?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        if (_seams is not null)
        {
            return;
        }

        foreach (var client in _hostClients.Values)
        {
            client.Dispose();
        }

        _sts?.Dispose();
    }

    private async Task<Credentials> AssumeAsync(AssumeRoleRequest request, CancellationToken cancellationToken)
    {
        try
        {
            var response = await Sts.AssumeRoleAsync(request, cancellationToken).ConfigureAwait(false);
            return response.Credentials
                ?? throw ImportObjectStoreException.Forbidden("The host identity cannot assume the connection role.");
        }
        catch (AmazonSecurityTokenServiceException ex)
        {
            if (_logger is not null)
            {
                Log.AssumeRoleFailed(_logger, request.RoleArn, ex.ErrorCode, (int)ex.StatusCode);
            }

            throw ImportObjectStoreException.Forbidden(
                ex.StatusCode == HttpStatusCode.Forbidden || string.Equals(ex.ErrorCode, "AccessDenied", StringComparison.Ordinal)
                    ? "The connection role does not trust this installation with this external id."
                    : "The host identity cannot assume the connection role.");
        }
        catch (AmazonClientException)
        {
            throw ImportObjectStoreException.Forbidden("The host identity is unavailable.");
        }
    }

    // An endpoint override exists only for emulators and is refused in Production.
    private void EnsureEndpointAllowed()
    {
        if (!string.IsNullOrWhiteSpace(Options.ServiceUrl) && _environment?.IsProduction() == true)
        {
            throw ImportObjectStoreException.Forbidden("An object storage endpoint override is not allowed in Production.");
        }
    }

    private IAmazonS3 HostClient(string region)
    {
        if (_seams is not null)
        {
            return _seams.HostClient;
        }

        var key = string.IsNullOrWhiteSpace(Options.ServiceUrl) ? region : "endpoint";
        return _hostClients.GetOrAdd(key, _ => new AmazonS3Client(CreateConfig(region)));
    }

    private IAmazonSecurityTokenService Sts
    {
        get
        {
            lock (_gate)
            {
                if (_sts is null && _seams is not null)
                {
                    throw ImportObjectStoreException.Forbidden("The host identity cannot assume the connection role.");
                }

                return _sts ??= CreateStsClient();
            }
        }
    }

    private AmazonSecurityTokenServiceClient CreateStsClient()
    {
        var options = Options;
        var config = new AmazonSecurityTokenServiceConfig();
        AwsClientEndpoint.Apply(config, EffectiveRegion(options.Region ?? "us-east-1"), options.ServiceUrl);
        return new AmazonSecurityTokenServiceClient(config);
    }

    private AmazonS3Config CreateConfig(string region)
    {
        var options = Options;
        var config = new AmazonS3Config
        {
            ForcePathStyle = options.ForcePathStyle || !string.IsNullOrWhiteSpace(options.ServiceUrl),
        };
        AwsClientEndpoint.Apply(config, EffectiveRegion(region), options.ServiceUrl);
        return config;
    }

    private string EffectiveRegion(string region)
    {
        var options = Options;
        if (!string.IsNullOrWhiteSpace(options.ServiceUrl))
        {
            return string.IsNullOrWhiteSpace(options.Region) ? "us-east-1" : options.Region;
        }

        return ObjectStoragePolicy.IsKnownRegion(region) ? region : "us-east-1";
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 4811, Level = LogLevel.Warning, Message = "AssumeRole failed for {RoleArn} with {ErrorCode} status {StatusCode}")]
        public static partial void AssumeRoleFailed(ILogger logger, string roleArn, string? errorCode, int statusCode);
    }
}

internal sealed class AwsObjectStoreLease : IDisposable
{
    private IAmazonS3? _owned;

    private AwsObjectStoreLease(IAmazonS3 client, IAmazonS3? owned)
    {
        Client = client;
        _owned = owned;
    }

    public IAmazonS3 Client { get; }

    public static AwsObjectStoreLease Owned(IAmazonS3 client) => new(client, client);

    public static AwsObjectStoreLease Borrowed(IAmazonS3 client) => new(client, null);

    // Hands ownership of a session client to a response stream.
    public IAmazonS3? Detach()
    {
        var owned = _owned;
        _owned = null;
        return owned;
    }

    public void Dispose()
    {
        _owned?.Dispose();
        _owned = null;
    }
}

internal static class AwsObjectStoreErrors
{
    public static bool IsNotFound(AmazonS3Exception exception)
        => exception.StatusCode == HttpStatusCode.NotFound
           || exception.ErrorCode is "NoSuchKey" or "NoSuchBucket" or "NotFound";

    public static bool IsRequesterPaysSignal(AmazonS3Exception exception)
        => exception.Message.Contains("RequestPayer", StringComparison.OrdinalIgnoreCase)
           || exception.Message.Contains("requester pays", StringComparison.OrdinalIgnoreCase);
}
