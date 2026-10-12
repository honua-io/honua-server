// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Protocols.OData.Models;
using Honua.Protocols.OData.Services;
using Xunit;

namespace Honua.Protocols.OData.Tests.Services;

/// <summary>
/// Unit tests for the AOT-safe ETag canonicalizer (#1647). The previous implementation
/// used the reflection-based <c>JsonSerializer.SerializeToUtf8Bytes(object)</c> overload,
/// which throws "Reflection-based serialization has been disabled" under PublishAot=true
/// and 500'd every OData write on AOT Lambda images.
/// </summary>
[Trait("Category", "Unit")]
[Trait("Component", "OData")]
[Trait("Feature", "ETag")]
public sealed class ODataEtagSerializationTests
{
    [Fact]
    public void SerializeForEtag_DoesNotUseReflectionSerializer_AndProducesBytes()
    {
        // The mix of scalar types and a complex geometry object is exactly what tripped
        // the reflection serializer on AOT. This must succeed without throwing.
        var payload = SamplePayload();

        var bytes = ODataUtilityService.SerializeForEtag(payload);

        Assert.NotEmpty(bytes);
        // Sanity: the output is valid JSON text.
        var json = Encoding.UTF8.GetString(bytes);
        Assert.StartsWith("{", json, StringComparison.Ordinal);
        Assert.Contains("\"Wailuku\"", json, StringComparison.Ordinal);
    }

    [Fact]
    public void SerializeForEtag_IsKeyOrderIndependent()
    {
        var first = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = 1L,
            ["Name"] = "Wailuku",
            ["Acres"] = 12.5,
        };
        var second = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Acres"] = 12.5,
            ["Name"] = "Wailuku",
            ["ObjectId"] = 1L,
        };

        // Canonicalization sorts keys, so insertion order must not change the ETag bytes.
        Assert.Equal(
            ODataUtilityService.SerializeForEtag(first),
            ODataUtilityService.SerializeForEtag(second));
    }

    [Fact]
    public void SerializeForEtag_GeometryChange_ChangesBytes()
    {
        var withPoint = SamplePayload();
        var withMovedPoint = SamplePayload();
        withMovedPoint["Geometry"] = new ODataSpatialGeometry
        {
            Type = "Point",
            CoordinatesJson = "[1.0,2.0]",
        };

        // Geometry must remain part of the canonical ETag input — otherwise optimistic
        // concurrency would not detect a geometry-only edit.
        Assert.NotEqual(
            ODataUtilityService.SerializeForEtag(withPoint),
            ODataUtilityService.SerializeForEtag(withMovedPoint));
    }

    private static Dictionary<string, object?> SamplePayload() =>
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["ObjectId"] = 1L,
            ["Name"] = "Wailuku",
            ["Acres"] = 12.5,
            ["Active"] = true,
            ["Geometry"] = new ODataSpatialGeometry
            {
                Type = "Point",
                CoordinatesJson = "[0.0,0.0]",
            },
        };
}
