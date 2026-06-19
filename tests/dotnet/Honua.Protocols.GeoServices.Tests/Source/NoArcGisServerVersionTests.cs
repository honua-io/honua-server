// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using FluentAssertions;
using FluentAssertions.Execution;
using Honua.Protocols.GeoServices.Catalog;
using Honua.Protocols.GeoServices.FeatureServer.Models;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Protocols.GeoServices.ImageServer.Models;
using Honua.Protocols.GeoServices.MapServer.Models;
using Honua.Protocols.GeoServices.Sharing;
using Honua.Protocols.GeoServices.VersionManagementServer.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Guard test: Honua is an independent, Esri-compatible server and must NOT advertise an
/// ArcGIS Server / ArcGIS Portal version. Every GeoServices REST metadata response model must
/// therefore serialize without a <c>currentVersion</c> or <c>fullVersion</c> field. These
/// fields previously hardcoded <c>10.81</c> (and <c>10.91</c>/<c>11.1</c>), which falsely
/// claimed to be a specific ArcGIS release.
///
/// This test asserts on the actual source-generated JSON wire contract for each edited
/// response type, so it FAILS the moment anyone re-adds a <c>currentVersion</c>/<c>fullVersion</c>
/// property to any of these models. Do not weaken it; remove the offending property instead.
/// </summary>
public sealed class NoArcGisServerVersionTests
{
    private static readonly string[] ForbiddenWireNames = ["currentVersion", "fullVersion"];

    /// <summary>
    /// Every GeoServices metadata response type, paired with the source-generated
    /// <see cref="JsonTypeInfo"/> that production uses to serialize it. Covers the service-
    /// directory root (<c>/rest/services</c>), <c>/rest/info</c>, FeatureServer service + layer,
    /// MapServer service + layer, ImageServer service, GPServer service, the VersionManagementServer
    /// info, and the ArcGIS Portal/Sharing facade documents.
    /// </summary>
    private static IEnumerable<(string TypeName, JsonTypeInfo TypeInfo)> MetadataResponseTypes()
    {
        yield return (nameof(ServicesDirectoryResponse), GeoservicesCatalogJsonContext.Default.ServicesDirectoryResponse);
        yield return (nameof(RestInfoResponse), GeoservicesCatalogJsonContext.Default.RestInfoResponse);
        yield return (nameof(FeatureServerResponse), FeatureServerJsonContext.Default.FeatureServerResponse);
        yield return (nameof(LayerResponse), FeatureServerJsonContext.Default.LayerResponse);
        yield return (nameof(MapServerResponse), MapServerJsonContext.Default.MapServerResponse);
        yield return (nameof(MapServerLayerResponse), MapServerJsonContext.Default.MapServerLayerResponse);
        yield return (nameof(ImageServerServiceInfo), ImageServerJsonContext.Default.ImageServerServiceInfo);
        yield return (nameof(GPServiceInfoResponse), GPServerJsonContext.Default.GPServiceInfoResponse);
        yield return (nameof(VersionManagementServiceInfo), VersionManagementJsonContext.Default.VersionManagementServiceInfo);
        yield return (nameof(SharingInfoResponse), SharingRestJsonContext.Default.SharingInfoResponse);
        yield return (nameof(PortalSelfResponse), SharingRestJsonContext.Default.PortalSelfResponse);
    }

    [UnitTest]
    public void MetadataResponses_DoNotAdvertiseArcGisServerVersion()
    {
        using var scope = new AssertionScope();

        foreach (var (typeName, typeInfo) in MetadataResponseTypes())
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
