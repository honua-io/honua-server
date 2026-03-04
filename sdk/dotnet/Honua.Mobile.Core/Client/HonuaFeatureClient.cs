// Copyright (c) Honua. All rights reserved.
// Licensed under the Apache License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Honua.Mobile.Core.Abstractions;
using Honua.Mobile.Core.Auth;
using Honua.Mobile.Core.Converters;
using Honua.Mobile.Core.Proto;
using Microsoft.Extensions.Logging;

namespace Honua.Mobile.Core.Client;

/// <summary>
/// Main gRPC client for accessing Honua geospatial feature services.
/// Provides both unary and streaming access to feature data with mobile-optimized protocols.
/// </summary>
public sealed class HonuaFeatureClient : IFeatureReader, IFeatureWriter, IDisposable
{
    private readonly FeatureService.FeatureServiceClient _grpcClient;
    private readonly IMobileAuthenticationProvider _auth;
    private readonly ILogger<HonuaFeatureClient> _logger;
    private readonly GrpcChannel _channel;
    private bool _disposed;

    /// <summary>
    /// Initializes a new instance of the HonuaFeatureClient.
    /// </summary>
    /// <param name="serverUrl">The base URL of the Honua server</param>
    /// <param name="authProvider">Authentication provider for API keys or OIDC</param>
    /// <param name="logger">Optional logger for diagnostics</param>
    public HonuaFeatureClient(
        string serverUrl,
        IMobileAuthenticationProvider authProvider,
        ILogger<HonuaFeatureClient>? logger = null)
    {
        if (string.IsNullOrWhiteSpace(serverUrl))
            throw new ArgumentException("Server URL cannot be null or empty", nameof(serverUrl));

        _auth = authProvider ?? throw new ArgumentNullException(nameof(authProvider));
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HonuaFeatureClient>.Instance;

        // Configure gRPC channel for mobile scenarios
        var channelOptions = new GrpcChannelOptions
        {
            // Configure keep-alive for mobile network stability
            HttpHandler = new SocketsHttpHandler
            {
                PooledConnectionIdleTimeout = TimeSpan.FromMinutes(5),
                KeepAlivePingDelay = TimeSpan.FromSeconds(30),
                KeepAlivePingTimeout = TimeSpan.FromSeconds(5),
                EnableMultipleHttp2Connections = true
            }
        };

        _channel = GrpcChannel.ForAddress(serverUrl, channelOptions);
        _grpcClient = new FeatureService.FeatureServiceClient(_channel);

        _logger.LogDebug("Initialized HonuaFeatureClient for server: {ServerUrl}", serverUrl);
    }

    /// <inheritdoc />
    public async Task<Models.QueryResult<Models.Feature>> QueryAsync(
        string serviceId,
        int layerId,
        Models.FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        ValidateDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(query);

        try
        {
            _logger.LogDebug("Executing feature query for service {ServiceId}, layer {LayerId}", serviceId, layerId);

            var request = ProtoConverters.ToProtoRequest(serviceId, layerId, query);
            var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);

            var response = await _grpcClient.QueryFeaturesAsync(request, headers, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var result = ProtoConverters.FromProtoResponse(response);

            _logger.LogDebug("Query returned {FeatureCount} features for service {ServiceId}, layer {LayerId}",
                result.Items.Count, serviceId, layerId);

            return result;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during feature query for service {ServiceId}, layer {LayerId}: {Status}",
                serviceId, layerId, ex.Status);
            throw new HonuaClientException($"Query failed: {ex.Status.Detail}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during feature query for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw new HonuaClientException("Query failed due to unexpected error", ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Models.Feature> QueryStreamAsync(
        string serviceId,
        int layerId,
        Models.FeatureQuery query,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        ValidateDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(query);

        AsyncServerStreamingCall<FeaturePage>? call = null;

        _logger.LogDebug("Starting streaming feature query for service {ServiceId}, layer {LayerId}", serviceId, layerId);

        var request = ProtoConverters.ToProtoRequest(serviceId, layerId, query);
        var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);

        call = _grpcClient.QueryFeaturesStream(request, headers, cancellationToken: cancellationToken);

        var featureCount = 0;
        try
        {
            await foreach (var page in call.ResponseStream.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                foreach (var protoFeature in page.Features)
                {
                    yield return ProtoConverters.FromProtoFeature(protoFeature);
                    featureCount++;
                }

                if (page.IsLastPage)
                {
                    _logger.LogDebug("Completed streaming query with {FeatureCount} features for service {ServiceId}, layer {LayerId}",
                        featureCount, serviceId, layerId);
                    break;
                }
            }
        }
        finally
        {
            call?.Dispose();
        }
    }

    /// <inheritdoc />
    public async Task<long> CountAsync(
        string serviceId,
        int layerId,
        Models.FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var countQuery = query with { ReturnGeometry = false };
        var request = ProtoConverters.ToProtoRequest(serviceId, layerId, countQuery);
        request.ReturnCountOnly = true;

        var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);
        var response = await _grpcClient.QueryFeaturesAsync(request, headers, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Count;
    }

