// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using FluentAssertions;

namespace Honua.TestKit.Extensions;

public static class HttpResponseAssertions
{
    public static void Be200Ok(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.OK, "the response should be successful");
    }

    public static void Be201Created(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.Created, "the response should indicate creation");
    }

    public static void Be204NoContent(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.NoContent, "the response should have no content");
    }

    public static void Be400BadRequest(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, "the request was invalid");
    }

    public static void Be404NotFound(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the resource should not exist");
    }

    public static void Be500InternalServerError(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(HttpStatusCode.InternalServerError, "the server encountered an error");
    }

    public static void BeSuccessful(this HttpResponseMessage response)
    {
        response.Should().NotBeNull();
        response.IsSuccessStatusCode.Should().BeTrue("the response should be successful (2xx)");
    }

    public static void HaveStatusCode(this HttpResponseMessage response, HttpStatusCode expectedStatusCode, string because = "")
    {
        response.Should().NotBeNull();
        response.StatusCode.Should().Be(expectedStatusCode, because);
    }

    public static void HaveContentType(this HttpResponseMessage response, string expectedContentType)
    {
        response.Should().NotBeNull();
        response.Content.Headers.ContentType.Should().NotBeNull();
        response.Content.Headers.ContentType!.MediaType.Should().Be(expectedContentType);
    }

    public static void HaveHeader(this HttpResponseMessage response, string headerName)
    {
        response.Should().NotBeNull();
        response.Headers.Should().Contain(h => h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase));
    }

    public static void HaveHeader(this HttpResponseMessage response, string headerName, string expectedValue)
    {
        response.Should().NotBeNull();
        response.Headers.Should().Contain(h => h.Key.Equals(headerName, StringComparison.OrdinalIgnoreCase));
        var headerValues = response.Headers.GetValues(headerName);
        headerValues.Should().Contain(expectedValue);
    }
}
