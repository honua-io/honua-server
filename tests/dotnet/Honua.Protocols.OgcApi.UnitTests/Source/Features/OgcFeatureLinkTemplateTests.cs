// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Protocols.Ogc.Api.Features;
using Honua.Protocols.Ogc.Common;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Protocols.OgcApi.UnitTests.Features;

public sealed class OgcFeatureLinkTemplateTests
{
    private const string Items = "https://example.test/ogc/features/collections/my%20layer/items/a%2Fb";
    private const string Crs = "crs=http%3A%2F%2Fwww.opengis.net%2Fdef%2Fcrs%2FEPSG%2F0%2F4326";
    private static readonly string[] FeatureIds = ["1", "a b", "é/ü?"];
    private const string Collection = "collection|application/json|Collection|https://example.test/ogc/features/collections/my%20layer";

    [UnitTest]
    public void BuildFeatureLinks_RequestCrs_PinsEveryHref()
    {
        var links = Describe(OgcFeaturesUtilities.BuildFeatureLinks(
            Request("?crs=http://www.opengis.net/def/crs/EPSG/0/4326"), "my layer", "a/b", MediaTypes.GeoJson));

        Assert.Equal(
            [
                $"self|application/geo+json|Feature|{Items}?{Crs}&f=geojson",
                $"alternate|application/json|JSON|{Items}?{Crs}&f=json",
                $"alternate|application/gml+xml; version=3.2|GML|{Items}?{Crs}&f=gml",
                $"alternate|text/csv|CSV|{Items}?{Crs}&f=csv",
                $"alternate|text/html|HTML|{Items}?{Crs}&f=html",
                Collection,
            ],
            links);
    }

    [UnitTest]
    public void BuildFeatureLinks_ResponseCrsAndUnlistedFormat_PinsEveryHref()
    {
        var links = Describe(OgcFeaturesUtilities.BuildFeatureLinks(
            Request(string.Empty), "my layer", "a/b", "application/x-test", OgcFeaturesUtilities.Epsg4326Uri));

        Assert.Equal(
            [
                $"self|application/x-test|Feature|{Items}?{Crs}",
                $"alternate|application/geo+json|GeoJSON|{Items}?{Crs}&f=geojson",
                $"alternate|application/json|JSON|{Items}?{Crs}&f=json",
                $"alternate|application/gml+xml; version=3.2|GML|{Items}?{Crs}&f=gml",
                $"alternate|text/csv|CSV|{Items}?{Crs}&f=csv",
                $"alternate|text/html|HTML|{Items}?{Crs}&f=html",
                Collection,
            ],
            links);
    }

    [UnitTest]
    public void BuildFeatureLinks_NoCrs_WritesFormatOnly()
    {
        var links = Describe(OgcFeaturesUtilities.BuildFeatureLinks(
            Request("?crs=%20"), "my layer", "a/b", MediaTypes.Json));

        Assert.Equal($"self|application/json|Feature|{Items}?f=json", links[0]);
        Assert.Equal($"alternate|application/geo+json|GeoJSON|{Items}?f=geojson", links[1]);
        Assert.Equal(6, links.Length);
    }

    [UnitTest]
    public void BuildFeatureLinks_RequestCrsAndResponseCrs_RequestCrsWins()
    {
        var links = OgcFeaturesUtilities.BuildFeatureLinks(
            Request("?crs=EPSG:3857"), "my layer", "a/b", MediaTypes.GeoJson, OgcFeaturesUtilities.Epsg4326Uri);

        Assert.Equal($"{Items}?crs=EPSG%3A3857&f=geojson", links[0].Href);
        Assert.All(links[..^1], link => Assert.Contains("?crs=EPSG%3A3857&f=", link.Href, StringComparison.Ordinal));
    }

    [UnitTest]
    public void Template_ReusedAcrossFeatures_EscapesEachFeatureId()
    {
        var template = OgcFeatureLinkTemplate.Create(Request("?crs=EPSG:3857"), new("my layer", MediaTypes.Csv));
        const string ItemsBase = "https://example.test/ogc/features/collections/my%20layer/items/";

        var hrefs = FeatureIds.Select(id => template.Build(id)[0].Href);

        Assert.Equal(
            [
                $"{ItemsBase}1?crs=EPSG%3A3857&f=csv",
                $"{ItemsBase}a%20b?crs=EPSG%3A3857&f=csv",
                $"{ItemsBase}%C3%A9%2F%C3%BC%3F?crs=EPSG%3A3857&f=csv",
            ],
            hrefs);
        Assert.Same(template.Build("1")[^1], template.Build("2")[^1]);
    }

    private static string[] Describe(IEnumerable<Link> links)
        => [.. links.Select(link => $"{link.Rel}|{link.Type}|{link.Title}|{link.Href}")];

    private static HttpRequest Request(string queryString)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["Public:BaseUrl"] = "https://example.test" })
            .Build();
        var services = new ServiceCollection().AddSingleton<IConfiguration>(configuration);
        var context = new DefaultHttpContext { RequestServices = services.BuildServiceProvider() };
        context.Request.QueryString = new QueryString(queryString);
        return context.Request;
    }
}
