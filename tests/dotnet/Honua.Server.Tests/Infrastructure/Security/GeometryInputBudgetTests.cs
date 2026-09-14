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

    [Theory]
    [InlineData("application/json")]
    [InlineData("application/vnd.honua+json")]
    [InlineData("multipart/form-data")]
    public async Task SupportedBodyEncodings_ValidateAndPreserveCompleteGeometry(string contentType)
    {
        var geometry = DetailedPolygon();
        var result = await InvokeAsync(geometry, contentType: contentType);
        Assert.True(result.Passed, result.Body);
        Assert.Equal(geometry, result.ForwardedValue);
    }

    [Fact]
    public async Task JsonObjectGeometry_UsesTheSameParserAndBudgetAsEncodedGeometry()
    {
        var geometry = DetailedPolygon();
        var body = $"{{\"geometry\":{geometry}}}";
        var accepted = await InvokeAsync(geometry, contentType: "application/json", rawBody: body);
        Assert.True(accepted.Passed, accepted.Body);
        Assert.Equal(geometry, accepted.ForwardedValue);
        var rejected = await InvokeAsync(geometry, contentType: "application/json", rawBody: body, maxVertices: 400);
        Assert.False(rejected.Passed);
        Assert.Contains("MaxVerticesPerGeometry", rejected.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data")]
    public async Task BodyGeometry_OverridesQueryWithoutBypassingByteAndVertexBudgets(string contentType)
    {
        const string geometry = "{\"paths\":[[[1,2],[3,4]],[[5,6],[7,8]]]}";
        const string queryGeometry = "{\"x\":1,\"y\":2}";
        Assert.True((await InvokeAsync(geometry, contentType: contentType, queryGeometry: queryGeometry,
            maxBytes: Encoding.UTF8.GetByteCount(geometry), maxVertices: 4)).Passed);
        var bytes = await InvokeAsync(geometry, contentType: contentType, queryGeometry: queryGeometry,
            maxBytes: Encoding.UTF8.GetByteCount(geometry) - 1);
        Assert.False(bytes.Passed);
        Assert.Contains("MaxGeometrySize", bytes.Body, StringComparison.Ordinal);
        var vertices = await InvokeAsync(geometry, contentType: contentType, queryGeometry: queryGeometry, maxVertices: 3);
        Assert.False(vertices.Passed);
        Assert.Contains("MaxVerticesPerGeometry", vertices.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("application/json")]
    [InlineData("multipart/form-data")]
    public async Task StructuredBody_OtherParametersRetainLimitsAndInjectionChecks(string contentType)
    {
        Assert.False((await InvokeAsync(new string('a', 8193), name: "where", contentType: contentType)).Passed);
        var injection = await InvokeAsync("<script>alert(1)</script>", name: "where", contentType: contentType);
        Assert.False(injection.Passed);
        Assert.Contains("XSS", injection.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{broken")]
    [InlineData("[]")]
    [InlineData("{\"geometry\":{\"x\":1}}")]
    public async Task InvalidJsonBody_IsRejectedBeforeDispatch(string rawBody)
    {
        Assert.False((await InvokeAsync("", contentType: "application/json", rawBody: rawBody)).Passed);
    }

    [Theory]
    [InlineData("curvePaths", "[[1,0],{\"c\":[[0,1],[0,0]]}]")]
    [InlineData("curvePaths", "[[0,0],{\"b\":[[1,1],[0,1],[1,0]]}]")]
    [InlineData("curvePaths", "[[1,0],{\"a\":[[0,1],[0,0],1,0]}]")]
    [InlineData("curveRings", "[[1,0],{\"c\":[[-1,0],[0,1]]},{\"c\":[[1,0],[0,-1]]}]")]
    public async Task TrueCurves_AreAdmittedWithoutChangingTheirDefinition(string property, string part)
    {
        var geometry = $"{{\"{property}\":[{part}]}}";
        var result = await InvokeAsync(geometry);
        Assert.True(result.Passed, result.Body);
        Assert.Equal(geometry, result.ForwardedValue);
    }

    [Fact]
    public async Task TrueCurveBudget_CountsExpandedVerticesAcrossParts()
    {
        // Each fixed-sampling Bezier produces its start plus 32 generated vertices.
        const string part = "[[0,0],{\"b\":[[1,1],[0,1],[1,0]]}]";
        var geometry = $"{{\"curvePaths\":[{part},{part}]}}";
        Assert.True((await InvokeAsync(geometry, maxVertices: 66)).Passed);
        var rejected = await InvokeAsync(geometry, maxVertices: 65);
        Assert.False(rejected.Passed);
        Assert.Contains("MaxVerticesPerGeometry", rejected.Body, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("{\"curvePaths\":[[ {\"c\":[[0,1],[0,0]]} ]]}")]
    [InlineData("{\"curvePaths\":[[[0,0],{\"unsupported\":[]}]]}")]
    [InlineData("{\"curvePaths\":[[[0,0],{\"b\":[[1,1],[0,\"invalid\"],[1,0]]}]]}")]
    [InlineData("{\"curvePaths\":[[[0,0],{\"c\":[[1,1e999],[1,0]]}]]}")]
    [InlineData("{\"curvePaths\":[null]}")]
    public async Task MalformedTrueCurves_AreValidationFailures(string geometry)
    {
        Assert.False((await InvokeAsync(geometry)).Passed);
    }

    [Fact]
    public async Task StructuredBody_ObservesRequestCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            InvokeAsync(DetailedPolygon(), contentType: "application/json", cancellationToken: cancelled.Token));
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
        int maxVertices = 50_000,
        string? contentType = null,
        string? queryGeometry = null,
        string? rawBody = null,
        CancellationToken cancellationToken = default)
    {
        using var services = new ServiceCollection().AddLogging().AddOptions().BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.Path = "/rest/services/test/FeatureServer/1/query";
        context.Request.Method = method;
        context.RequestAborted = cancellationToken;
        context.Response.Body = new MemoryStream();
        if (declared)
        {
            context.SetEndpoint(new Endpoint(null, new EndpointMetadataCollection(GeoServicesQueryGeometryMetadata.Instance), "geometry query"));
        }
        if (name == "Authorization")
        {
            context.Request.Headers.Authorization = value;
        }
        else if (method == "POST" && contentType != null)
        {
            if (contentType == "multipart/form-data")
            {
                using var multipart = new MultipartFormDataContent("geometry-budget-boundary");
                multipart.Add(new StringContent(value, Encoding.UTF8), name);
                context.Request.ContentType = multipart.Headers.ContentType!.ToString();
                var bytes = await multipart.ReadAsByteArrayAsync(CancellationToken.None);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
            }
            else
            {
                context.Request.ContentType = contentType;
                var json = rawBody ?? JsonSerializer.Serialize(new Dictionary<string, string> { [name] = value });
                var bytes = Encoding.UTF8.GetBytes(json);
                context.Request.Body = new MemoryStream(bytes);
                context.Request.ContentLength = bytes.Length;
            }
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
        if (queryGeometry != null)
        {
            context.Request.QueryString = QueryString.Create("geometry", queryGeometry);
        }
        var passed = false;
        string? forwarded = null;
        var middleware = new InputValidationMiddleware(
            async next =>
            {
                passed = true;
                if (method == "POST")
                {
                    var parsed = await GeoServicesRequestValueHelpers.TryReadRequestValuesAsync(next.Request, next.RequestAborted);
                    Assert.Null(parsed.Error);
                    forwarded = parsed.Values?[name].ToString();
                }
                else
                {
                    forwarded = next.Request.Query[name].ToString();
                }
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
        return (passed, forwarded, await reader.ReadToEndAsync(CancellationToken.None));
    }
}
