// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Edit.Receipts;
using Honua.Core.Features.Infrastructure.Backpressure;
using Honua.Infrastructure.Models;
using Honua.Infrastructure.Security;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Certification;

[Collection("RateLimitingCapacity")]
public sealed partial class SdkHardeningParityContractTests
{
    private const string SensitiveSecret = "SuperSecretDbPass99!";
    private const string SensitiveInternalPath = "/srv/honua/src/InternalDb.cs:line 42";
    private const string TrustedProxyNetwork = "10.42.0.0/16";
    private const string TrustedProxyPeer = "10.42.1.15";
    private const string UntrustedPeer = "192.0.2.99";

    [UnitTheory]
    [InlineData(StatusCodes.Status400BadRequest, "Bad Request")]
    [InlineData(StatusCodes.Status401Unauthorized, "Unauthorized")]
    [InlineData(StatusCodes.Status403Forbidden, "Forbidden")]
    [InlineData(StatusCodes.Status404NotFound, "Not Found")]
    [InlineData(StatusCodes.Status409Conflict, "Conflict")]
    [InlineData(StatusCodes.Status429TooManyRequests, "Too Many Requests")]
    [InlineData(StatusCodes.Status503ServiceUnavailable, "Service Unavailable")]
    public async Task StandardErrorResponse_SdkErrorCodes_ReturnsSanitizedMachineReadableEnvelope(
        int statusCode,
        string expectedTitle)
    {
        using var scope = CreateHttpScope("/api/v1/admin/layers");
        var result = CreateStandardErrorResult(scope.Context, statusCode);

        await result.ExecuteAsync(scope.Context);

        scope.Context.Response.StatusCode.Should().Be(statusCode);
        scope.Context.Response.ContentType.Should().StartWith("application/problem+json");

        var body = ReadResponseBody(scope.Context);
        AssertNoInternalLeakage(body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(statusCode);
        root.GetProperty("title").GetString().Should().Be(expectedTitle);
        root.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("type").GetString().Should().Be("https://honua.io/problems/admin");
        root.GetProperty("instance").GetString().Should().Be("/api/v1/admin/layers");
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
        root.GetProperty("timestamp").GetString().Should().NotBeNullOrWhiteSpace();

        AssertStatusSpecificHeaders(scope.Context, root, statusCode);
    }

    [UnitTheory]
    [InlineData(StatusCodes.Status400BadRequest)]
    [InlineData(StatusCodes.Status401Unauthorized)]
    [InlineData(StatusCodes.Status403Forbidden)]
    [InlineData(StatusCodes.Status404NotFound)]
    [InlineData(StatusCodes.Status409Conflict)]
    [InlineData(StatusCodes.Status503ServiceUnavailable)]
    [InlineData(StatusCodes.Status500InternalServerError)]
    public async Task StandardErrorResponse_FromException_NeverLeaksStackTraceOrInternalPaths(
        int expectedStatus)
    {
        using var scope = CreateHttpScope("/ogc/features/collections/parcels/items");
        var exception = CreateThrownException(expectedStatus);

        var model = StandardErrorResponse.FromException(exception, includeDebugDetails: false);
        model.StatusCode.Should().Be(expectedStatus);
        model.DebugInfo.Should().BeNull();

        var result = StandardErrorHelpers.CreateFromException(
            scope.Context,
            exception,
            includeDebugDetails: false);
        await result.ExecuteAsync(scope.Context);

        scope.Context.Response.StatusCode.Should().Be(expectedStatus);
        var body = ReadResponseBody(scope.Context);
        AssertNoInternalLeakage(body);

        using var document = JsonDocument.Parse(body);
        document.RootElement.GetProperty("status").GetInt32().Should().Be(expectedStatus);
        document.RootElement.GetProperty("title").GetString().Should().NotBeNullOrWhiteSpace();
        document.RootElement.GetProperty("detail").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public async Task RateLimitingMiddleware_OnQuotaExhaustion_Returns429WithNumericRetryAfterAndProblemPayload()
    {
        var middleware = CreateRateLimitingMiddleware(limit: 1);
        var subject = $"sdk-parity-{Guid.NewGuid():N}";

        using var firstScope = CreateHttpScope("/ogc/features/collections/parcels/items", "198.51.100.181", subject);
        await middleware.InvokeAsync(firstScope.Context);
        firstScope.Context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);

        using var secondScope = CreateHttpScope("/ogc/features/collections/parcels/items", "198.51.100.181", subject);
        await middleware.InvokeAsync(secondScope.Context);

        secondScope.Context.Response.StatusCode.Should().Be(StatusCodes.Status429TooManyRequests);
        secondScope.Context.Response.Headers["X-RateLimit-Remaining"].ToString().Should().Be("0");
        secondScope.Context.Response.Headers["Honua-Retryable"].ToString().Should().Be("true");
        secondScope.Context.Response.Headers["X-Correlation-ID"].ToString().Should().NotBeNullOrWhiteSpace();

        var retryAfter = secondScope.Context.Response.Headers.RetryAfter.ToString();
        int.TryParse(retryAfter, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            .Should().BeTrue("429 responses must emit a numeric Retry-After delay in seconds");
        seconds.Should().BeInRange(0, 60);

        var body = ReadResponseBody(secondScope.Context);
        AssertNoInternalLeakage(body);

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(StatusCodes.Status429TooManyRequests);
        root.GetProperty("code").GetString().Should().Be(BackpressureMetadata.RateLimitExceededCode);
        root.GetProperty("retryable").GetBoolean().Should().BeTrue();
        root.GetProperty("retryAfterSeconds").GetInt32().Should().BeInRange(0, 60);
        root.GetProperty("correlationId").GetString().Should().NotBeNullOrWhiteSpace();
    }

    [UnitTest]
    public async Task IdempotencyContract_ScopesByTenantAndActor_AndReplaysSingleReceipt()
    {
        using var tenantAScope = CreateHttpScope("/api/v1/admin/import/upload-url", "198.51.100.182", "sdk-actor-a");
        using var tenantBScope = CreateHttpScope("/api/v1/admin/import/upload-url", "198.51.100.182", "sdk-actor-b");

        var callerA = IdempotencyCaller.Resolve(tenantAScope.Context);
        var callerB = IdempotencyCaller.Resolve(tenantBScope.Context);
        callerA.Should().NotBeNull();
        callerB.Should().NotBeNull();
        callerA!.Fingerprint().Should().NotBe(callerB!.Fingerprint());

        var store = new InMemoryIdempotencyReservationStore(TimeProvider.System);
        var scopeKey = $"{callerA.Fingerprint()}:edit-key-1";
        var reservation = await store.TryReserveAsync(scopeKey, TimeSpan.FromMinutes(1), CancellationToken.None);
        reservation.Should().NotBeNull();

        var duplicate = await store.TryReserveAsync(scopeKey, TimeSpan.FromMinutes(1), CancellationToken.None);
        duplicate.Should().BeNull("a live reservation must block concurrent duplicate execution");

        var receiptBytes = Encoding.UTF8.GetBytes("{\"accepted\":true}");
        var completed = await store.CompleteAsync(
            new IdempotencyReceiptWrite(reservation!.Value, receiptBytes),
            TimeSpan.FromMinutes(5),
            CancellationToken.None);
        completed.Should().BeTrue();

        var replayed = await store.FindReceiptAsync(scopeKey, CancellationToken.None);
        replayed.Should().Equal(receiptBytes);
    }

    [UnitTest]
    public async Task ForwardedHeaders_FromTrustedProxy_RewritesToValidHttpsAbsoluteUrl()
    {
        var probe = new ForwardedProbeRequest
        {
            PeerAddress = TrustedProxyPeer,
            ForwardedProto = "https",
            ForwardedHost = "gis.example.org",
            ForwardedFor = "203.0.113.42",
        };

        var observed = await SendForwardedRequestAsync(probe);

        observed.Scheme.Should().Be("https");
        observed.Host.Should().Be("gis.example.org");
        observed.RemoteAddress.Should().Be("203.0.113.42");
        Uri.TryCreate(observed.AbsoluteUrl, UriKind.Absolute, out var parsedUri).Should().BeTrue();
        parsedUri!.Scheme.Should().Be(Uri.UriSchemeHttps);
        parsedUri.Host.Should().Be("gis.example.org");
        parsedUri.AbsolutePath.Should().Be("/ogc/features/collections");
        observed.ResolvedBaseUrl.Should().NotContain("evil.attacker.test");
    }

    [UnitTest]
    public async Task ForwardedHeaders_FromUntrustedPeer_IgnoresSpoofedProtoAndHostHeaders()
    {
        var probe = new ForwardedProbeRequest
        {
            PeerAddress = UntrustedPeer,
            ForwardedProto = "https",
            ForwardedHost = "evil.attacker.test",
            ForwardedFor = "203.0.113.42",
        };

        var observed = await SendForwardedRequestAsync(probe);

        observed.Scheme.Should().Be("http");
        observed.Host.Should().Be("localhost");
        observed.RemoteAddress.Should().Be(UntrustedPeer);
        observed.AbsoluteUrl.Should().Be("http://localhost/ogc/features/collections");
        observed.AbsoluteUrl.Should().NotContain("evil.attacker.test");
        observed.ResolvedBaseUrl.Should().NotContain("evil.attacker.test");
    }
}
