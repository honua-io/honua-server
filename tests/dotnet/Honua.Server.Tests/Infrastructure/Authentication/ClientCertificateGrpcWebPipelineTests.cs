// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Full-pipeline regression tests for the interaction between <c>UseGrpcWeb</c> and
/// <c>UseHonuaClientCertificateAuthentication</c>. <c>UseGrpcWeb</c> rewrites
/// <c>Content-Type</c> from <c>application/grpc-web*</c> to <c>application/grpc</c>
/// before the client-certificate middleware runs, so the capture middleware in
/// <c>Program.cs</c> must record the original protocol via <c>HttpContext.Items</c>
/// for required-native mTLS to correctly skip browser/gRPC-Web callers.
/// </summary>
[Protocol(TestProtocols.Grpc)]
[Operation(Operations.Security)]
public sealed class ClientCertificateGrpcWebPipelineTests
{
    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task RequiredForNative_WithGrpcWebContentType_DoesNotReturn401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/geospatial.v1.SpecService/PlanSpec");
        request.Content = new ByteArrayContent([])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/grpc-web+proto") }
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "the capture middleware should preserve the original gRPC-Web protocol indicator so the client-certificate middleware skips enforcement");
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task RequiredForNative_WithGrpcWebXGrpcWebHeader_DoesNotReturn401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/geospatial.v1.SpecService/PlanSpec");
        request.Headers.TryAddWithoutValidation("X-Grpc-Web", "1");
        request.Content = new ByteArrayContent([])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/grpc") }
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized,
            "X-Grpc-Web alone must mark the request as gRPC-Web for the capture middleware");
    }

    [IntegrationTest]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    public async Task RequiredForNative_WithNativeGrpcContentType_Returns401()
    {
        using var factory = CreateFactory();
        using var client = factory.CreateClient();

        using var request = new HttpRequestMessage(HttpMethod.Post, "/geospatial.v1.SpecService/PlanSpec");
        request.Content = new ByteArrayContent([])
        {
            Headers = { ContentType = new MediaTypeHeaderValue("application/grpc") }
        };

        using var response = await client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized,
            "native HTTP/2 gRPC must still be enforced under RequiredForNative when no certificate is presented");
    }

    private static WebApplicationFactory<Program> CreateFactory()
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("Authentication:ClientCertificates:Mode", "RequiredForNative");
                builder.UseSetting("Authentication:ClientCertificates:EnvironmentId", "test");
                builder.UseSetting("Authentication:ClientCertificates:ProtectedGrpcServices:0", "geospatial.v1.SpecService");
            });
    }
}
