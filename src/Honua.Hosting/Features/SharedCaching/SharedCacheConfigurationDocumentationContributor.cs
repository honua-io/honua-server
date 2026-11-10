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
                    .. Lifetime("StaticMapImage", "static map images", (ProductDefaults.StaticMapImage, current.StaticMapImage)),
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
                ],
            },
        ];
    }

    public IReadOnlyList<EnvironmentVariableInfo> GetEnvironmentVariables() =>
    [
        new() { Name = "SharedCache__Tile__SharedMaxAgeSeconds", ConfigPath = "SharedCache.Tile", Description = "Seconds the CDN keeps a public tile", Default = "300", Required = false, Example = "600" },
        new() { Name = "SharedCache__Layers__{serviceId}/{layerId}__SharedMaxAgeSeconds", ConfigPath = "SharedCache.Layers", Description = "Seconds the CDN keeps responses for one layer", Default = "", Required = false, Example = "15" },
        new() { Name = "SharedCache__Emergency__AllPublicLayers", ConfigPath = "SharedCache.Emergency", Description = "Force the emergency lifetime on every public layer", Default = "false", Required = false, Example = "true" },
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
