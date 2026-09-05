// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Text;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.Extensions.DependencyInjection;

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

    private static async Task VerifyConcurrentPatchAsync(bool firstIsOData, bool otherIsOData)
    {
        var barrier = new WriteBarrier();
        var fixture = new WebAppFixture().WithTestLicense(HonuaEdition.Pro)
            .UseSeed(Path.Join("tests", "seed", "odata.yaml"))
            .ConfigureServices(services =>
            {
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
        await fixture.InitializeAsync();
        try
        {
            var objectId = await fixture.InsertFeatureAsync(0, "original name");
            var original = (await fixture.GetService<IFeatureReader>().GetAsync(0, objectId))!.Value;
            using var firstRequest = CreatePatch(firstIsOData, objectId, changeName: true);
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
            Assert.Equal(HttpStatusCode.Conflict, firstResponse.StatusCode);
            Assert.Equal("original name", current.Attributes["name"]);

            // A client can retry the rejected partial edit against the current state.
            using var retry = CreatePatch(firstIsOData, objectId, changeName: true);
            using var retryResponse = await fixture.Client.SendAsync(retry);
            Assert.True(retryResponse.IsSuccessStatusCode, await retryResponse.Content.ReadAsStringAsync());
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

    private static HttpRequestMessage CreatePatch(bool odata, long objectId, bool changeName)
    {
        var payload = (odata, changeName) switch
        {
            (true, true) => """{"name":"changed name"}""",
            (true, false) => """{"population":12345,"Geometry":{"type":"Point","coordinates":[-120,35]}}""",
            (false, true) => """{"properties":{"name":"changed name"}}""",
            _ => """{"properties":{"population":12345},"geometry":{"type":"Point","coordinates":[-120,35]}}"""
        };
        return new HttpRequestMessage(HttpMethod.Patch, odata
            ? $"/odata/Features(LayerId=0,ObjectId={objectId})"
            : $"/ogc/features/collections/0/items/{objectId}")
        {
            Content = new StringContent(payload, Encoding.UTF8, odata ? "application/json" : "application/merge-patch+json")
        };
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
