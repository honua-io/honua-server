// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Http;

namespace Honua.Server.Tests.Features.Protocols.GeoServices;

/// <summary>
/// Shared assertion helpers for GeoServices REST error responses.
/// </summary>
/// <remarks>
/// GeoServices REST errors are emitted as HTTP <c>200</c> with the real status
/// carried in the JSON body <c>{"error":{"code":N}}</c> per the Esri GeoServices
/// REST spec (see <c>StandardErrorResponseFormatter.FormatGeoServicesError</c>,
/// PA-070/PA-117). Asserting on <c>context.Response.StatusCode</c> therefore reads
/// the transport <c>200</c>, never the error code — a harness anti-pattern that
/// fails against correct product behavior. Handler unit tests must read the error
/// code from the body instead; this helper centralizes that so future GeoServices
/// handler tests use it by default.
/// </remarks>
public static class GeoServicesErrorAssertions
{
    /// <summary>
    /// Executes an error <see cref="IResult"/> against the supplied context and
    /// asserts the GeoServices error code carried in the response body. Asserts
    /// the transport status is HTTP <c>200</c> and that <c>error.code</c> matches
    /// one of <paramref name="expectedCodes"/>.
    /// </summary>
    /// <param name="context">
    /// The HTTP context the result is executed against. Its
    /// <see cref="HttpResponse.Body"/> must be a seekable stream (for example a
    /// <see cref="System.IO.MemoryStream"/>) so the body can be re-read.
    /// </param>
    /// <param name="result">The error result returned by the handler under test.</param>
    /// <param name="expectedCodes">
    /// One or more acceptable GeoServices error codes (HTTP status values such as
    /// <c>404</c> or <c>400</c>) carried in the JSON body. Note that
    /// <c>GeoServicesErrorCodes.FromHttpStatusCode</c> has no <c>501</c> branch, so
    /// <c>501 Not Implemented</c> collapses to the <c>500</c> default; statuses with
    /// explicit branches (<c>400</c>/<c>401</c>/<c>403</c>/<c>404</c>/<c>499</c>/<c>503</c>…)
    /// map to themselves.
    /// </param>
    public static async Task AssertGeoServicesErrorAsync(HttpContext context, IResult result, params int[] expectedCodes)
    {
        if (expectedCodes is null || expectedCodes.Length == 0)
        {
            throw new ArgumentException("At least one expected error code is required.", nameof(expectedCodes));
        }

        await result.ExecuteAsync(context);

        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
        context.Response.Body.Position = 0;
        using var reader = new StreamReader(context.Response.Body, leaveOpen: true);
        var body = await reader.ReadToEndAsync();

        JsonDocument? json = null;
        try
        {
            json = JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            // Fall through to the envelope assertion below with json == null.
        }

        using (json)
        {
            var hasErrorCode = json is not null
                && json.RootElement.ValueKind == JsonValueKind.Object
                && json.RootElement.TryGetProperty("error", out var error)
                && error.TryGetProperty("code", out _);
            hasErrorCode.Should().BeTrue(
                $"the response body should be a GeoServices error envelope {{\"error\":{{\"code\":N}}}} but was: {(body.Length > 300 ? body[..300] + "…" : body)}");

            json!.RootElement.GetProperty("error").GetProperty("code").GetInt32()
                .Should().BeOneOf(expectedCodes);
        }
    }
}
