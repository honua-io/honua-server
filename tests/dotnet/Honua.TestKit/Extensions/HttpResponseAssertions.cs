// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;

namespace Honua.TestKit.Extensions;

/// <summary>
/// FluentAssertions extensions for HTTP responses.
/// </summary>
public static class HttpResponseAssertions
{
    /// <summary>
    /// Asserts that the HTTP response has a 200 OK status code.
    /// </summary>
    public static void Be200Ok(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the response should be successful");
    }

    /// <summary>
    /// Asserts that the HTTP response has a 201 Created status code.
    /// </summary>
    public static void Be201Created(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the response should indicate creation");
    }

    /// <summary>
    /// Asserts that the HTTP response has a 204 No Content status code.
    /// </summary>
    public static void Be204NoContent(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the response should have no content");
    }

    /// <summary>
    /// Asserts that the HTTP response has a 400 Bad Request status code.
    /// </summary>
    public static void Be400BadRequest(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the request was invalid");
    }

    /// <summary>
    /// Asserts that the HTTP response has a 404 Not Found status code.
    /// </summary>
    public static void Be404NotFound(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the resource should not exist");
    }

    /// <summary>
    /// Asserts that the HTTP response has a 500 Internal Server Error status code.
    /// </summary>
    public static void Be500InternalServerError(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the server encountered an error");
    }

    /// <summary>
    /// Asserts that a GeoServices response signals an error through the Esri error contract:
    /// HTTP 200 OK with the failing code carried in the JSON body as
    /// <c>{"error":{"code":N,...}}</c>.
    /// </summary>
    /// <remarks>
    /// PA-070/PA-117: Esri GeoServices REST responses (including errors) always use HTTP 200 OK;
    /// the error is signalled exclusively through the JSON body error code, never the HTTP status.
    /// </remarks>
    /// <param name="response">The HTTP response returned by a GeoServices endpoint.</param>
    /// <param name="expectedCode">The error code expected in the JSON body (for example 400, 403, 404).</param>
    public static async Task ShouldBeGeoServicesError(this HttpResponseMessage response, int expectedCode)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(
            HttpStatusCode.OK,
            "GeoServices signals errors through the JSON body, not the HTTP status");

        var content = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
        using var document = JsonDocument.Parse(content);
        var code = document.RootElement.GetProperty("error").GetProperty("code").GetInt32();
        code.Should().Be(
            expectedCode,
            "the GeoServices error body should carry the intended error code");
    }

    /// <summary>
    /// Asserts that the HTTP response is successful (2xx status code).
    /// </summary>
    public static void BeSuccessful(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.IsSuccessStatusCode.Should().BeTrue("the response should be successful (2xx)");
    }

    /// <summary>
    /// Asserts that the HTTP response has a specific status code.
    /// </summary>
    public static void HaveStatusCode(this HttpResponseMessage response, HttpStatusCode expectedStatusCode, string because = "")
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(expectedStatusCode, because);
    }

    /// <summary>
    /// Asserts that the HTTP response has a specific content type.
    /// </summary>
    public static void HaveContentType(this HttpResponseMessage response, string expectedContentType)
    {
        response.Should().NotBeNull();
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be(expectedContentType);
    }

    /// <summary>
    /// Asserts that the HTTP response contains a specific header.
    /// </summary>
    public static void HaveHeader(this HttpResponseMessage response, string headerName)
    {
        response.Should().NotBeNull();
        response.Headers.Should().Contain(h => h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Asserts that the HTTP response contains a specific header with a specific value.
    /// </summary>
    public static void HaveHeader(this HttpResponseMessage response, string headerName, string expectedValue)
    {
        response.Should().NotBeNull();
        response.Headers.Should().Contain(h => h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase));
        var headerValues = response.Headers.GetValues(headerName);
        headerValues.Should().Contain(expectedValue);
    }
}
