// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Xml.Linq;
using FluentAssertions;
using Honua.TestKit;

namespace Honua.Server.Tests.Features.CrossServerConsume;

internal static class CrossServerConsumeTestSupport
{
    internal const string ExternalServicesEnv = "HONUA_TEST_CROSS_SERVER_CONSUME";

    // WMS 1.3 EPSG:4326 uses latitude,longitude axis order. This envelope covers the seeded Hawaii data.
    internal const string HawaiiWms13Bbox4326 = "18.8,-161,22.5,-154.5";

    internal static bool IsEnabled()
        => !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ExternalServicesEnv));

    internal static string BuildUrl(string baseUrl, params (string Name, string Value)[] parameters)
    {
        var separator = baseUrl.Contains('?') ? '&' : '?';
        return baseUrl + separator + string.Join(
            '&',
            parameters.Select(static parameter =>
                $"{Uri.EscapeDataString(parameter.Name)}={Uri.EscapeDataString(parameter.Value)}"));
    }

    internal static async Task<XDocument> GetXmlAsync(HttpClient httpClient, string url)
    {
        using var response = await httpClient.GetAsync(url).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "source server request should succeed: {0}\n{1}", url, body);
        body.Should().NotBeNullOrWhiteSpace();

        return XDocument.Parse(body);
    }

    internal static async Task<string> GetTextAsync(HttpClient httpClient, string url)
    {
        using var response = await httpClient.GetAsync(url).ConfigureAwait(false);
        var body = await response.Content.ReadAsStringAsync().ConfigureAwait(false);

        response.StatusCode.Should().Be(HttpStatusCode.OK, "source server request should succeed: {0}\n{1}", url, body);
        body.Should().NotBeNullOrWhiteSpace();
        body.Should().NotContain("ServiceException", "GetFeatureInfo should not return an OGC exception report");

        return body;
    }

    internal static async Task<byte[]> GetImageAsync(HttpClient httpClient, string url)
    {
        using var response = await httpClient.GetAsync(url).ConfigureAwait(false);
        var body = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
        var contentType = response.Content.Headers.ContentType?.MediaType;

        response.StatusCode.Should().Be(HttpStatusCode.OK, "source server image request should succeed: {0}", url);
        contentType.Should().NotBeNull();
        contentType!.Should().StartWith("image/");
        body.Length.Should().BeGreaterThan(100, "source server should return a non-empty image payload");

        return body;
    }

    internal static void AssertRoot(XDocument document, string localName)
    {
        document.Root.Should().NotBeNull();
        document.Root!.Name.LocalName.Should().Be(localName);
    }

    internal static void AssertDocumentContains(XDocument document, string expectedValue)
    {
        document.Root.Should().NotBeNull();
        document.Root!.DescendantsAndSelf()
            .Any(element => element.Value.Contains(expectedValue, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("capabilities document should advertise '{0}'", expectedValue);
    }

    internal static void AssertFeatureCollectionHasFeature(XDocument document, string expectedLayerName)
    {
        AssertRoot(document, "FeatureCollection");

        if (document.Root!.Attribute("numberReturned")?.Value is { } numberReturnedValue &&
            int.TryParse(numberReturnedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var numberReturned))
        {
            numberReturned.Should().BeGreaterThan(0);
            return;
        }

        document.Descendants()
            .Any(element =>
                string.Equals(element.Name.LocalName, "member", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, "featureMember", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(element.Name.LocalName, expectedLayerName, StringComparison.OrdinalIgnoreCase))
            .Should().BeTrue("WFS GetFeature should return at least one feature for '{0}'", expectedLayerName);
    }

    internal static WmtsTileRequest SelectFirstAdvertisedTile(XDocument capabilities, string layerIdentifier)
    {
        var layer = capabilities.Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "Layer", StringComparison.Ordinal) &&
                string.Equals(ReadChildValue(element, "Identifier"), layerIdentifier, StringComparison.OrdinalIgnoreCase));

        layer.Should().NotBeNull("WMTS capabilities should advertise layer '{0}'", layerIdentifier);

        var format = layer!.Elements()
            .Where(static element => string.Equals(element.Name.LocalName, "Format", StringComparison.Ordinal))
            .Select(static element => element.Value.Trim())
            .FirstOrDefault(static value => string.Equals(value, "image/png", StringComparison.OrdinalIgnoreCase))
            ?? layer.Elements()
                .First(element => string.Equals(element.Name.LocalName, "Format", StringComparison.Ordinal))
                .Value.Trim();

        var style = layer.Elements()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "Style", StringComparison.Ordinal));
        var styleIdentifier = style is null ? string.Empty : ReadChildValue(style, "Identifier") ?? string.Empty;

        var matrixSetLink = layer.Elements()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TileMatrixSetLink", StringComparison.Ordinal));
        matrixSetLink.Should().NotBeNull("WMTS layer '{0}' should link at least one tile matrix set", layerIdentifier);

        var matrixSet = ReadChildValue(matrixSetLink!, "TileMatrixSet");
        matrixSet.Should().NotBeNullOrWhiteSpace();

        var limits = matrixSetLink!.Descendants()
            .FirstOrDefault(static element => string.Equals(element.Name.LocalName, "TileMatrixLimits", StringComparison.Ordinal));
        if (limits is not null)
        {
            return new WmtsTileRequest(
                layerIdentifier,
                styleIdentifier,
                format,
                matrixSet!,
                ReadChildValue(limits, "TileMatrix")!,
                ReadIntChildValue(limits, "MinTileRow"),
                ReadIntChildValue(limits, "MinTileCol"));
        }

        var matrixSetElement = capabilities.Descendants()
            .FirstOrDefault(element =>
                string.Equals(element.Name.LocalName, "TileMatrixSet", StringComparison.Ordinal) &&
                string.Equals(ReadChildValue(element, "Identifier"), matrixSet, StringComparison.OrdinalIgnoreCase));
        matrixSetElement.Should().NotBeNull("WMTS capabilities should define tile matrix set '{0}'", matrixSet);

        var tileMatrix = matrixSetElement!.Elements()
            .First(element => string.Equals(element.Name.LocalName, "TileMatrix", StringComparison.Ordinal));

        return new WmtsTileRequest(
            layerIdentifier,
            styleIdentifier,
            format,
            matrixSet!,
            ReadChildValue(tileMatrix, "Identifier")!,
            TileRow: 0,
            TileCol: 0);
    }

    private static string? ReadChildValue(XElement element, string localName)
        => element.Elements()
            .FirstOrDefault(child => string.Equals(child.Name.LocalName, localName, StringComparison.Ordinal))
            ?.Value.Trim();

    private static int ReadIntChildValue(XElement element, string localName)
    {
        var value = ReadChildValue(element, localName);
        value.Should().NotBeNullOrWhiteSpace();
        return int.Parse(value!, CultureInfo.InvariantCulture);
    }
}

