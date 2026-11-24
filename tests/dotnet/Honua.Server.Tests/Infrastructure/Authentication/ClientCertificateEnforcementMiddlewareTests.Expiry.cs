// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Encodings.Web;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed partial class ClientCertificateEnforcementMiddlewareTests
{
    [UnitTest]
    public async Task Authenticate_CertificateExpiryBoundsTheAuthenticationTicket()
    {
        using var certificate = CreateCertificate();
        var context = CreateContext(certificate);
        var validator = new StubValidator(ClientCertificateValidationResult.Success(
            CreatePrincipal("certificate-user"), "profile", "mapping", "prod", "fingerprint", "issuer", 30, "certificate-user"));
        var options = new TestOptionsMonitor<ClientCertificateAuthenticationOptions>(new()
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod"
        });
        var handler = new ClientCertificateAuthenticationHandler(
            new TestOptionsMonitor<AuthenticationSchemeOptions>(new()),
            NullLoggerFactory.Instance,
            UrlEncoder.Default,
            new ClientCertificateAuthenticationDependencies(new ClientCertificateExtractor(options), validator, options));
        await handler.InitializeAsync(new AuthenticationScheme(
            ClientCertificateAuthenticationDefaults.AuthenticationScheme, null, typeof(ClientCertificateAuthenticationHandler)), context);

        var result = await handler.AuthenticateAsync();

        result.Succeeded.Should().BeTrue();
        result.Properties!.ExpiresUtc.Should().Be(new DateTimeOffset(certificate.NotAfter.ToUniversalTime()));
    }

    [UnitTest]
    public async Task InvokeAsync_MiddlewareAdmittedStreamRetainsCertificateExpiry()
    {
        using var certificate = CreateCertificate(DateTimeOffset.UtcNow.AddSeconds(-1));
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddSingleton<LiveStreamSecurityRegistry>();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ClientCertificateAuthenticationHandler>(
            ClientCertificateAuthenticationDefaults.AuthenticationScheme, _ => { });
        await using var services = registrations.BuildServiceProvider();
        var context = CreateContext(certificate);
        context.RequestServices = services;
        var validator = new StubValidator(ClientCertificateValidationResult.Success(
            CreatePrincipal("certificate-user"), "profile", "mapping", "prod", "fingerprint", "issuer", 0, "certificate-user"));
        var middleware = CreateMiddleware(validator, new ClientCertificateAuthenticationOptions
        {
            Mode = ClientCertificateAuthenticationMode.Optional,
            EnvironmentId = "prod"
        }, async current =>
        {
            var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(current), async _ =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, current.RequestAborted);
                return Results.Empty;
            });
            result.Should().BeAssignableTo<IStatusCodeHttpResult>().Which.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        });

        await middleware.InvokeAsync(context).WaitAsync(TimeSpan.FromSeconds(4));
    }
}
