// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.FeatureStore.Abstractions;
using Honua.Core.Features.FeatureStore.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Db.Postgres.Features.Geoprocessing;
using Honua.Geoprocessing;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.ControlPlane;
using Honua.Geoprocessing.Execution;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using NetTopologySuite.IO;
using NSubstitute;

namespace Honua.Server.Tests.Features.Geoprocessing.Execution;

[Collection("Database")]
[Trait("Category", "LayerExecutionProof")]
public sealed class LayerSourceExecutionProofTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new WebAppFixture().ConfigureServices(_ => { });
    private long _selectedId;

    public async Task InitializeAsync()
    {
        await _fixture.InitializeAsync();
        var writer = _fixture.GetService<IFeatureWriter>();
        writer.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        foreach (var (name, category, x, y) in new[] { ("proof-selected", "proof", 12d, 34d),
            ("proof-wrong-category", "excluded", 13d, 35d), ("proof-outside", "proof", 60d, 10d) })
        {
            var feature = await writer.CreateAsync(0, Feature.Create(0,
                new WKBWriter().Write(factory.CreatePoint(new Coordinate(x, y))),
                ImmutableDictionary<string, object?>.Empty.Add("name", name).Add("category", category)
                    .Add("description", "private-field-must-not-appear")));
            if (name == "proof-selected")
            {
                _selectedId = feature.Id;
            }
        }
    }

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    public async Task FeatureProject_RealCatalogLayer_PublishesAnalyticalMercatorCoordinatesAndSrid()
    {
        using var provider = SourceServices();
        var executor = new LayerFeatureProjectExecutor(provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<LayerFeatureProjectExecutor>.Instance);
        using var output = await Execute(executor, "conversion.feature-project", ("layerId", "0"),
            ("where", "name = 'proof-selected'"), ("targetSrid", "3857"));
        AssertSelected(output.RootElement, fieldsRestricted: false);
        output.RootElement.GetProperty("srid").GetInt32().Should().Be(3857);
        // Read the source through the production store; projection must not persist there.
        var source = await _fixture.GetService<IFeatureReader>().QueryAsync(0, new FeatureQuery { Where = "name = 'proof-selected'" });
        var geometry = new WKBReader().Read(source.Items.Single().Geometry!);
        geometry.Coordinate.X.Should().Be(12);
        geometry.Coordinate.Y.Should().Be(34);
    }

    [IntegrationTest]
    public async Task HonuaLayerSource_RealCatalogFilterBboxAndFields_PublishesExactProjectedSelection()
    {
        using var provider = SourceServices();
        var executor = RemoteSourceExecutor.ForProcess("source.honua-layer", provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<RemoteSourceExecutor>.Instance);
        using var output = await Execute(executor, "source.honua-layer", ("layerId", "0"),
            ("where", "category = 'proof'"), ("bbox", "1000000,3000000,2000000,5000000"),
            ("outFields", "objectid,name,category"), ("outSrid", "3857"));
        AssertSelected(output.RootElement, fieldsRestricted: true);
        output.RootElement.GetProperty("srid").GetInt32().Should().Be(3857);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task HonuaLayerSource_AdvertisedCrsDiffersFromStorage_UsesCanonicalStoragePrecedence(bool bindingOverridesResource)
    {
        var store = _fixture.GetService<IMetadataV2GraphStore>();
        var snapshot = await store.GetCurrentAsync();
        var original = snapshot.Index.ResourcesByStorageLayerId[0];
        var binding = snapshot.Graph.StorageBindings.Single(b => b.StorageLayerId == 0 && b.ResourceId == original.Metadata.Id);
        var options = binding.Options.ToDictionary(kv => kv.Key, kv => kv.Value);
        options.Remove("srid");
        options.Remove("storageSrid");
        if (bindingOverridesResource)
        {
            options["storageSrid"] = JsonSerializer.SerializeToElement(4326);
        }
        var changed = original with
        {
            Spatial = original.Spatial! with
            {
                SpatialReference = new MetadataV2SpatialReference { Srid = 3857 },
                StorageCrs = new MetadataV2SpatialReference { Srid = bindingOverridesResource ? 32610 : 4326 }
            }
        };
        await store.SaveAsync(snapshot.Graph with
        {
            Resources = snapshot.Graph.Resources.Select(r => r.Metadata.Id == original.Metadata.Id ? changed : r).ToArray(),
            StorageBindings = snapshot.Graph.StorageBindings.Select(b => b.Metadata.Id == binding.Metadata.Id ? b with { Options = options } : b).ToArray()
        }, snapshot.Etag);
        using var provider = SourceServices();
        var executor = RemoteSourceExecutor.ForProcess("source.honua-layer", provider.GetRequiredService<IServiceScopeFactory>(),
            ExecutorOptions(), NullLogger<RemoteSourceExecutor>.Instance);
        using var output = await Execute(executor, "source.honua-layer", ("layerId", "0"),
            ("where", "category = 'proof'"), ("bbox", "1000000,3000000,2000000,5000000"),
            ("outFields", "objectid,name,category"), ("outSrid", "3857"));
        AssertSelected(output.RootElement, fieldsRestricted: true);
        output.RootElement.GetProperty("srid").GetInt32().Should().Be(3857);
    }

    private void AssertSelected(JsonElement output, bool fieldsRestricted)
    {
        output.GetProperty("featureCount").GetInt32().Should().Be(1);
        var feature = output.GetProperty("features").EnumerateArray().Should().ContainSingle().Which;
        var attributes = feature.GetProperty("properties");
        attributes.GetProperty("objectid").GetInt64().Should().Be(_selectedId);
        attributes.GetProperty("name").GetString().Should().Be("proof-selected");
        attributes.GetProperty("category").GetString().Should().Be("proof");
        if (fieldsRestricted)
        {
            attributes.EnumerateObject().Select(p => p.Name).Should().BeEquivalentTo("objectid", "name", "category");
        }
        else
        {
            attributes.GetProperty("description").GetString().Should().Be("private-field-must-not-appear");
        }
        var coordinates = feature.GetProperty("geometry").GetProperty("coordinates");
        const double radius = 6378137;
        coordinates[0].GetDouble().Should().BeApproximately(radius * 12 * Math.PI / 180, 0.001);
        coordinates[1].GetDouble().Should().BeApproximately(radius * Math.Log(Math.Tan(Math.PI / 4 + 34 * Math.PI / 360)), 0.001);
    }

    private ServiceProvider SourceServices()
    {
        var store = _fixture.GetService<IStreamingFeatureStore>();
        store.GetType().Assembly.GetName().Name.Should().Be("Honua.Postgres");
        return new ServiceCollection().AddSingleton<IDagFeatureSource>(new HonuaLayerDagSource(store, _fixture.GetService<IMetadataV2GraphProvider>())).BuildServiceProvider();
    }

    private static IOptionsMonitor<GeoprocessingExecutorOptions> ExecutorOptions()
    {
        var options = Substitute.For<IOptionsMonitor<GeoprocessingExecutorOptions>>();
        options.CurrentValue.Returns(new GeoprocessingExecutorOptions());
        return options;
    }

    private static async Task<JsonDocument> Execute(IProcessExecutor executor, string id, params (string Key, string Value)[] inputs)
    {
        var parameters = new Dictionary<string, string> { [ExecutionJobParameterKeys.GeoprocessingProcessDefinitions] = id, ["protocolProcessId"] = id };
        foreach (var (key, value) in inputs)
        {
            parameters[ExecutionJobParameterKeys.GeoprocessingStepInputPrefix + "0." + key] = value;
        }
        var job = new ExecutionJobRecord
        {
            OperationId = Guid.NewGuid().ToString("N"),
            Status = ExecutionJobStatus.Running,
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow,
            Spec = new ExecutionJobSpec
            {
                Kind = ExecutionJobKind.Geoprocessing,
                TargetKind = BatchComputeTargetKind.KubernetesJob,
                Backend = "local",
                WorkloadName = "geoprocessing:" + id,
                Parameters = parameters
            }
        };
        var artifacts = new List<string>();
        var context = Substitute.For<IJobExecutionContext>();
        context.When(c => c.PublishArtifactAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())).Do(c => artifacts.Add(c.ArgAt<string>(0)));
        var result = await executor.ExecuteAsync(job, context, CancellationToken.None);
        result.Status.Should().Be(ExecutionJobStatus.Succeeded, result.ErrorMessage);
        artifacts.Should().ContainSingle();
        return JsonDocument.Parse(Convert.FromBase64String(artifacts[0][(artifacts[0].IndexOf(',') + 1)..]));
    }
}