    /// <inheritdoc />
    public async Task<Models.Extent?> GetExtentAsync(
        string serviceId,
        int layerId,
        Models.FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var extentQuery = query with { ReturnGeometry = false };
        var request = ProtoConverters.ToProtoRequest(serviceId, layerId, extentQuery);
        request.ReturnExtentOnly = true;

        var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);
        var response = await _grpcClient.QueryFeaturesAsync(request, headers, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.Extent != null
            ? ProtoConverters.FromProtoExtent(response.Extent)
            : null;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<long>> GetObjectIdsAsync(
        string serviceId,
        int layerId,
        Models.FeatureQuery query,
        CancellationToken cancellationToken = default)
    {
        var idsQuery = query with { ReturnGeometry = false };
        var request = ProtoConverters.ToProtoRequest(serviceId, layerId, idsQuery);
        request.ReturnIdsOnly = true;

        var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);
        var response = await _grpcClient.QueryFeaturesAsync(request, headers, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        return response.ObjectIds.ToList();
    }

    /// <inheritdoc />
    public async Task<Models.EditResult> ApplyEditsAsync(
        string serviceId,
        int layerId,
        Models.FeatureEditBatch edits,
        CancellationToken cancellationToken = default)
    {
        ValidateDisposed();
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceId);
        ArgumentNullException.ThrowIfNull(edits);

        try
        {
            _logger.LogDebug("Applying edits for service {ServiceId}, layer {LayerId}: {CreateCount} creates, {UpdateCount} updates, {DeleteCount} deletes",
                serviceId, layerId, edits.Creates.Count, edits.Updates.Count, edits.Deletes.Count);

            var request = new ApplyEditsRequest
            {
                ServiceId = serviceId,
                LayerId = layerId,
                RollbackOnFailure = edits.RollbackOnFailure,
                ForceWrite = edits.ForceWrite
            };

            // Add creates
            foreach (var feature in edits.Creates)
            {
                request.Adds.Add(ProtoConverters.ToProtoFeature(feature));
            }

            // Add updates
            foreach (var feature in edits.Updates)
            {
                request.Updates.Add(ProtoConverters.ToProtoFeature(feature));
            }

            // Add deletes
            request.Deletes.AddRange(edits.Deletes);

            var headers = await _auth.GetAuthHeadersAsync(cancellationToken).ConfigureAwait(false);
            var response = await _grpcClient.ApplyEditsAsync(request, headers, cancellationToken: cancellationToken)
                .ConfigureAwait(false);

            var result = ProtoConverters.FromProtoEditResponse(response);

            _logger.LogDebug("Applied edits for service {ServiceId}, layer {LayerId}: Success = {Success}",
                serviceId, layerId, result.IsSuccess);

            return result;
        }
        catch (RpcException ex)
        {
            _logger.LogError(ex, "gRPC error during edit operation for service {ServiceId}, layer {LayerId}: {Status}",
                serviceId, layerId, ex.Status);
            throw new HonuaClientException($"Edit operation failed: {ex.Status.Detail}", ex);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error during edit operation for service {ServiceId}, layer {LayerId}",
                serviceId, layerId);
            throw new HonuaClientException("Edit operation failed due to unexpected error", ex);
        }
    }

    /// <inheritdoc />
    public Task<Models.EditResult> CreateFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<Models.Feature> features,
        CancellationToken cancellationToken = default)
    {
        var batch = Models.FeatureEditBatch.CreateOnly(features);
        return ApplyEditsAsync(serviceId, layerId, batch, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Models.EditResult> UpdateFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<Models.Feature> features,
        CancellationToken cancellationToken = default)
    {
        var batch = Models.FeatureEditBatch.UpdateOnly(features);
        return ApplyEditsAsync(serviceId, layerId, batch, cancellationToken);
    }

    /// <inheritdoc />
    public Task<Models.EditResult> DeleteFeaturesAsync(
        string serviceId,
        int layerId,
        IReadOnlyList<long> objectIds,
        CancellationToken cancellationToken = default)
    {
        var batch = Models.FeatureEditBatch.DeleteOnly(objectIds);
        return ApplyEditsAsync(serviceId, layerId, batch, cancellationToken);
    }

    private void ValidateDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    /// <inheritdoc />
    public void Dispose()
    {
        if (!_disposed)
        {
            _channel?.Dispose();
            _disposed = true;
            _logger.LogDebug("HonuaFeatureClient disposed");
        }
    }
}

/// <summary>
/// Exception thrown when Honua client operations fail.
/// </summary>
public sealed class HonuaClientException : Exception
{
    public HonuaClientException(string message) : base(message) { }
    public HonuaClientException(string message, Exception innerException) : base(message, innerException) { }
}