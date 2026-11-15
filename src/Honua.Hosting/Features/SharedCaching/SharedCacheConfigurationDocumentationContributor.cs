// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Configuration;
using Honua.Infrastructure.Abstractions;
using Microsoft.Extensions.Options;
using ConfigurationSection = Honua.Core.Configuration.ConfigurationSection;

namespace Honua.Infrastructure.SharedCaching;

// Makes the effective cache lifetimes, per-layer lifetimes and the emergency setting readable through the admin configuration API.
internal sealed class SharedCacheConfigurationDocumentationContributor(IOptionsMonitor<SharedCacheOptions> options)
    : IConfigurationDocumentationContributor
{
    private const string Section = SharedCacheOptions.SectionName;
    private static readonly SharedCacheOptions ProductDefaults = new();

    public IReadOnlyList<ConfigurationSection> GetSections()
    {
        var current = options.CurrentValue;
        return
        [
            new ConfigurationSection
            {
                Name = Section,
                Description = "What a CDN in front of the service may store, and for how long",
                Properties =
                [
                    .. Lifetime("Tile", "public tiles", (ProductDefaults.Tile, current.Tile)),
                    .. Lifetime("FeatureQuery", "public feature queries", (ProductDefaults.FeatureQuery, current.FeatureQuery)),
                    .. Lifetime("MetadataDocument", "capabilities and metadata documents", (ProductDefaults.MetadataDocument, current.MetadataDocument)),
                    .. Lifetime("StaticObject", "stored objects served by the server", (ProductDefaults.StaticObject, current.StaticObject)),
                    .. current.Layers.SelectMany(layer => Lifetime($"Layers:{layer.Key}", $"layer {layer.Key}", (null, layer.Value))),
                    Property(
                        "StaleIfErrorSeconds",
                        "How long the CDN may serve a public tile or query while the service is unreachable",
                        (ProductDefaults.StaleIfErrorSeconds, current.StaleIfErrorSeconds)),
                    Property(
                        "Emergency:AllPublicLayers",
                        "Forces every public layer to 15 seconds at the CDN and 0 in the browser",
                        (false, current.Emergency.AllPublicLayers)),
                    Property(
                        "Emergency:Layers",
                        "Layers, named serviceId/layerId, forced to the emergency lifetime",
                        (string.Empty, string.Join(',', current.Emergency.Layers))),
                    Property(
                        "CacheTagHeader",
                        "Response header the CDN reads cache tags from; when set, edits invalidate by tag instead of by path",
                        (string.Empty, current.CacheTagHeader ?? string.Empty)),
                    .. PurgeBudgetProperties(current.PurgeBudget),
                    .. PurgeBackedProperties(current.PurgeBacked),
                ],
            },
        ];
    }

    private static ConfigurationProperty[] PurgeBudgetProperties(CdnPurgeBudgetOptions budget) =>
    [
        Property(
            "PurgeBudget:MonthlyEditSubmissions",
            "Edit purges sent this UTC month before further edit purges wait for the cache lifetime. Zero disables the cap. Access changes and releases are not counted",
            (CdnPurgeBudgetOptions.DefaultMonthlyEditSubmissions, budget.MonthlyEditSubmissions)),
        Property(
            "PurgeBudget:EstimatedCostPerSubmission",
            "Operator-supplied cost added to the month's spend estimate for each admitted edit purge",
            (0m, budget.EstimatedCostPerSubmission)),
    ];

    private static ConfigurationProperty[] PurgeBackedProperties(SharedCachePurgeBacked purgeBacked) =>
    [
        Property(
            "PurgeBacked:AllLayers",
            "Every layer is written only through the service, so its public tiles and queries may use the purge-backed CDN lifetimes",
            (false, purgeBacked.AllLayers)),
        Property(
            "PurgeBacked:Layers",
            "Layers, named serviceId/layerId, written only through the service, whose public tiles and queries may use the purge-backed CDN lifetimes",
            (string.Empty, string.Join(',', purgeBacked.Layers))),
        .. Lifetime("PurgeBacked:Tile", "public tiles of purge-backed layers", (ProductDefaults.PurgeBacked.Tile, purgeBacked.Tile)),
        .. Lifetime("PurgeBacked:FeatureQuery", "public feature queries of purge-backed layers", (ProductDefaults.PurgeBacked.FeatureQuery, purgeBacked.FeatureQuery)),
    ];

    public IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables() =>
    [
        new() { Name = "SharedCache__Tile__SharedMaxAgeSeconds", ConfigPath = "SharedCache.Tile", Description = "Seconds the CDN keeps a public tile", Default = "300", Required = false, Example = "600" },
        new() { Name = "SharedCache__Layers__{serviceId}/{layerId}__SharedMaxAgeSeconds", ConfigPath = "SharedCache.Layers", Description = "Seconds the CDN keeps responses for one layer", Default = "", Required = false, Example = "15" },
        new() { Name = "SharedCache__Emergency__AllPublicLayers", ConfigPath = "SharedCache.Emergency", Description = "Force the emergency lifetime on every public layer", Default = "false", Required = false, Example = "true" },
        new() { Name = "SharedCache__PurgeBacked__Layers__0", ConfigPath = "SharedCache.PurgeBacked.Layers", Description = "A layer written only through the service; its public tiles and queries may use the purge-backed CDN lifetimes while tag purges reach the CDN", Default = "", Required = false, Example = "parcels/3" },
        new() { Name = "SharedCache__PurgeBacked__AllLayers", ConfigPath = "SharedCache.PurgeBacked.AllLayers", Description = "Treat every layer as written only through the service", Default = "false", Required = false, Example = "true" },
        new() { Name = "SharedCache__PurgeBacked__Tile__SharedMaxAgeSeconds", ConfigPath = "SharedCache.PurgeBacked.Tile", Description = "Seconds the CDN keeps a public tile of a purge-backed layer", Default = "86400", Required = false, Example = "86400" },
        new() { Name = "SharedCache__PurgeBacked__FeatureQuery__SharedMaxAgeSeconds", ConfigPath = "SharedCache.PurgeBacked.FeatureQuery", Description = "Seconds the CDN keeps a public feature query of a purge-backed layer", Default = "300", Required = false, Example = "300" },
        new() { Name = "SharedCache__CacheTagHeader", ConfigPath = "SharedCache.CacheTagHeader", Description = "Response header the CDN's cache-tag configuration reads; empty sends no tags", Default = "", Required = false, Example = "x-amz-meta-cache-tag" },
        new() { Name = "SharedCache__PurgeBudget__MonthlyEditSubmissions", ConfigPath = "SharedCache.PurgeBudget.MonthlyEditSubmissions", Description = "Edit purges admitted per UTC month before edit purges pause. Zero disables the cap", Default = "1000", Required = false, Example = "1000" },
        new() { Name = "SharedCache__PurgeBudget__EstimatedCostPerSubmission", ConfigPath = "SharedCache.PurgeBudget.EstimatedCostPerSubmission", Description = "Operator-supplied unit cost added to the monthly spend estimate. Zero records a count only", Default = "0", Required = false, Example = "0" },
        new() { Name = "SharedCache__CloudFront__DistributionId", ConfigPath = "SharedCache.CloudFront", Description = "CloudFront distribution to invalidate when content changes", Default = "", Required = false, Example = "E2EXAMPLE123" },
    ];

    private static ConfigurationProperty[] Lifetime(
        string name, string subject, (SharedCacheLifetime? ProductDefault, SharedCacheLifetime Current) lifetime) =>
    [
        Property(
            $"{name}:MaxAgeSeconds",
            $"Seconds a browser keeps {subject}",
            (lifetime.ProductDefault?.MaxAgeSeconds, lifetime.Current.MaxAgeSeconds)),
        Property(
            $"{name}:SharedMaxAgeSeconds",
            $"Seconds the CDN keeps {subject}",
            (lifetime.ProductDefault?.SharedMaxAgeSeconds, lifetime.Current.SharedMaxAgeSeconds)),
    ];

    private static ConfigurationProperty Property(string name, string description, (object? Default, object Current) value)
    {
        var path = $"{Section}:{name}";
        return new ConfigurationProperty
        {
            Name = name,
            Path = path,
            EnvironmentVariable = path.Replace(":", "__", StringComparison.Ordinal),
            Type = value.Current switch
            {
                int => "integer",
                bool => "boolean",
                _ => "string",
            },
            Description = description,
            DefaultValue = value.Default,
            CurrentValue = value.Current,
        };
    }
}
