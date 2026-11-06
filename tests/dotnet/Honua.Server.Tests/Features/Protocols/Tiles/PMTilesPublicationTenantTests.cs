// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Reflection;
using FluentAssertions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Validation;
using Honua.Server.Features.Protocols.Tiles.PMTilesProxy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.Tiles;

public sealed class PMTilesPublicationTenantTests
{
    [UnitTheory]
    [InlineData("GET", null, false)]
    [InlineData("GET", "tenant-a", false)]
    [InlineData("GET", "tenant-b", true)]
    [InlineData("HEAD", null, false)]
    [InlineData("HEAD", "tenant-a", false)]
    [InlineData("HEAD", "tenant-b", true)]
    public async Task Proxy_PrivatePublication_RefusesBeforeReadingBytes(string httpMethod, string? tenantId, bool allowed)
    {
        var graph = CreateGraph();
        var storage = Substitute.For<ICloudFileStorage>();
        var metadata = CreateMetadata();
        storage.GetMetadataAsync("artifact", Arg.Any<CancellationToken>()).Returns(metadata);
        using var conditionalDownload = new MemoryStream([1, 2, 3, 4]);
        using var fallbackDownload = new MemoryStream([1, 2, 3, 4]);
        storage.DownloadIfMatchAsync("artifact", "v1", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(conditionalDownload));
        storage.DownloadAsync("artifact", Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<Stream?>(fallbackDownload));
        var proxy = new PMTilesProxyService(storage, [], [], Options.Create(new CloudStorageOptions()));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns(tenantId);
        using var services = new ServiceCollection().AddLogging().AddValidationServices()
            .AddSingleton(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Method = httpMethod;
        context.Response.Body = new MemoryStream();
        var method = typeof(PMTilesProxyEndpoints).GetMethod("HandleProxy", BindingFlags.Static | BindingFlags.NonPublic)!;

        var result = await (Task<IResult>)method.Invoke(null,
            ["artifact", context, proxy, graph, NullLoggerFactory.Instance, CancellationToken.None])!;
        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(allowed ? StatusCodes.Status200OK : StatusCodes.Status404NotFound);
        var reads = storage.ReceivedCalls().Count(call => call.GetMethodInfo().Name is nameof(ICloudFileStorage.DownloadAsync) or nameof(ICloudFileStorage.DownloadIfMatchAsync));
        reads.Should().Be(allowed && httpMethod == "GET" ? 1 : 0);
        if (allowed)
        {
            context.Response.ContentLength.Should().Be(4);
        }
    }

    private static TestMetadataV2GraphProvider CreateGraph()
    {
        var graph = new TestMetadataV2GraphBuilder()
            .AddResource("resource", "resource", accessPolicy: new() { AllowAnonymous = true })
            .AddStorageBinding("binding", "resource", "resource", storageLayerId: 1)
            .AddService("maps", "maps", protocols: [ServiceProtocols.MapServer], accessPolicy: new() { AllowAnonymous = true })
            .AddPublication("publication", "maps", "resource", layerIndex: 0, storageBindingId: "binding")
            .Build();
        return new TestMetadataV2GraphProvider(graph with
        {
            Publications = graph.Publications.Select(publication => publication with
            {
                Metadata = publication.Metadata with { Tenant = "tenant-b" }
            }).ToArray()
        });
    }

    private static CloudFile CreateMetadata() => new()
    {
        FileId = "artifact",
        FileName = "artifact.pmtiles",
        StoragePath = "pmtiles/artifact",
        ContentType = "application/vnd.pmtiles",
        SizeBytes = 4,
        UploadedAt = DateTimeOffset.UnixEpoch,
        Provider = CloudStorageProvider.Local,
        ETag = "v1",
        Metadata = ImmutableDictionary<string, string>.Empty.Add("operation", "publish")
            .Add("layerId", "0").Add("serviceId", "maps").Add("publicationId", "publication")
            .Add("resourceId", "resource").Add("storageBindingId", "binding")
    };
}
