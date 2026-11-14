// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Security;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using FluentAssertions;
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Honua.Server.Tests.Features.OperationsToolset;

// An approved operation replayed to this server over the TLS loopback must succeed when Kestrel serves a
// certificate the host does not trust or that does not name the request host, and must trust nothing else.
[Trait("Tier", "Fast")]
public sealed class AdminOperationLoopbackTlsTests
{
    [Fact]
    public void Validate_WithoutPin_RequiresDefaultValidation()
    {
        using var certificate = CreateSelfSigned("CN=other");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:8443/");

        AdminOperationLoopbackTls.Validate(request, certificate, SslPolicyErrors.None).Should().BeTrue();
        AdminOperationLoopbackTls.Validate(request, certificate, SslPolicyErrors.RemoteCertificateChainErrors).Should().BeFalse();
        AdminOperationLoopbackTls.Validate(request, certificate, SslPolicyErrors.RemoteCertificateNameMismatch).Should().BeFalse();
    }

    [Fact]
    public void Validate_WithPin_AcceptsOnlyThePinnedCertificate()
    {
        using var served = CreateSelfSigned("CN=served");
        using var impostor = CreateSelfSigned("CN=served");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:8443/");
        request.Options.Set(AdminOperationLoopbackTls.ExpectedServerCertificateHash, served.GetCertHashString(HashAlgorithmName.SHA256));
        const SslPolicyErrors untrusted = SslPolicyErrors.RemoteCertificateChainErrors | SslPolicyErrors.RemoteCertificateNameMismatch;

        AdminOperationLoopbackTls.Validate(request, served, untrusted).Should().BeTrue();
        AdminOperationLoopbackTls.Validate(request, impostor, SslPolicyErrors.None).Should().BeFalse(
            "a pinned replay trusts the served certificate only, even if another certificate chains to a trusted root");
        AdminOperationLoopbackTls.Validate(request, null, SslPolicyErrors.RemoteCertificateNotAvailable).Should().BeFalse();
    }

    [Fact]
    public void Validate_MalformedPin_FailsClosed()
    {
        using var certificate = CreateSelfSigned("CN=served");
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://127.0.0.1:8443/");
        request.Options.Set(AdminOperationLoopbackTls.ExpectedServerCertificateHash, "not-hex");

        AdminOperationLoopbackTls.Validate(request, certificate, SslPolicyErrors.None).Should().BeFalse();
    }

    [Fact]
    public void PinServedCertificate_PlainHttpReplay_IsNotPinned()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "http://127.0.0.1:8080/");

        AdminOperationLoopbackTls.PinServedCertificate(request, new DefaultHttpContext());

        request.Options.TryGetValue(AdminOperationLoopbackTls.ExpectedServerCertificateHash, out _).Should().BeFalse();
    }

    [Fact]
    public async Task DirectKestrelTls_ReplayPinnedToServedCertificate_Succeeds_AndUnpinnedReplayFails()
    {
        // The certificate is self-signed and names only "served.invalid", so neither the chain nor the
        // request host ("honua.example") validates by default: the shape of a direct-TLS deployment with a
        // private-CA or self-signed certificate.
        using var certificate = CreateSelfSigned("CN=served.invalid");
        await using var app = await StartReplayServerAsync(certificate);
        try
        {
            var address = new Uri(app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single());
            using var callerHandler = new SocketsHttpHandler();
            var servedHash = certificate.GetCertHashString(HashAlgorithmName.SHA256);
            callerHandler.SslOptions.RemoteCertificateValidationCallback = (_, presented, _, _) =>
                presented is not null && presented.GetCertHashString(HashAlgorithmName.SHA256) == servedHash;
            using var caller = new HttpClient(callerHandler);

            var (pinnedStatus, pinnedBody) = await RequestReplayAsync(caller, address, pinned: true);
            pinnedStatus.Should().Be(HttpStatusCode.OK);
            pinnedBody.Should().Be("replayed");
            var (unpinnedStatus, _) = await RequestReplayAsync(caller, address, pinned: false);
            unpinnedStatus.Should().Be(HttpStatusCode.BadGateway,
                "without the pin, default validation rejects the server's own untrusted certificate");
        }
        finally
        {
            await app.StopAsync();
        }
    }

    private static async Task<WebApplication> StartReplayServerAsync(X509Certificate2 certificate)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrel(options => options.Listen(IPAddress.Loopback, 0, listen => listen.UseHttps(certificate)));
        var app = builder.Build();
        app.MapGet("/target", () => Results.Text("replayed"));
        app.MapGet("/replay", ReplayToSelfAsync);
        await app.StartAsync();
        return app;
    }

    // Mirrors the executors: replay to 127.0.0.1 on the local port with the original Host header.
    private static async Task<IResult> ReplayToSelfAsync(HttpContext current, bool pinned)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get,
            new UriBuilder(Uri.UriSchemeHttps, IPAddress.Loopback.ToString(), current.Connection.LocalPort, "/target").Uri);
        message.Headers.Host = current.Request.Host.Value;
        if (pinned)
            AdminOperationLoopbackTls.PinServedCertificate(message, current);
        using var handler = AdminOperationLoopbackTls.CreateHandler();
        using var client = new HttpClient(handler);
        try
        {
            using var response = await client.SendAsync(message);
            return Results.Text(await response.Content.ReadAsStringAsync());
        }
        catch (HttpRequestException exception)
        {
            return Results.Text("failed:" + exception.GetType().Name, statusCode: StatusCodes.Status502BadGateway);
        }
    }

    private static async Task<(HttpStatusCode Status, string Body)> RequestReplayAsync(HttpClient caller, Uri address, bool pinned)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(address, $"/replay?pinned={(pinned ? "true" : "false")}"));
        request.Headers.Host = "honua.example";
        using var response = await caller.SendAsync(request);
        return (response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static X509Certificate2 CreateSelfSigned(string subject)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest(subject, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var names = new SubjectAlternativeNameBuilder();
        names.AddDnsName(subject["CN=".Length..]);
        request.CertificateExtensions.Add(names.Build());
        using var ephemeral = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        // Kestrel on Windows needs a persisted key; exporting and reloading gives one on every platform.
        return X509CertificateLoader.LoadPkcs12(ephemeral.Export(X509ContentType.Pkcs12), null);
    }
}
