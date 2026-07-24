// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Server.Features.Geocoding;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Geocoding;

/// <summary>
/// Guard test: Honua is an independent, Esri-compatible server and must NOT advertise an
/// ArcGIS Server / ArcGIS Portal version. Every Esri-compatible metadata response the
/// <c>Honua.Server</c> host assembly serializes must therefore omit a <c>currentVersion</c>
/// or <c>fullVersion</c> field. <c>GeocodeServerInfoResponse</c> previously hardcoded
/// <c>10.81</c> (honua-server#2891), which falsely claimed a specific ArcGIS release.
///
/// This is the sibling of <c>NoArcGisServerVersionTests</c> in the GeoServices protocol
/// assembly. GeocodeServer lives in <c>Honua.Server</c>, not <c>Honua.Protocols.GeoServices</c>,
/// so it fell outside that guard's assembly and shipped ungated. This test closes the gap by
/// DERIVING its coverage from every <see cref="JsonSerializerContext"/> in the
/// <c>Honua.Server</c> assembly rather than enumerating a hand-written allowlist — an enumerated
/// allowlist is exactly how the original hardcodes escaped their guards (honua-server#2878).
/// Anything a Honua.Server context can serialize is covered automatically the moment it is
/// registered. Do not weaken it; remove the offending property instead.
/// </summary>
public sealed class NoHonuaServerArcGisVersionTests
{
    private static readonly string[] ForbiddenWireNames = ["currentVersion", "fullVersion"];

    /// <summary>
    /// Types that legitimately carry a <c>currentVersion</c> wire field whose meaning is a
    /// per-record DATA version (optimistic-concurrency / edit versioning), NOT an ArcGIS Server
    /// software release. These are exempted by exact full name so the guard stays fail-safe: any
    /// NEW type that introduces <c>currentVersion</c>/<c>fullVersion</c> is caught by default and
    /// must either drop the field (if it impersonates an ArcGIS release) or be added here with a
    /// justification. Keep this list minimal and documented.
    /// </summary>
    private static readonly HashSet<string> DataVersionExemptTypes = new(StringComparer.Ordinal)
    {
        // Optimistic-concurrency conflict payload: CurrentVersion is the feature's stored edit
        // version token returned to the client on a stale-write, unrelated to ArcGIS versioning.
        "Honua.Core.Features.Collaboration.FeatureLocks.FeatureVersionConflictError",
    };

    /// <summary>
    /// Every type registered in a <c>Honua.Server</c> source-generated
    /// <see cref="JsonSerializerContext"/>, paired with the <see cref="JsonTypeInfo"/> production
    /// uses to serialize it. The set is DERIVED by reflecting over every context in the host
    /// assembly, so newly added Esri-compatible service models (GeocodeServer and any future
    /// server-hosted service metadata) are covered without touching this test.
    /// </summary>
    private static IEnumerable<(string TypeName, JsonTypeInfo TypeInfo)> SerializableTypes()
    {
        // Anchor on a known Honua.Server type to locate the host assembly, then discover every
        // JsonSerializerContext it declares and enumerate each context's generated
        // JsonTypeInfo<T> properties.
        var assembly = typeof(GeocodeServerInfoResponse).Assembly;

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
                yield return (typeInfo.Type.FullName ?? typeInfo.Type.Name, typeInfo);
            }
        }
    }

    [UnitTest]
    public void SerializableResponses_DoNotAdvertiseArcGisServerVersion()
    {
        using var scope = new AssertionScope();

        var serializableTypes = SerializableTypes().ToArray();
        serializableTypes.Should().NotBeEmpty(
            "the guard must discover the Honua.Server serialization contexts; an empty set would "
            + "silently pass and re-open the hole this test exists to close.");

        foreach (var (typeName, typeInfo) in serializableTypes)
        {
            if (typeInfo.Type.FullName is { } fullName && DataVersionExemptTypes.Contains(fullName))
            {
                continue;
            }

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
}
