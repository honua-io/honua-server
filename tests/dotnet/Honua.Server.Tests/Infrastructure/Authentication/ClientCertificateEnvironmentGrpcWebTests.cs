// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Security.Cryptography.X509Certificates;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class ClientCertificateEnvironmentGrpcWebTests
{
    [UnitTheory]
    [InlineData("application/grpc-web+proto")]
    [InlineData("application/grpc-web-text+proto")]
    public async Task RequiredForEnvironment_GrpcWebWithoutCertificate_IsRefused(string contentType)
    {
        var context = GrpcContext(contentType);

        await AssertRefusedAsync(context);
    }

    [UnitTest]
    public async Task RequiredForEnvironment_GrpcWebAfterContentTypeRewrite_IsRefused()
    {
        var context = GrpcContext("application/grpc");
        context.Items[ClientCertificateHttpContextItems.OriginalGrpcWebRequest] = true;

        await AssertRefusedAsync(context);
    }

    private static DefaultHttpContext GrpcContext(string contentType)
    {
        var context = new DefaultHttpContext { RequestServices = new ServiceCollection().AddLogging().BuildServiceProvider() };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = contentType;
        context.Response.Body = new MemoryStream();
        return context;
    }

    private static async Task AssertRefusedAsync(DefaultHttpContext context)
    {
        var nextCalled = false;
        var options = new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.RequiredForEnvironment,
            EnvironmentId = "prod",
            ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
        };
        var monitor = new ClientCertificateValidationTests.TestOptionsMonitor<ClientCertificateAuthenticationOptions>(options);
        var middleware = new ClientCertificateEnforcementMiddleware(
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            },
            new ClientCertificateExtractor(monitor),
            new MissingCertificateValidator(),
            monitor,
            NullLogger<ClientCertificateEnforcementMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        Assert.False(nextCalled);
        Assert.Equal(StatusCodes.Status401Unauthorized, context.Response.StatusCode);
    }

    private sealed class MissingCertificateValidator : IClientCertificateValidator
    {
        public Task<ClientCertificateValidationResult> ValidateAsync(
            X509Certificate2? certificate,
            CancellationToken cancellationToken = default)
            => Task.FromResult(ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.UntrustedIssuer,
                "no profile configured"));
    }
}
