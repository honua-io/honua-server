// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Honua.TestKit.Extensions;

/// <summary>
/// Assertion helpers for the Esri GeoServices REST error contract.
/// </summary>
/// <remarks>
/// PA-070 / PA-117 (honua-server #2418) aligned the GeoServices REST surface
/// (paths under <c>/rest/services</c> and <c>/tiles</c>) with the Esri ArcGIS
/// REST convention: operation errors are signalled with <b>HTTP 200</b> and an
/// <c>{"error":{"code":N,...}}</c> JSON body rather than a non-2xx HTTP status.
/// Modern OGC API (RFC 7807) error paths were intentionally left untouched.
/// <para>
/// These helpers assert that contract while remaining tolerant of a legacy
/// non-2xx status, so they keep catching real regressions (a plain success body
/// where an error is expected) without re-encoding the pre-parity status codes.
/// This mirrors the Python helper
/// <c>tests/python/shared/geoservices.py::assert_geoservices_error</c>.
/// </para>
/// </remarks>
public static class GeoServicesErrorAssertions
{
    /// <summary>
    /// Asserts a GeoServices REST/tiles error response whose body error code is
    /// <paramref name="expectedCode"/>.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <param name="expectedCode">
    /// The expected GeoServices <c>error.code</c> for a 200 body (or the HTTP
    /// status of a legacy <c>&gt;= 400</c> response). Because #2418 maps the
    /// body code 1:1 from the pre-parity HTTP status, this is simply the old
    /// asserted status code (e.g. 400, 404, 405, 413, 422, 500).
    /// </param>
    /// <param name="allowEmpty">
    /// Also accept an empty <c>204 No Content</c> (used by tile endpoints that
    /// emit an empty tile rather than an error body).
    /// </param>
    public static Task AssertGeoServicesErrorAsync(
        this HttpResponseMessage response,
        int expectedCode,
        bool allowEmpty = false)
        => response.AssertGeoServicesErrorAsync(new[] { expectedCode }, allowEmpty);

    /// <summary>
    /// Asserts a GeoServices REST/tiles error response.
    /// </summary>
    /// <param name="response">The HTTP response to inspect.</param>
    /// <param name="expectedCodes">
    /// If provided, the <c>error.code</c> of a 200 body (or the HTTP status of a
    /// legacy <c>&gt;= 400</c> response) must be one of these. When null, any
    /// error code is accepted (the response must still be an error, never a
    /// success-shaped body).
    /// </param>
    /// <param name="allowEmpty">
    /// Also accept an empty <c>204 No Content</c> (used by tile endpoints that
    /// emit an empty tile rather than an error body).
    /// </param>
    public static async Task AssertGeoServicesErrorAsync(
        this HttpResponseMessage response,
        int[]? expectedCodes = null,
        bool allowEmpty = false)
    {
        response.Should().NotBeNull();

        var status = (int)response.StatusCode;

        // Only read the body when we actually need to inspect it (2xx path), so
        // an empty/streamed error response doesn't trip us up on the legacy path.
        if (status < 400 && !(allowEmpty && status == (int)HttpStatusCode.NoContent))
        {
            var payload = await response.Content.ReadAsStringAsync();
            AssertGeoServicesError(status, payload, expectedCodes, allowEmpty);
        }
        else
        {
            AssertGeoServicesError(status, body: null, expectedCodes, allowEmpty);
        }
    }

    /// <summary>
    /// Asserts a GeoServices error for callers that have already captured the raw
    /// status code and body string (for example scale-test wrappers that don't
    /// expose the original <see cref="HttpResponseMessage"/>). Same contract as
    /// the <see cref="HttpResponseMessage"/> overloads.
    /// </summary>
    /// <param name="statusCode">The HTTP status carried by the response.</param>
    /// <param name="body">
    /// The response body text. Required (non-empty) when <paramref name="statusCode"/>
    /// is a 2xx; ignored on the legacy <c>&gt;= 400</c> and empty-204 paths.
    /// </param>
    /// <param name="expectedCodes">
    /// If provided, the <c>error.code</c> of a 200 body (or the HTTP status of a
    /// legacy <c>&gt;= 400</c> response) must be one of these.
    /// </param>
    /// <param name="allowEmpty">Also accept an empty <c>204 No Content</c>.</param>
    public static void AssertGeoServicesError(
        int statusCode,
        string? body,
        int[]? expectedCodes = null,
        bool allowEmpty = false)
    {
        if (allowEmpty && statusCode == (int)HttpStatusCode.NoContent)
        {
            return;
        }

        // Legacy transition tolerance: a non-2xx status still counts as an error.
        // Some GeoServices errors are emitted by the transport/routing layer (e.g.
        // route-not-matched 404) and never pass through
        // StandardErrorResponseFormatter, so they legitimately retain a >= 400
        // status even after the 200+body flip.
        if (statusCode >= 400)
        {
            if (expectedCodes is not null)
            {
                expectedCodes.Should().Contain(
                    statusCode,
                    "a legacy GeoServices error status must match the expected code(s)");
            }

            return;
        }

        statusCode.Should().Be(
            (int)HttpStatusCode.OK,
            "a GeoServices error must be signalled with HTTP 200 + an error body or a legacy >= 400 status, but got {0}: {1}",
            statusCode,
            Truncate(body));

        JsonElement root = default;
        var parsed = false;
        try
        {
            using var document = JsonDocument.Parse(body ?? string.Empty);
            root = document.RootElement.Clone();
            parsed = true;
        }
        catch (JsonException)
        {
            // fall through to the assertion below with parsed == false
        }

        parsed.Should().BeTrue(
            "expected a JSON GeoServices error body, but got: {0}",
            Truncate(body));

        root.ValueKind.Should().Be(
            JsonValueKind.Object,
            "a GeoServices error body must be a JSON object {{\"error\":{{...}}}}, but got: {0}",
            Truncate(body));

        root.TryGetProperty("error", out var error).Should().BeTrue(
            "a GeoServices 200 error response must carry an \"error\" object (a success-shaped body is rejected), but got: {0}",
            Truncate(body));

        error.ValueKind.Should().Be(
            JsonValueKind.Object,
            "the GeoServices \"error\" member must be an object, but got: {0}",
            Truncate(body));

        if (expectedCodes is not null)
        {
            error.TryGetProperty("code", out var codeElement).Should().BeTrue(
                "a GeoServices error object must carry a numeric \"code\", but got: {0}",
                Truncate(body));

            codeElement.ValueKind.Should().Be(
                JsonValueKind.Number,
                "the GeoServices error \"code\" must be numeric, but got: {0}",
                Truncate(body));

            expectedCodes.Should().Contain(
                codeElement.GetInt32(),
                "the GeoServices error code must match the expected code(s); body: {0}",
                Truncate(body));
        }
    }

    /// <summary>Convenience single-code overload of <see cref="AssertGeoServicesError(int, string, int[], bool)"/>.</summary>
    public static void AssertGeoServicesError(int statusCode, string? body, int expectedCode, bool allowEmpty = false)
        => AssertGeoServicesError(statusCode, body, new[] { expectedCode }, allowEmpty);

    private static string Truncate(string? value)
    {
        value ??= string.Empty;
        return value.Length <= 300 ? value : value[..300];
    }
}
