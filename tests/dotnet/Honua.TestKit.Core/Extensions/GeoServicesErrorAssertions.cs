// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Honua.TestKit.Extensions;

public static class GeoServicesErrorAssertions
{
    public static Task AssertGeoServicesErrorAsync(
        this HttpResponseMessage response,
        int expectedCode,
        bool allowEmpty = false)
        => response.AssertGeoServicesErrorCoreAsync(new[] { expectedCode }, allowEmpty);

    public static Task AssertGeoServicesErrorAsync(
        this HttpResponseMessage response,
        params int[] expectedCodes)
        => response.AssertGeoServicesErrorCoreAsync(
            expectedCodes is { Length: > 0 } ? expectedCodes : null,
            allowEmpty: false);

    private static async Task AssertGeoServicesErrorCoreAsync(
        this HttpResponseMessage response,
        int[]? expectedCodes,
        bool allowEmpty)
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

    public static void AssertGeoServicesError(int statusCode, string? body, int expectedCode, bool allowEmpty = false)
        => AssertGeoServicesError(statusCode, body, new[] { expectedCode }, allowEmpty);

    private static string Truncate(string? value)
    {
        value ??= string.Empty;
        return value.Length <= 300 ? value : value[..300];
    }
}
