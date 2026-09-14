// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Infrastructure.Security;
using Honua.Protocols.GeoServices;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Tests.Infrastructure.Security;

[Trait("Category", "Unit")]
[Trait("Tier", "Fast")]
public sealed class GeometryInputBudgetTests
{
    [Theory]
    [InlineData("GET")]
    [InlineData("POST")]
    public async Task DeclaredGeometry_OverTextLimit_PreservesCompleteValue(string method)
    {
        var geometry = DetailedPolygon();
        Assert.True(geometry.Length > 8192);
        var result = await InvokeAsync(geometry, method);
        Assert.True(result.Passed);
        Assert.Equal(geometry, result.ForwardedValue);
    }

    [Fact]
    public async Task GeometryNameWithoutEndpointDeclaration_RetainsTextLimit()
    {
        var result = await InvokeAsync(DetailedPolygon(), declared: false);
        Assert.False(result.Passed);
        Assert.Contains("8192", result.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("where")]
    [InlineData("Authorization")]
    public async Task OtherParametersAndHeaders_RetainTextLimit(string name)
    {
        var result = await InvokeAsync(new string('a', 8193), name: name);
        Assert.False(result.Passed);
        Assert.Contains("8192", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task GeometryBudget_MeasuresUtf8BytesAndHonorsExactBoundary()
    {
        const string geometry = "{\"x\":1,\"y\":2,\"label\":\"東京\"}";
        var bytes = Encoding.UTF8.GetByteCount(geometry);
        Assert.True(bytes > geometry.Length);
        Assert.True((await InvokeAsync(geometry, maxBytes: bytes)).Passed);
        var result = await InvokeAsync(geometry, maxBytes: bytes - 1);
        Assert.False(result.Passed);
        Assert.Contains("Limits:Geometry:MaxGeometrySize", result.Body, StringComparison.Ordinal);
    }

    [Fact]
    public async Task VertexBudget_AppliesAcrossAllPartsIncludingClosingPositions()
    {
        const string geometry = "{\"paths\":[[[1,2],[3,4]],[[5,6],[7,8]]]}";
        Assert.True((await InvokeAsync(geometry, maxVertices: 4)).Passed);
        var result = await InvokeAsync(geometry, maxVertices: 3);
        Assert.False(result.Passed);
        Assert.Contains("MaxVerticesPerGeometry", result.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("{}")]
    [InlineData("{\"x\":1}")]
    [InlineData("{\"x\":1,\"y\":1e999}")]
    [InlineData("{\"points\":[]}")]
    [InlineData("{\"paths\":[null]}")]
    [InlineData("{\"points\":[[1]]}")]
    [InlineData("{\"points\":[[1,1e999]]}")]
    public async Task InvalidGeometry_IsRejectedBeforeDispatch(string geometry)
    {
        Assert.False((await InvokeAsync(geometry)).Passed);
    }

    [Fact]
    public async Task ExcessiveJsonDepth_IsRejectedBeforeDispatch()
    {
        var geometry = "{\"paths\":" + new string('[', 80) + "0" + new string(']', 80) + "}";
        Assert.False((await InvokeAsync(geometry)).Passed);
    }

    [Fact]
    public async Task GeometryDeclaration_DoesNotSkipInjectionChecks()
    {
        var result = await InvokeAsync("{\"x\":1,\"y\":2,\"label\":\"<script>alert(1)</script>\"}");
        Assert.False(result.Passed);
        Assert.Contains("XSS", result.Body, StringComparison.Ordinal);
    }

    private static string DetailedPolygon()
    {
        var ring = Enumerable.Range(0, 401).Select(i =>
        {
            var angle = -(i % 400) * Math.PI * 2 / 400;
            return new[] { -74 + Math.Cos(angle) / 100, 40.7 + Math.Sin(angle) / 100 };
        }).ToArray();
        return JsonSerializer.Serialize(new { rings = new[] { ring }, spatialReference = new { wkid = 4326 } });
    }

    private static async Task<(bool Passed, string? ForwardedValue, string Body)> InvokeAsync(
        string value,
        string method = "POST",
        bool declared = true,
        string name = "geometry",
        long maxBytes = 5 * 1024 * 1024,
        int maxVertices = 50_000)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/rest/services/test/FeatureServer/1/query";
        context.Request.Method = method;
        context.Response.Body = new MemoryStream();
        if (declared)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(GeoServicesQueryGeometryMetadata.Instance), "geometry query"));
        }
        if (name == "Authorization")
        {
            context.Request.Headers.Authorization = value;
        }
        else if (method == "POST")
        {
            context.Request.ContentType = "application/x-www-form-urlencoded";
            context.Request.Form = new FormCollection(new Dictionary<string, StringValues> { [name] = value });
        }
        else
        {
            context.Request.QueryString = QueryString.Create(name, value);
        }
        var passed = false;
        string? forwarded = null;
        var middleware = new InputValidationMiddleware(
            next =>
            {
                passed = true;
                forwarded = method == "POST" ? next.Request.Form[name].ToString() : next.Request.Query[name].ToString();
                return Task.CompletedTask;
            },
            NullLogger<InputValidationMiddleware>.Instance,
            Options.Create(new InputValidationOptions()),
            Options.Create(new LimitsOptions
            {
                Geometry = new GeometryLimits { MaxGeometrySize = maxBytes, MaxVerticesPerGeometry = maxVertices }
            }));
        await middleware.InvokeAsync(context);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body);
        return (passed, forwarded, await reader.ReadToEndAsync());
    }
}
