// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Runtime.CompilerServices;
using Grpc.Core;
using Grpc.Net.Client;
using Honua.Sdk.Grpc.Conversion;
using Microsoft.Extensions.Options;

namespace Honua.Sdk.Grpc;

/// <summary>
/// gRPC client for the Honua FeatureService.
/// </summary>
public sealed class HonuaGrpcClient : IHonuaGrpcClient, IDisposable
{
    private readonly Honua.Server.Features.Grpc.Proto.FeatureService.FeatureServiceClient _client;
    private readonly GrpcChannel? _ownedChannel;
    private readonly Metadata _metadata;

    /// <summary>
    /// Creates a new gRPC client using the provided options.
    /// </summary>
    /// <param name="options">Configuration options for the client.</param>
    public HonuaGrpcClient(IOptions<HonuaGrpcClientOptions> options)
    {
        var opts = options.Value;
        _ownedChannel = GrpcChannel.ForAddress(opts.Address);
        _client = new Honua.Server.Features.Grpc.Proto.FeatureService.FeatureServiceClient(_ownedChannel);
        _metadata = BuildMetadata(opts);
    }

    /// <summary>
    /// Creates a new gRPC client using a pre-configured channel.
    /// </summary>
    /// <param name="channel">The gRPC channel to use.</param>
    /// <param name="options">Optional client options for authentication.</param>
    public HonuaGrpcClient(GrpcChannel channel, HonuaGrpcClientOptions? options = null)
    {
        _client = new Honua.Server.Features.Grpc.Proto.FeatureService.FeatureServiceClient(channel);
        _metadata = BuildMetadata(options ?? new HonuaGrpcClientOptions());
    }

    // For testing - inject the generated client stub directly
    internal HonuaGrpcClient(Honua.Server.Features.Grpc.Proto.FeatureService.FeatureServiceClient client, Metadata? metadata = null)
    {
        _client = client;
        _metadata = metadata ?? new Metadata();
    }

    /// <inheritdoc />
    public async Task<Models.QueryFeaturesResponse> QueryFeaturesAsync(
        Models.QueryFeaturesRequest request, CancellationToken ct = default)
    {
        var protoRequest = ProtoAdapter.ToProtoRequest(request);
        try
        {
            var protoResponse = await _client.QueryFeaturesAsync(protoRequest, _metadata, cancellationToken: ct);
            return ProtoAdapter.FromProtoResponse(protoResponse);
        }
        catch (RpcException ex)
        {
            throw new HonuaGrpcException(ex.StatusCode, ex.Status.Detail, ex);
        }
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<Models.FeaturePage> QueryFeaturesStreamAsync(
        Models.QueryFeaturesRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var protoRequest = ProtoAdapter.ToProtoRequest(request);
        var call = _client.QueryFeaturesStream(protoRequest, _metadata, cancellationToken: ct);
        try
        {
            await foreach (var protoPage in call.ResponseStream.ReadAllAsync(ct))
            {
                var page = ProtoAdapter.FromProtoPage(protoPage);
                yield return page;
                if (protoPage.IsLastPage)
                    yield break;
            }
        }
        finally
        {
            call.Dispose();
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        _ownedChannel?.Dispose();
    }

    private static Metadata BuildMetadata(HonuaGrpcClientOptions opts)
    {
        var metadata = new Metadata();
        if (!string.IsNullOrEmpty(opts.ApiKey))
            metadata.Add("x-api-key", opts.ApiKey);
        if (!string.IsNullOrEmpty(opts.BearerToken))
            metadata.Add("authorization", $"Bearer {opts.BearerToken}");
        if (opts.EnableCompressionNegotiation && !string.IsNullOrWhiteSpace(opts.AcceptedCompressionEncodings))
            metadata.Add("grpc-accept-encoding", opts.AcceptedCompressionEncodings);
        return metadata;
    }
}
