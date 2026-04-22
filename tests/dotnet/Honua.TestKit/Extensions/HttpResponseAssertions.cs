// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
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
