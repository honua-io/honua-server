// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Spec;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Ai.Grounding.Spec;
using Honua.TestKit.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Grounding.Spec;

internal static class SpecGroundingTestSupport
{
    public static MetadataV2Resource CreateLayer(
        int id,
        string name,
        string? description = null,
        params MetadataV2Field[] fields)
    {
        var layerFields = fields.Length > 0
            ? fields
            : DefaultFields;

        var bindingId = $"binding-{id}";
        return new MetadataV2Resource
        {
            Metadata = new MetadataV2ObjectMetadata
            {
                Id = $"layer-{id}",
                Name = name,
                Description = description
            },
            Type = MetadataV2ResourceType.FeatureDataset,
            StorageBindingIds = [bindingId],
            SchemaFields = layerFields,
            Spatial = new MetadataV2ResourceSpatial
            {
                SpatialReference = MetadataV2SpatialReference.Wgs84,
                GeometryType = MetadataV2GeometryType.Point
            }
        };
    }

    public static MetadataV2Field Field(
        string name,
        MetadataV2FieldType type,
        int? length = null,
        bool nullable = true)
        => new()
        {
            Name = name,
            Type = type,
            Length = length,
            Nullable = nullable
        };

    public static IMetadataV2GraphProvider CreateGraphProvider(params MetadataV2Resource[] layers)
        => new TestMetadataV2GraphProvider(CreateGraph(layers));

    public static JsonElement ParseJsonElement(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    public static SpecDocument CreateEmptySpecDocument()
        => new(
            SourceSpan.Synthetic,
            SpecGrammarVersion.Current,
            SourceSpan.Synthetic,
            "analysis",
            null,
            ImmutableArray<Honua.Core.Features.Spec.Domain.SourceBinding>.Empty,
            ImmutableArray<ScopeClause>.Empty,
            ImmutableArray<ComputeStep>.Empty,
            null,
            ImmutableArray<OutputBinding>.Empty,
            ImmutableDictionary<string, string>.Empty);

    public static string BuildRoundTripTurn(SpecSummary summary)
        => string.Join(" ", summary.Sections.Select(section => section.Text));

    private static readonly MetadataV2Field[] DefaultFields =
    [
        Field("objectid", MetadataV2FieldType.Integer, nullable: false),
        Field("name", MetadataV2FieldType.String, length: 128),
        Field("category", MetadataV2FieldType.String, length: 64)
    ];

    private static MetadataV2Graph CreateGraph(IReadOnlyList<MetadataV2Resource> layers)
    {
        var bindings = layers.Select(layer =>
        {
            var layerId = ParseLayerId(layer);
            var bindingId = layer.StorageBindingIds[0];
            return new MetadataV2StorageBinding
            {
                Metadata = new MetadataV2ObjectMetadata
                {
                    Id = bindingId,
                    Name = bindingId
                },
                ResourceId = layer.Metadata.Id,
                StorageType = MetadataV2StorageType.RelationalTable,
                Locator = layer.Metadata.Name,
                StorageLayerId = layerId
            };
        }).ToArray();

        return new MetadataV2Graph
        {
            Revision = 1,
            Environment = "test",
            GeneratedAt = DateTimeOffset.UtcNow,
            Resources = layers.ToArray(),
            StorageBindings = bindings,
            Services =
            [
                new MetadataV2Service
                {
                    Metadata = new MetadataV2ObjectMetadata
                    {
                        Id = "grounding-service",
                        Name = "grounding",
                        Description = "Grounding test service"
                    }
                }
            ]
        };
    }

    private static int ParseLayerId(MetadataV2Resource layer)
    {
        const string prefix = "layer-";
        if (layer.Metadata.Id.StartsWith(prefix, StringComparison.Ordinal) &&
            int.TryParse(layer.Metadata.Id[prefix.Length..], out var id))
        {
            return id;
        }

        throw new InvalidOperationException($"Test layer '{layer.Metadata.Id}' does not use the expected layer id prefix.");
    }
}

internal sealed class SpecGroundingHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public SpecGroundingHarness(params MetadataV2Resource[] layers)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        serviceCollection.AddSpecGrounding();
        serviceCollection.AddSingleton<IMetadataV2GraphProvider>(SpecGroundingTestSupport.CreateGraphProvider(layers));
        _services = serviceCollection.BuildServiceProvider();
    }

    public SpecGroundingService Service => _services.GetRequiredService<SpecGroundingService>();

    public ISpecParser Parser => _services.GetRequiredService<ISpecParser>();

    public ISpecCanonicalizer Canonicalizer => _services.GetRequiredService<ISpecCanonicalizer>();

    public SpecDocument Parse(string source)
    {
        var result = Parser.Parse(source);
        result.Diagnostics.Should().BeEmpty(
            "grounding test fixtures should parse without spec grammar diagnostics");
        result.Document.Should().NotBeNull();
        return result.Document!;
    }

    public string ToCanonicalJson(SpecDocument document) => Canonicalizer.ToJson(document);

    public void Dispose() => _services.Dispose();
}
