// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Catalog.Abstractions;
using Honua.Core.Features.Catalog.Domain;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Core.Features.Spec;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Core.Features.Shared.Models;
using Honua.Server.Features.Grounding.Spec;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.Grounding.Spec;

internal static class SpecGroundingTestSupport
{
    public static LayerDefinition CreateLayer(
        int id,
        string name,
        string? description = null,
        params FieldDefinition[] fields)
    {
        var layerFields = fields.Length > 0
            ? fields
            : DefaultFields;

        return new LayerDefinition(
            Id: id,
            Name: name,
            Description: description,
            GeometryType: GeometryType.Point,
            SpatialReference: SpatialReference.WGS84,
            Fields: layerFields);
    }

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

    private static readonly FieldDefinition[] DefaultFields =
    [
        new("objectid", FieldType.Integer, Nullable: false),
        new("name", FieldType.String, Length: 128),
        new("category", FieldType.String, Length: 64)
    ];
}

internal sealed class SpecGroundingHarness : IDisposable
{
    private readonly ServiceProvider _services;

    public SpecGroundingHarness(params LayerDefinition[] layers)
    {
        var serviceCollection = new ServiceCollection();
        serviceCollection.AddLogging(builder => builder.SetMinimumLevel(LogLevel.Debug));
        serviceCollection.AddSpecGrounding();
        serviceCollection.AddSingleton<ILayerCatalog>(new SpecGroundingLayerCatalog(layers));
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

internal sealed class SpecGroundingLayerCatalog : ILayerCatalog
{
    private readonly LayerDefinition[] _layers;
    private readonly ServiceDefinition[] _services;

    public SpecGroundingLayerCatalog(params LayerDefinition[] layers)
    {
        _layers = layers;
        _services =
        [
            new ServiceDefinition(
                "grounding",
                "Grounding test service",
                _layers,
                SpatialReference.WGS84)
        ];
    }

    public Task<LayerDefinition?> GetLayerAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.FirstOrDefault(layer => layer.Id == layerId));

    public Task<LayerDefinition[]> ListLayersAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_layers);

    public Task<ServiceDefinition?> GetServiceAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(_services.FirstOrDefault(service =>
            string.Equals(service.Name, serviceName, StringComparison.OrdinalIgnoreCase)));

    public Task<ServiceDefinition[]> ListServicesAsync(CancellationToken cancellationToken = default)
        => Task.FromResult(_services);

    public Task<bool> LayerExistsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(_layers.Any(layer => layer.Id == layerId));

    public Task<bool> ServiceExistsAsync(string serviceName, CancellationToken cancellationToken = default)
        => Task.FromResult(_services.Any(service =>
            string.Equals(service.Name, serviceName, StringComparison.OrdinalIgnoreCase)));

    public Task<Relationship?> GetRelationshipAsync(int layerId, int relationshipId, CancellationToken cancellationToken = default)
        => Task.FromResult<Relationship?>(null);

    public Task<Relationship[]> ListRelationshipsAsync(int layerId, CancellationToken cancellationToken = default)
        => Task.FromResult(Array.Empty<Relationship>());
}
