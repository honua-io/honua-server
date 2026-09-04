// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.FeatureStore.Services;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Security.Domain;
using Honua.Protocols.OData.Services;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using NSubstitute;

namespace Honua.Server.Tests.Features.Protocols.OData;

[Collection("Database")]
[Protocol(TestProtocols.ODataV4)]
public sealed class ODataBoundProviderEndpointTests : IAsyncLifetime
{
    private readonly IFeatureReader _managedReader = Substitute.For<IFeatureReader>();
    private readonly IFeatureWriter _managedWriter = Substitute.For<IFeatureWriter>();
    private readonly IFeatureReader _boundReader = Substitute.For<IFeatureReader>();
    private readonly WebAppFixture _fixture;

    public ODataBoundProviderEndpointTests()
    {
        var connectionId = Guid.NewGuid();
        var (snapshot, _, _, _) = ODataFeatureProviderResolverTests.CreateSnapshot(connectionId.ToString());
        var graph = snapshot.Graph with
        {
            Services = snapshot.Graph.Services.Select(service => service with
            {
                Status = service.Status with { Lifecycle = MetadataV2LifecycleStatus.Active },
                AccessPolicy = new AccessPolicy { AllowAnonymous = true }
            }).ToArray(),
            Publications = snapshot.Graph.Publications.Select(publication => publication with
            {
                Status = publication.Status with { Lifecycle = MetadataV2LifecycleStatus.Active },
                PublicationType = MetadataV2PublicationType.ODataEntitySet
            }).ToArray(),
            StorageBindings = snapshot.Graph.StorageBindings.Select(binding => binding with
            {
                Status = binding.Status with { Lifecycle = MetadataV2LifecycleStatus.Active }
            }).ToArray(),
            Resources = snapshot.Graph.Resources.Select(resource => resource with
            {
                Status = resource.Status with { Lifecycle = MetadataV2LifecycleStatus.Active },
                AccessPolicy = new AccessPolicy { AllowAnonymous = true },
                SchemaFields = [.. resource.SchemaFields, new MetadataV2Field { Name = "name", Type = MetadataV2FieldType.String }]
            }).ToArray()
        };
        var graphProvider = Substitute.For<IMetadataV2GraphProvider>();
        graphProvider.GetCurrentAsync(Arg.Any<CancellationToken>())
            .Returns(new MetadataV2GraphSnapshot(graph, "bound-test", DateTimeOffset.UtcNow));
        var provider = Substitute.For<IFeatureDataProvider, IBindableFeatureDataProvider>();
        provider.ProviderName.Returns(DataProviderNames.Postgis);
        provider.Capabilities.Returns(FeatureProviderCapabilities.ReadWritePostgis);
        provider.Reader.Returns(_managedReader);
        provider.Writer.Returns(_managedWriter);
        ((IBindableFeatureDataProvider)provider).CreateReaderForBinding(Arg.Any<FeatureProviderBinding>())
            .Returns(_boundReader);
        var feature = Feature.Create(42, null, ImmutableDictionary<string, object?>.Empty.Add("name", "Harbor"));
        _boundReader.QueryAsync(41, Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Create(1, [feature]));
        _boundReader.GetAsync(41, 42, Arg.Any<CancellationToken>()).Returns(feature);
        _managedReader.QueryAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(QueryResult<Feature>.Empty());
        var stream = Substitute.For<IStreamingFeatureStore>();
        stream.StreamFeaturesAsync(Arg.Any<int>(), Arg.Any<FeatureQuery>(), Arg.Any<CancellationToken>())
            .Returns(EmptyFeatures());
        var resolver = new ODataFeatureProviderResolver(
            _managedReader, _managedWriter, ODataFeatureProviderResolverTests.CreateRouter(connectionId, provider));
        _fixture = new WebAppFixture()
            .ReplaceService(graphProvider)
            .ReplaceService(_managedReader)
            .ReplaceService(_managedWriter)
            .ReplaceService(stream)
            .ReplaceService(resolver);
    }

    public Task InitializeAsync() => _fixture.InitializeAsync();
    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTheory]
    [InlineData("/odata/Layers(4)/Features?$search=Harbor")]
    [InlineData("/odata/Features(4)/$search?$search=Harbor")]
    [InlineData("/odata/Features(LayerId=4,ObjectId=42)")]
    [InlineData("/odata/Layers(4)/Features(42)")]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task Read_ConnectionBoundPublication_UsesBoundReader(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        (await response.Content.ReadAsStringAsync()).Should().Contain("Harbor");
        await _managedReader.DidNotReceiveWithAnyArgs().QueryAsync(default, default, default);
        await _managedReader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default);
    }

    [IntegrationTheory]
    [InlineData("/odata/Layers(4)/Features?$apply=aggregate($count%20as%20n)")]
    [InlineData("/odata/Features(4)/$apply?$apply=aggregate($count%20as%20n)")]
    [Operation(Operations.Query)]
    [Endpoint("GET /odata/Layers({layerId})/Features")]
    public async Task Apply_ConnectionBoundPublication_CountsBoundRows(string path)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("value")[0].GetProperty("n").GetInt64().Should().Be(1);
        await _boundReader.Received().QueryAsync(41,
            Arg.Is<FeatureQuery>(query => query.Limit == 10001), Arg.Any<CancellationToken>());
    }

    [IntegrationTheory]
    [InlineData("POST", "/odata/Layers(4)/Features")]
    [InlineData("PATCH", "/odata/Features(LayerId=4,ObjectId=42)")]
    [InlineData("PUT", "/odata/Features(LayerId=4,ObjectId=42)")]
    [InlineData("DELETE", "/odata/Features(LayerId=4,ObjectId=42)")]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public async Task Write_ConnectionBoundPublication_RejectsBeforeManagedReadOrWrite(string method, string path)
    {
        using var request = new HttpRequestMessage(new HttpMethod(method), path);
        if (method != "DELETE")
        {
            request.Content = JsonContent.Create(new { name = "Changed" });
        }
        var response = await _fixture.Client.SendAsync(request);
        response.StatusCode.Should().Be(HttpStatusCode.NotImplemented, await response.Content.ReadAsStringAsync());
        await _managedReader.DidNotReceiveWithAnyArgs().GetAsync(default, default, default);
        await _managedWriter.DidNotReceiveWithAnyArgs().ApplyEditsAsync(default, default, default);
    }

    [IntegrationTest]
    [Operation(Operations.ODataBatch)]
    [Endpoint("POST /odata/$batch")]
    public async Task Batch_ConnectionBoundWrite_RejectsBeforeShadowMutation()
    {
        var response = await _fixture.Client.PostAsJsonAsync("/odata/$batch", new
        {
            requests = new[]
            {
                new { id = "1", method = "POST", url = "/odata/Layers(4)/Features",
                    atomicityGroup = "changes", headers = new Dictionary<string, string> { ["Content-Type"] = "application/json" },
                    body = new { name = "Changed" } }
            }
        });
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        json.RootElement.GetProperty("responses")[0].GetProperty("status").GetInt32().Should().Be(501);
        await _managedWriter.DidNotReceiveWithAnyArgs().ApplyEditsAsync(default, default, default);
    }

    private static async IAsyncEnumerable<Feature> EmptyFeatures()
    {
        await Task.CompletedTask;
        yield break;
    }
}
