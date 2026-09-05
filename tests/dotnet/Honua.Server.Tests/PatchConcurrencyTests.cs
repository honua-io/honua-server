// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;

namespace Honua.Server.Tests;

[Collection("Database")]
public sealed class PatchConcurrencyTests
{
    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /odata/Features(LayerId={layerId},ObjectId={objectId})")]
    public Task ODataPatch_ConcurrentDisjointPatch_PreservesCommittedPropertiesAndGeometry(bool otherIsOData)
        => VerifyConcurrentPatchAsync(true, otherIsOData);

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Protocol(TestProtocols.OgcApiFeatures)]
    [Operation(Operations.Update)]
    [Endpoint("PATCH /ogc/features/collections/{collectionId}/items/{featureId}")]
    public Task OgcPatch_ConcurrentDisjointPatch_PreservesCommittedPropertiesAndGeometry(bool otherIsOData)
        => VerifyConcurrentPatchAsync(false, otherIsOData);

    [IntegrationTheory]
    [InlineData(true)]
    [InlineData(false)]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Update)]
    [Endpoint("POST /odata/$batch")]
    public Task ODataBatchPatch_ConcurrentDisjointPatch_PreservesCommittedPropertiesAndGeometry(bool otherIsOData)
        => VerifyConcurrentPatchAsync(true, otherIsOData, firstIsBatch: true);

    [IntegrationTheory]
    [InlineData(false)]
    [InlineData(true)]
    [Protocol(TestProtocols.ODataV4)]
    [Operation(Operations.Update)]
    public Task ODataPatch_IfNoneMatchBecomesFalse_ReturnsPreconditionFailed(bool batch)
        => VerifyConcurrentPatchAsync(true, true, firstIsBatch: batch, withIfNoneMatch: true);

    [IntegrationTheory]
    [InlineData("odata", false)]
    [InlineData("ogc", false)]
    [InlineData("batch", false)]
    [InlineData("odata", true)]
    [InlineData("ogc", true)]
    [InlineData("batch", true)]
    [Operation(Operations.Update)]
    public async Task Patch_MaskedAttribute_PreservesStoredValueWithoutExposingIt(string protocol, bool concurrent)
    {
        var barrier = new WriteBarrier();
        var mask = new MutableFieldMask();
        var fixture = CreateFixture(barrier, mask);
        await fixture.InitializeAsync();
        try
        {
            var id = await fixture.InsertFeatureAsync(0, "original name");
            var original = (await fixture.GetService<IFeatureReader>().GetAsync(0, id))!.Value;
            await fixture.GetService<IFeatureWriter>().UpdateAsync(0, original with { Attributes = original.Attributes.SetItem("population", 12345L) });
            mask.Fields = ImmutableArray.Create("POPULATION");
            Assert.False((await fixture.GetService<IFeatureReader>().GetAsync(0, id))!.Value.Attributes.ContainsKey("population"));
            if (!concurrent) barrier.Resume.TrySetResult();
            using var request = CreatePatch(protocol != "ogc", id, changeName: true, batch: protocol == "batch");
            var firstTask = fixture.Client.SendAsync(request);
            try
            {
                if (concurrent)
                {
                    await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                    using var second = new HttpRequestMessage(HttpMethod.Patch, $"/odata/Features(LayerId=0,ObjectId={id})")
                    {
                        Content = new StringContent("""{"population":45678}""", Encoding.UTF8, "application/json")
                    };
                    using var secondResponse = await fixture.Client.SendAsync(second);
                    Assert.True(secondResponse.IsSuccessStatusCode, await secondResponse.Content.ReadAsStringAsync());
                }
            }
            finally
            {
                barrier.Resume.TrySetResult();
            }
            using var response = await firstTask;
            Assert.InRange((int)await ReadStatusAsync(response, protocol == "batch"), 200, 299);
            Assert.DoesNotContain("population", await response.Content.ReadAsStringAsync(), StringComparison.OrdinalIgnoreCase);
            mask.Fields = ImmutableArray<string>.Empty;
            var stored = (await fixture.GetService<IFeatureReader>().GetAsync(0, id))!.Value;
            Assert.Equal(concurrent ? 45678L : 12345L, Convert.ToInt64(stored.Attributes["population"], CultureInfo.InvariantCulture));
            Assert.Equal("changed name", stored.Attributes["name"]);
            Assert.Equal(original.Geometry, stored.Geometry);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await fixture.DisposeAsync();
        }
    }

    [IntegrationTest]
    public async Task PatchEndpoints_DeclareConflictAndPreconditionResponses()
    {
        var fixture = CreateFixture(new WriteBarrier());
        await fixture.InitializeAsync();
        try
        {
            var names = new[] { "ODataUpdateFeature", "ODataUpdateLayerFeature", "ODataUpdateFeatureLegacy", "PatchItem" };
            var endpoints = fixture.GetService<EndpointDataSource>().Endpoints;
            foreach (var name in names)
            {
                var endpoint = Assert.Single(endpoints.Where(e => e.Metadata.GetMetadata<IEndpointNameMetadata>()?.EndpointName == name));
                foreach (var status in new[] { 409, 412 })
                {
                    var response = Assert.Single(endpoint.Metadata.GetOrderedMetadata<IProducesResponseTypeMetadata>().Where(m => m.StatusCode == status));
                    Assert.Equal(name == "PatchItem" ? typeof(Microsoft.AspNetCore.Mvc.ProblemDetails) : typeof(Honua.Protocols.OData.Features.Models.ODataError), response.Type);
                }
            }
        }
        finally
        {
            await fixture.DisposeAsync();
        }
    }

    private static WebAppFixture CreateFixture(WriteBarrier barrier, MutableFieldMask? mask = null)
    {
        return new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .UseSeed(Path.Join("tests", "seed", "odata.yaml"))
            .ConfigureServices(services =>
            {
                if (mask is not null)
                {
                    services.AddSingleton<IFieldMaskSource>(mask);
                }
                var registration = services.Last(service => service.ServiceType == typeof(IFeatureWriter));
                services.Remove(registration);
                services.Add(new ServiceDescriptor(typeof(IFeatureWriter), provider =>
                {
                    var inner = (IFeatureWriter)(registration.ImplementationInstance
                        ?? registration.ImplementationFactory?.Invoke(provider)
                        ?? ActivatorUtilities.CreateInstance(provider, registration.ImplementationType!));
                    return new InterleavedWriter(inner, barrier);
                }, registration.Lifetime));
            });
    }

    private sealed class MutableFieldMask : IFieldMaskSource
    {
        public ImmutableArray<string> Fields { get; set; } = ImmutableArray<string>.Empty;
        public Task<ImmutableArray<string>> ResolveAsync(MetadataV2Resource resource, CancellationToken cancellationToken = default)
            => Task.FromResult(Fields);
    }

    private static async Task VerifyConcurrentPatchAsync(bool firstIsOData, bool otherIsOData, bool firstIsBatch = false, bool withIfNoneMatch = false)
    {
        var barrier = new WriteBarrier();
        var fixture = CreateFixture(barrier);
        await fixture.InitializeAsync();
        try
        {
            var objectId = await fixture.InsertFeatureAsync(0, "original name");
            var original = (await fixture.GetService<IFeatureReader>().GetAsync(0, objectId))!.Value;
            string? ifNoneMatch = null;
            if (withIfNoneMatch)
            {
                using var futureRequest = CreatePatch(true, objectId, changeName: false);
                using var futureResponse = await fixture.Client.SendAsync(futureRequest);
                Assert.True(futureResponse.IsSuccessStatusCode);
                using var futureRead = await fixture.Client.GetAsync($"/odata/Features(LayerId=0,ObjectId={objectId})");
                ifNoneMatch = futureRead.Headers.ETag!.ToString();
                await fixture.GetService<IFeatureWriter>().UpdateAsync(0, original);
            }
            using var firstRequest = CreatePatch(firstIsOData, objectId, changeName: true, batch: firstIsBatch, ifNoneMatch: ifNoneMatch);
            var firstTask = fixture.Client.SendAsync(firstRequest);
            Feature committed;
            try
            {
                await barrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(30));
                using var secondRequest = CreatePatch(otherIsOData, objectId, changeName: false);
                using var secondResponse = await fixture.Client.SendAsync(secondRequest);
                Assert.True(secondResponse.IsSuccessStatusCode, await secondResponse.Content.ReadAsStringAsync());
                committed = (await fixture.GetService<IFeatureReader>().GetAsync(0, objectId))!.Value;
                Assert.Equal(12345L, Convert.ToInt64(committed.Attributes["population"], CultureInfo.InvariantCulture));
                Assert.NotEqual(original.Geometry, committed.Geometry);
            }
            finally
            {
                barrier.Resume.TrySetResult();
            }

            using var firstResponse = await firstTask;
            var current = (await fixture.GetService<IFeatureReader>().GetAsync(0, objectId))!.Value;
            Assert.True(current.Attributes.TryGetValue("population", out var population));
            Assert.Equal(committed.Attributes["population"], population);
            Assert.Equal(committed.Geometry, current.Geometry);
            Assert.Equal(withIfNoneMatch ? HttpStatusCode.PreconditionFailed : HttpStatusCode.Conflict, await ReadStatusAsync(firstResponse, firstIsBatch));
            Assert.Equal("original name", current.Attributes["name"]);

            // A client can retry the rejected partial edit against the current state.
            using var retry = CreatePatch(firstIsOData, objectId, changeName: true, batch: firstIsBatch);
            using var retryResponse = await fixture.Client.SendAsync(retry);
            var retryStatus = await ReadStatusAsync(retryResponse, firstIsBatch);
            Assert.InRange((int)retryStatus, 200, 299);
            var retried = (await fixture.GetService<IFeatureReader>().GetAsync(0, objectId))!.Value;
            Assert.Equal("changed name", retried.Attributes["name"]);
            Assert.Equal(committed.Attributes["population"], retried.Attributes["population"]);
            Assert.Equal(committed.Geometry, retried.Geometry);
        }
        finally
        {
            barrier.Resume.TrySetResult();
            await fixture.DisposeAsync();
        }
    }

    private static HttpRequestMessage CreatePatch(bool odata, long objectId, bool changeName, bool batch = false, string? ifNoneMatch = null)
    {
        var payload = (odata, changeName) switch
        {
            (true, true) => """{"name":"changed name"}""",
            (true, false) => """{"population":12345,"Geometry":{"type":"Point","coordinates":[-120,35]}}""",
            (false, true) => """{"properties":{"name":"changed name"}}""",
            _ => """{"properties":{"population":12345},"geometry":{"type":"Point","coordinates":[-120,35]}}"""
        };
        if (batch)
        {
            var condition = ifNoneMatch is null ? string.Empty : ",\"If-None-Match\":" + JsonSerializer.Serialize(ifNoneMatch);
            var body = $$"""{"requests":[{"id":"patch","atomicityGroup":"changes","method":"PATCH","url":"Features(LayerId=0,ObjectId={{objectId}})","headers":{"Content-Type":"application/json"{{condition}}},"body":{{payload}}}]}""";
            return new HttpRequestMessage(HttpMethod.Post, "/odata/$batch")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json")
            };
        }

        var request = new HttpRequestMessage(HttpMethod.Patch, odata
            ? $"/odata/Features(LayerId=0,ObjectId={objectId})"
            : $"/ogc/features/collections/0/items/{objectId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, odata ? "application/json" : "application/merge-patch+json")
        };
        if (ifNoneMatch is not null)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        }
        return request;
    }

    private static async Task<HttpStatusCode> ReadStatusAsync(HttpResponseMessage response, bool batch)
    {
        if (!batch)
        {
            return response.StatusCode;
        }

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var responses = document.RootElement.GetProperty("responses");
        Assert.Equal(1, responses.GetArrayLength());
        return (HttpStatusCode)responses[0].GetProperty("status").GetInt32();
    }

    private sealed class WriteBarrier
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Claimed;
    }

    private sealed class InterleavedWriter(IFeatureWriter inner, WriteBarrier barrier) : IFeatureWriter
    {
        public Task<Feature> CreateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.CreateAsync(layerId, feature, cancellationToken);

        public Task<Feature> UpdateAsync(int layerId, Feature feature, CancellationToken cancellationToken = default)
            => inner.UpdateAsync(layerId, feature, cancellationToken);

        public Task<bool> DeleteAsync(int layerId, long featureId, CancellationToken cancellationToken = default)
            => inner.DeleteAsync(layerId, featureId, cancellationToken);

        public async Task<FeatureEditResult> ApplyEditsAsync(int layerId, FeatureEditBatch editBatch, CancellationToken cancellationToken = default)
        {
            if (editBatch.Updates.Any(feature => feature.Attributes.TryGetValue("name", out var name) && Equals(name, "changed name"))
                && Interlocked.CompareExchange(ref barrier.Claimed, 1, 0) == 0)
            {
                barrier.Reached.TrySetResult();
                await barrier.Resume.Task.WaitAsync(TimeSpan.FromSeconds(30), cancellationToken);
            }

            return await inner.ApplyEditsAsync(layerId, editBatch, cancellationToken);
        }
    }
}
