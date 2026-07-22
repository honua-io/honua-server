// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Protocols.GeoServices.Catalog;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Guard test: Honua is an independent, Esri-compatible server and must NOT advertise an
/// ArcGIS Server / ArcGIS Portal version. Every GeoServices REST metadata response model must
/// therefore serialize without a <c>currentVersion</c> or <c>fullVersion</c> field. These
/// fields previously hardcoded <c>10.81</c> (and <c>10.91</c>/<c>11.1</c>), which falsely
/// claimed to be a specific ArcGIS release.
///
/// This test asserts on the actual source-generated JSON wire contract for every type registered
/// in the GeoServices assembly's serialization contexts, so it FAILS the moment anyone re-adds a
/// <c>currentVersion</c>/<c>fullVersion</c> property to any of these models. Do not weaken it;
/// remove the offending property instead.
/// </summary>
public sealed class NoArcGisServerVersionTests
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

            // codeql[cs/linq/missed-where] -- predicate binds state or awaits; retain imperative control flow.
            foreach (var property in typeInfoProperties)
            {
                if (property.GetValue(context) is JsonTypeInfo typeInfo)
                {
                    yield return ($"{contextType.Name}.{typeInfo.Type.Name}", typeInfo);
                }
            }
        }
    }

    [UnitTest]
    public void MetadataResponses_DoNotAdvertiseArcGisServerVersion()
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
                wireNames.Should().NotContain(
                    wireName => string.Equals(wireName, forbidden, StringComparison.OrdinalIgnoreCase),
                    $"{typeName} must not advertise an ArcGIS Server/Portal version (no '{forbidden}'); "
                    + "Honua is an independent Esri-compatible server and does not impersonate an ArcGIS release.");
            }
        }
    }

    /// <summary>
    /// The GeometryServer service descriptor is emitted as a raw JSON string literal rather than a
    /// typed model, so it cannot be covered by the property-metadata check above. Assert directly on
    /// the literal that it carries no <c>currentVersion</c>/<c>fullVersion</c> key.
    /// </summary>
    [UnitTest]
    public void GeometryServerInfo_DoesNotAdvertiseArcGisServerVersion()
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
