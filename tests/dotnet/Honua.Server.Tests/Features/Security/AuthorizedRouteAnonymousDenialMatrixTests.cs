// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Routing.Patterns;

namespace Honua.Server.Tests.Features.Security;

/// <summary>
/// Anonymous column of the route authorization matrix. Every deployed route that carries endpoint
/// authorization metadata (as classified by <see cref="RouteAuthorizationInventoryGuardTests"/>) is
/// requested with no credentials against a host with the development auth bypass disabled, and must
/// answer <c>401 Unauthorized</c> (or, on ArcGIS REST/Portal sharing routes, the protocol-native
/// HTTP 200 <c>error.code 499</c> "token required" envelope). The denominator is the live endpoint data source, so a new protected
/// route is covered automatically, method-agnostic routes are requested with both GET and POST, and a
/// route whose metadata is not actually enforced fails here.
/// </summary>
[Collection("Database")]
[SecurityTest]
[Protocol(TestProtocols.TestQuality)]
[Operation(Operations.Security)]
public sealed class AuthorizedRouteAnonymousDenialMatrixTests : IAsyncLifetime
{
    private const string GrpcContentType = "application/grpc";
    private const string AdminPassword = "anonymous-denial-matrix-key";

    private readonly WebAppFixture _fixture = new WebAppFixture()
        .ConfigureWebHost(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("HONUA_DEV_AUTH", "false");
            builder.UseSetting("HONUA_ADMIN_PASSWORD", AdminPassword);
        });

    /// <inheritdoc />
    public Task InitializeAsync() => _fixture.InitializeAsync();

    /// <inheritdoc />
    public Task DisposeAsync() => _fixture.DisposeAsync();

    // Cross-cutting: the denominator is every endpoint-authorized route in the live route table. The
    // [Endpoint] entries name representative routes this test drives that have no other anonymous proof:
    // the SignalR admin hub (method-agnostic) and the introspection route (ArcGIS 499 envelope).
    [IntegrationTest]
    [Endpoint("GET /hubs/admin")]
    [Endpoint("POST /sharing/rest/oauth2/introspect")]
    public async Task EveryAuthorizedRoute_AnonymousCaller_Returns401()
    {
        // Method-agnostic endpoints (no HttpMethodMetadata: SignalR hub transport and negotiate, /metrics)
        // are expanded into concrete GET and POST requests, not skipped. The gRPC unimplemented-method
        // catch-all only matches gRPC requests, so it is sent as an application/grpc POST.
        var routes = RouteAuthorizationInventoryGuardTests.ClassifyDeployedRoutes(_fixture.Services)
            .Where(entry => entry.Classification == "authorized")
            .SelectMany(entry => entry.Method != "ANY"
                ? new[] { entry }
                : IsGrpcUnimplementedCatchAll(entry.Pattern)
                    ? new[] { entry with { Method = "POST", AcceptsContentType = GrpcContentType } }
                    : new[] { entry with { Method = "GET" }, entry with { Method = "POST" } })
            .Distinct()
            .ToArray();
        routes.Should().NotBeEmpty();

        using var client = _fixture.CreateClient(allowAutoRedirect: false);
        var failures = new List<string>();
        foreach (var route in routes)
        {
            if (!TryBuildPath(route.Pattern, out var path, out var unsupported))
            {
                failures.Add($"{route.Method} {route.Pattern}: unsupported route parameter '{unsupported}', extend SampleValue");
                continue;
            }

            using var request = new HttpRequestMessage(new HttpMethod(route.Method), path);
            if (route.Method is "POST" or "PUT" or "PATCH")
            {
                request.Content = CreateBody(route.AcceptsContentType);
            }

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (response.StatusCode != HttpStatusCode.Unauthorized && !IsGeoServicesTokenRequired(route.Pattern, response, body))
            {
                failures.Add($"{route.Method} {route.Pattern} -> {(int)response.StatusCode} ({route.Policies}) {body[..Math.Min(body.Length, 160)]}");
            }
        }

        failures.Should().BeEmpty(
            "every route carrying authorization metadata must deny an anonymous caller with 401 ({0} routes checked):\n{1}",
            routes.Length,
            string.Join('\n', failures));
    }

    /// <summary>
    /// ArcGIS REST and Portal sharing surfaces report authentication failures in their native wire envelope:
    /// HTTP 200 carrying <c>{"error":{"code":499,...}}</c> ("token required"). That is the protocol's 401.
    /// </summary>
    private static bool IsGeoServicesTokenRequired(string pattern, HttpResponseMessage response, string body)
    {
        if (response.StatusCode != HttpStatusCode.OK
            || !(pattern.StartsWith("/sharing/rest/", StringComparison.Ordinal) || pattern.StartsWith("/rest/", StringComparison.Ordinal)))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out var code)
                && code.ValueKind == JsonValueKind.Number
                && code.GetInt32() == 499;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static bool IsGrpcUnimplementedCatchAll(string pattern)
        => pattern.Contains(":grpcunimplemented}", StringComparison.Ordinal);

    /// <summary>
    /// Builds a request body in the content type the endpoint accepts, so routing selects the endpoint and
    /// authorization runs instead of a 415 from content-type matching.
    /// </summary>
    private static HttpContent CreateBody(string? acceptsContentType)
    {
        if (acceptsContentType is not null && acceptsContentType.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
        {
            return new MultipartFormDataContent { { new StringContent("matrix"), "field" } };
        }

        if (string.Equals(acceptsContentType, GrpcContentType, StringComparison.Ordinal))
        {
            var grpc = new ByteArrayContent([0, 0, 0, 0, 0]);
            grpc.Headers.ContentType = new MediaTypeHeaderValue(GrpcContentType);
            return grpc;
        }

        if (acceptsContentType is not null && !acceptsContentType.Contains('*', StringComparison.Ordinal))
        {
            return new StringContent("{}", Encoding.UTF8, MediaTypeHeaderValue.Parse(acceptsContentType));
        }

        return new StringContent("{}", Encoding.UTF8, "application/json");
    }

    private static bool TryBuildPath(string pattern, out string path, out string? unsupported)
    {
        var builder = new StringBuilder();
        unsupported = null;
        foreach (var segment in RoutePatternFactory.Parse(pattern).PathSegments)
        {
            builder.Append('/');
            foreach (var part in segment.Parts)
            {
                switch (part)
                {
                    case RoutePatternLiteralPart literal:
                        builder.Append(Uri.EscapeDataString(literal.Content));
                        break;
                    case RoutePatternSeparatorPart separator:
                        builder.Append(separator.Content);
                        break;
                    case RoutePatternParameterPart parameter:
                        var value = SampleValue(parameter);
                        if (value is null)
                        {
                            unsupported = parameter.Name;
                            path = string.Empty;
                            return false;
                        }

                        builder.Append(value);
                        break;
                }
            }
        }

        path = builder.Length == 0 ? "/" : builder.ToString();
        return true;
    }

    private static string? SampleValue(RoutePatternParameterPart parameter)
    {
        string? value = "matrix";
        foreach (var policy in parameter.ParameterPolicies)
        {
            var constraint = (policy.Content ?? string.Empty).ToLowerInvariant();
            value = constraint switch
            {
                "int" or "long" or "min(0)" or "min(1)" => "1",
                "guid" => Guid.Parse("00000000-0000-0000-0000-000000000001").ToString("D", CultureInfo.InvariantCulture),
                "apiversion" => "1",
                "bool" => "true",
                "alpha" or "nonfile" or "grpcunimplemented" => "matrix",
                _ when constraint.StartsWith("minlength", StringComparison.Ordinal) || constraint.StartsWith("maxlength", StringComparison.Ordinal) => value,
                _ => null,
            };

            if (value is null)
            {
                return null;
            }
        }

        return value;
    }
}
