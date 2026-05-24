// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.Server.Features.Infrastructure.Authentication.ClientCertificates;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Honua.Server.Tests.Infrastructure.Authentication;

public sealed class ClientCertificateEnforcementMiddlewareTests
{
    [Fact]
    public async Task InvokeAsync_OnSuccessfulValidation_ProjectsPrincipalToContextUser()
    {
        using var certificate = CreateCertificate();
        var context = CreateContext(certificate);
        var principal = CreatePrincipal("native-prod-admin");
        var validator = new StubValidator(ClientCertificateValidationResult.Success(
            principal,
            profileId: "prod-native",
            mappingId: "prod-admin",
            environmentId: "prod",
            fingerprintSha256: "FINGERPRINT",
            issuerHash: "HASH",
            daysUntilExpiry: 30,
            principalId: "native-prod-admin"));
        var nextCalled = false;
        var middleware = CreateMiddleware(
            validator,
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.Optional,
                EnvironmentId = "prod",
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Should().BeSameAs(principal);
        context.User.FindFirstValue(ClaimTypes.NameIdentifier).Should().Be("native-prod-admin");
        context.Items[ClientCertificateHttpContextItems.ValidationResult]
            .Should().BeOfType<ClientCertificateValidationResult>()
            .Which.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task InvokeAsync_RequiredForNative_WithProtectedGrpcPath_ProjectsPrincipalAndContinues()
    {
        using var certificate = CreateCertificate();
        var context = CreateContext(certificate);
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        var principal = CreatePrincipal("native-prod-operator");
        var validator = new StubValidator(ClientCertificateValidationResult.Success(
            principal,
            profileId: "prod-native",
            mappingId: "prod-operator",
            environmentId: "prod",
            fingerprintSha256: "FINGERPRINT",
            issuerHash: "HASH",
            daysUntilExpiry: 30,
            principalId: "native-prod-operator"));
        var nextCalled = false;
        var middleware = CreateMiddleware(
            validator,
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Should().BeSameAs(principal);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_ForwardedCertificate_UsesOriginalProxyPeerIpAfterForwardedHeadersRewrite()
    {
        using var certificate = CreateCertificate();
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Connection.RemoteIpAddress = IPAddress.Parse("203.0.113.40");
        context.Items[ClientCertificateHttpContextItems.OriginalProxyPeerIpAddress] = IPAddress.Parse("10.0.0.7");
        context.Request.Headers["X-Forwarded-Client-Cert"] =
            Convert.ToBase64String(certificate.Export(X509ContentType.Cert));

        var principal = CreatePrincipal("native-prod-forwarded");
        var validator = new StubValidator(ClientCertificateValidationResult.Success(
            principal,
            profileId: "prod-native",
            mappingId: "prod-forwarded",
            environmentId: "prod",
            fingerprintSha256: "FINGERPRINT",
            issuerHash: "HASH",
            daysUntilExpiry: 30,
            principalId: "native-prod-forwarded"));
        var nextCalled = false;
        var middleware = CreateMiddleware(
            validator,
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.Optional,
                EnvironmentId = "prod",
                ForwardedCertificate = new ForwardedClientCertificateOptions
                {
                    Enabled = true,
                    HeaderName = "X-Forwarded-Client-Cert",
                    Encoding = ForwardedClientCertificateEncoding.Base64Der,
                    TrustedProxyNetworks = ["10.0.0.0/24"],
                },
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Should().BeSameAs(principal);
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_RequiredForNative_WithNativeGrpcPathWithoutCertificate_Rejects()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = "application/grpc";
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Theory]
    [InlineData("application/grpc-web+proto")]
    [InlineData("application/grpc-web-text+proto")]
    [InlineData("application/grpc-web")]
    public async Task InvokeAsync_RequiredForNative_WithGrpcWebContentTypeWithoutCertificate_CallsNext(
        string contentType)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = contentType;

        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_RequiredForNative_WithXGrpcWebHeaderButNativeContentType_Rejects()
    {
        // X-Grpc-Web is a client-supplied header and is not part of the gRPC-Web spec
        // that UseGrpcWeb protocol-detects on. Treating it as sufficient on its own
        // would let any caller spoof gRPC-Web framing by adding one header to a native
        // application/grpc request and bypass required native mTLS.
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = "application/grpc";
        context.Request.Headers["X-Grpc-Web"] = "1";
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_RequiredForNative_UsesOriginalGrpcWebFlagAfterUseGrpcWebRewrite()
    {
        // UseGrpcWeb rewrites Content-Type from application/grpc-web* to application/grpc
        // before the enforcement middleware runs. The capture middleware in Program.cs
        // records the original protocol via HttpContext.Items so detection still works.
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = "application/grpc";
        context.Items[ClientCertificateHttpContextItems.OriginalGrpcWebRequest] = true;

        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.Response.StatusCode.Should().Be(StatusCodes.Status200OK);
    }

    [Fact]
    public async Task InvokeAsync_RequiredForNative_OriginalGrpcWebFalseRejectsNativeGrpc()
    {
        // Even when the capture middleware ran, native HTTP/2 gRPC requests (the flag
        // is false) must still be enforced under RequiredForNative.
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Path = "/geospatial.v1.FeatureService/Query";
        context.Request.ContentType = "application/grpc";
        context.Items[ClientCertificateHttpContextItems.OriginalGrpcWebRequest] = false;
        context.Response.Body = new MemoryStream();

        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForNative,
                EnvironmentId = "prod",
                ProtectedGrpcServices = ["geospatial.v1.FeatureService"],
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_RequiredForAdmin_WithoutCertificate_RejectsAndDoesNotCallNext()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        var context = new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
        };
        context.Request.Path = "/api/v1/admin/version";
        context.Response.Body = new MemoryStream();
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.RequiredForAdmin,
                EnvironmentId = "prod",
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeFalse();
        context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task InvokeAsync_Optional_WithoutCertificate_CallsNextWithoutSettingUser()
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Path = "/api/v1/health";
        var nextCalled = false;
        var middleware = CreateMiddleware(
            new StubValidator(null),
            new ClientCertificateAuthenticationOptions
            {
                Mode = ClientCertificateAuthenticationMode.Optional,
                EnvironmentId = "prod",
            },
            _ =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            });

        await middleware.InvokeAsync(context);

        nextCalled.Should().BeTrue();
        context.User.Identity?.IsAuthenticated.Should().NotBe(true);
    }

    private static ClientCertificateEnforcementMiddleware CreateMiddleware(
        IClientCertificateValidator validator,
        ClientCertificateAuthenticationOptions options,
        RequestDelegate next)
    {
        var monitor = new TestOptionsMonitor<ClientCertificateAuthenticationOptions>(options);
        var extractor = new ClientCertificateExtractor(monitor);
        return new ClientCertificateEnforcementMiddleware(
            next,
            extractor,
            validator,
            monitor,
            NullLogger<ClientCertificateEnforcementMiddleware>.Instance);
    }

    private static DefaultHttpContext CreateContext(X509Certificate2 certificate)
    {
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().BuildServiceProvider(),
        };
        context.Request.Path = "/";
        context.Items[ClientCertificateHttpContextItems.ExtractionResult] =
            ClientCertificateExtractionResult.Success(certificate, ClientCertificateSource.DirectTls);
        return context;
    }

    private static ClaimsPrincipal CreatePrincipal(string principalId)
    {
        var identity = new ClaimsIdentity(
            new[]
            {
                new Claim(ClaimTypes.NameIdentifier, principalId),
                new Claim(ClaimTypes.Name, principalId),
                new Claim(ClaimTypes.Role, "admin"),
                new Claim("auth_type", "client-certificate"),
            },
            ClientCertificateAuthenticationDefaults.AuthenticationScheme,
            ClaimTypes.Name,
            ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    private static X509Certificate2 CreateCertificate()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest(
            "CN=Honua Native Prod",
            key,
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new Oid("1.3.6.1.5.5.7.3.2") },
            critical: false));
        return request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(90));
    }

    private sealed class StubValidator(ClientCertificateValidationResult? result) : IClientCertificateValidator
    {
        public Task<ClientCertificateValidationResult> ValidateAsync(
            X509Certificate2? certificate,
            CancellationToken cancellationToken = default)
            => Task.FromResult(result ?? ClientCertificateValidationResult.Failure(
                ClientCertificateValidationErrorCode.UntrustedIssuer,
                "no profile configured"));
    }

    private sealed class TestOptionsMonitor<T>(T value) : IOptionsMonitor<T>
    {
        public T CurrentValue { get; } = value;

        public T Get(string? name) => CurrentValue;

        public IDisposable? OnChange(Action<T, string?> listener) => null;
    }
}