public sealed class CrossServerConsumeGeoServerFixture : IAsyncLifetime
{
    private readonly GeoServerFixture _geoServer = new(seedCuratedData: true);

    public string BaseUrl => _geoServer.BaseUrl;

    public Task InitializeAsync()
        => CrossServerConsumeTestSupport.IsEnabled()
            ? _geoServer.InitializeAsync()
            : Task.CompletedTask;

    public Task DisposeAsync()
        => CrossServerConsumeTestSupport.IsEnabled()
            ? _geoServer.DisposeAsync()
            : Task.CompletedTask;
}

public sealed class CrossServerConsumeMapServerFixture : IAsyncLifetime
{
    private readonly MapServerFixture _mapServer = new();

    public string EndpointUrl => _mapServer.EndpointUrl;

    public Task InitializeAsync()
        => CrossServerConsumeTestSupport.IsEnabled()
            ? _mapServer.InitializeAsync()
            : Task.CompletedTask;

    public Task DisposeAsync()
        => CrossServerConsumeTestSupport.IsEnabled()
            ? _mapServer.DisposeAsync()
            : Task.CompletedTask;
}

internal sealed record WmtsTileRequest(
    string Layer,
    string Style,
    string Format,
    string TileMatrixSet,
    string TileMatrix,
    int TileRow,
    int TileCol);
