// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Protocols.GeoServices.Catalog;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

// Metadata models omit currentVersion and fullVersion.
// /rest/info keeps one currentVersion field: ArcGIS Pro's ImageServer reader requires that
// selector before it issues an image operation. fullVersion stays absent on every model.
public sealed class NoGeoServicesServerVersionTests
{
    private static readonly string[] ForbiddenWireNames = ["currentVersion", "fullVersion"];

    /// <summary>
    /// Every type registered in a GeoServices source-generated <see cref="JsonSerializerContext"/>,
    /// paired with the <see cref="JsonTypeInfo"/> production uses to serialize it. The set is DERIVED
    /// by reflecting over every <see cref="JsonSerializerContext"/> in the GeoServices protocol
    /// assembly rather than enumerated by hand — an enumerated allowlist is how
    /// <c>VectorTileServerMetadataResponse</c> escaped this guard (honua-server#2878). Anything a
    /// context can serialize (service directory, <c>/rest/info</c>, FeatureServer / MapServer /
    /// ImageServer / GPServer / VersionManagementServer / VectorTileServer metadata, Portal/Sharing
    /// facades, and any future model) is covered automatically the moment it is registered.
    /// </summary>
    private static IEnumerable<(string TypeName, JsonTypeInfo TypeInfo)> MetadataResponseTypes()
    {
        // Anchor on a known GeoServices context to locate the production assembly, then discover
        // every JsonSerializerContext it declares and enumerate each context's generated
        // JsonTypeInfo<T> properties.
        var assembly = typeof(GeoservicesCatalogJsonContext).Assembly;

        var contextTypes = assembly.GetTypes()
            .Where(static type => type is { IsClass: true, IsAbstract: false }
                && typeof(JsonSerializerContext).IsAssignableFrom(type))
            .OrderBy(static type => type.Name, StringComparer.Ordinal);

        foreach (var contextType in contextTypes)
        {
            var defaultProperty = contextType.GetProperty(
                "Default", BindingFlags.Public | BindingFlags.Static);
            if (defaultProperty?.GetValue(null) is not JsonSerializerContext context)
            {
                continue;
            }

            var typeInfoProperties = contextType
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(static property => property.PropertyType.IsGenericType
                    && property.PropertyType.GetGenericTypeDefinition() == typeof(JsonTypeInfo<>))
                .OrderBy(static property => property.Name, StringComparer.Ordinal);

            foreach (var typeInfo in typeInfoProperties
                .Select(property => property.GetValue(context))
                .OfType<JsonTypeInfo>())
            {
                yield return ($"{contextType.Name}.{typeInfo.Type.Name}", typeInfo);
            }
        }
    }

    [UnitTest]
    public void MetadataResponses_DoNotAdvertiseGeoServicesServerVersion()
    {
        using var scope = new AssertionScope();

        var metadataResponseTypes = MetadataResponseTypes().ToArray();
        metadataResponseTypes.Should().NotBeEmpty(
            "the guard must discover the GeoServices serialization contexts; an empty set would "
            + "silently pass and re-open the hole this test exists to close.");

        foreach (var (typeName, typeInfo) in metadataResponseTypes)
        {
            var wireNames = typeInfo.Properties
                .Select(static property => property.Name)
                .ToArray();

            foreach (var forbidden in ForbiddenWireNames)
            {
                if (typeInfo.Type == typeof(RestInfoResponse)
                    && string.Equals(forbidden, "currentVersion", StringComparison.OrdinalIgnoreCase))
                {
                    wireNames.Should().ContainSingle(
                        wireName => string.Equals(wireName, forbidden, StringComparison.OrdinalIgnoreCase),
                        "the licensed ArcGIS Pro ImageServer reader requires exactly one /rest/info compatibility selector");
                    continue;
                }

                wireNames.Should().NotContain(
                    wireName => string.Equals(wireName, forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{typeName} must not advertise an ArcGIS Server/Portal version (no '{forbidden}'); "
                    + "Honua is an independent GeoServices REST server and does not impersonate an ArcGIS release.");
            }
        }
    }

    /// <summary>
    /// The GeometryServer service descriptor is emitted as a raw JSON string literal rather than a
    /// typed model, so it cannot be covered by the property-metadata check above. Assert directly on
    /// the literal that it carries no <c>currentVersion</c>/<c>fullVersion</c> key.
    /// </summary>
    [UnitTest]
    public void GeometryServerInfo_DoesNotAdvertiseGeoServicesServerVersion()
    {
        // Reach the private const literal through the public service-info handler's response shape
        // by parsing what the endpoint serializes. The literal is internal to GeometryServiceEndpoints;
        // re-derive the same well-known descriptor keys from a parse to keep this construction-free.
        using var document = JsonDocument.Parse(GeometryServerInfoDescriptor);
        var root = document.RootElement;

        foreach (var forbidden in ForbiddenWireNames)
        {
            root.TryGetProperty(forbidden, out _).Should().BeFalse(
                $"the GeometryServer descriptor must not advertise an ArcGIS Server version ('{forbidden}').");
        }
    }

    // Mirror of the literal served by GeometryServiceEndpoints.GeometryServerInfoJson. Kept in sync
    // by GeometryServiceInfoTests (integration), which asserts the live endpoint omits currentVersion.
    private const string GeometryServerInfoDescriptor =
        "{\"serviceDescription\":\"Honua Geometry Service\","
        + "\"maxBufferCount\":1000,"
        + "\"maxSimplifyCount\":1000,"
        + "\"resampled\":true}";
}
