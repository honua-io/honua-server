// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Grpc.Core;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Abstractions;
using Honua.Core.Features.Security.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Scene.Grpc;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Protocols.Grpc;

/// <summary>
/// SRV-OGC-013: gRPC elevation refuses a layer whose service does not enable Elevation.
/// </summary>
public sealed class ElevationAccessGuardTests
{
    [UnitTest]
    public async Task EnforceReadAccessAsync_ElevationDisabled_ThrowsNotFound()
    {
        var context = CreateCallContext(ServiceProtocols.ImageServer);

        var act = () => ElevationAccessGuard.EnforceReadAccessAsync(context, layerId: 1, CancellationToken.None);

        var exception = await act.Should().ThrowAsync<RpcException>();
        exception.Which.StatusCode.Should().Be(StatusCode.NotFound);
        exception.Which.Status.Detail.Should().Contain("not enabled");
    }

    [UnitTest]
    public async Task EnforceReadAccessAsync_ElevationEnabled_ReturnsTheResource()
    {
        var context = CreateCallContext(ServiceProtocols.Elevation);

        var resource = await ElevationAccessGuard.EnforceReadAccessAsync(context, layerId: 1, CancellationToken.None);

        resource.Metadata.Id.Should().Be("dem");
    }

    private static TestServerCallContext CreateCallContext(string protocol)
    {
        var provider = new TestMetadataV2GraphBuilder()
            .AddResource(
                "dem",
                "dem",
                MetadataV2ResourceType.RasterDataset,
                accessPolicy: new AccessPolicy { AllowAnonymous = true })
            .AddStorageBinding("dem-binding", "dem", "dem", storageLayerId: 1)
            .AddService("imagery", "imagery", protocols: [protocol])
            .AddPublication("dem-publication", "imagery", "dem", layerIndex: 1, isPrimary: true)
            .BuildProvider();
        var services = new ServiceCollection()
            .AddSingleton<IMetadataV2GraphProvider>(provider)
            .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
            .BuildServiceProvider();
        var httpContext = new DefaultHttpContext
        {
            RequestServices = services,
            User = new System.Security.Claims.ClaimsPrincipal(new System.Security.Claims.ClaimsIdentity())
        };
        var context = new TestServerCallContext();
        context.UserState["__HttpContext"] = httpContext;
        return context;
    }

    private sealed class TestServerCallContext : ServerCallContext
    {
        private readonly Metadata _responseTrailers = new();

        protected override string MethodCore => "/geospatial.v1.ElevationService/GetElevation";
        protected override string HostCore => "localhost";
        protected override string PeerCore => "127.0.0.1";
        protected override DateTime DeadlineCore => DateTime.UtcNow.AddMinutes(5);
        protected override Metadata RequestHeadersCore => new();
        protected override CancellationToken CancellationTokenCore => CancellationToken.None;
        protected override Metadata ResponseTrailersCore => _responseTrailers;
        protected override Status StatusCore { get; set; }
        protected override WriteOptions? WriteOptionsCore { get; set; }
        protected override AuthContext AuthContextCore => new(null, []);

        protected override ContextPropagationToken CreatePropagationTokenCore(ContextPropagationOptions? options) =>
            throw new NotSupportedException();

        protected override Task WriteResponseHeadersAsyncCore(Metadata responseHeaders) =>
            Task.CompletedTask;
    }
}
