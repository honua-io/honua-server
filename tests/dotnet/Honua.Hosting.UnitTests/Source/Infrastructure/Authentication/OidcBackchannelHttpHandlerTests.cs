// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Sockets;
using System.Text;
using FluentAssertions;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Hosting;

namespace Honua.Server.Tests.Infrastructure.Authentication;

[SecurityTest]
public sealed class OidcBackchannelHttpHandlerTests
{
    [UnitTheory]
    [InlineData("Development")]
    [InlineData("Test")]
    public void AllowsPrivateNetworks_DevelopmentAndTestEnvironments_ReturnsTrue(string environmentName)
    {
        var environment = new StubHostEnvironment(environmentName);

        OidcBackchannelHttpHandler.AllowsPrivateNetworks(environment).Should().BeTrue();
    }

    [UnitTheory]
    [InlineData("Production")]
    [InlineData("Staging")]
    [InlineData(null)]
    public void AllowsPrivateNetworks_NonDevelopmentEnvironments_ReturnsFalse(string? environmentName)
    {
        IHostEnvironment? environment = environmentName is null ? null : new StubHostEnvironment(environmentName);

        OidcBackchannelHttpHandler.AllowsPrivateNetworks(environment).Should().BeFalse();
    }

    [UnitTheory]
    [InlineData("169.254.169.254")]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.10")]
    [InlineData("::1")]
    public async Task CreatePublicOnlyClient_BlocksPrivateLoopbackAndMetadataAddresses(string resolvedAddress)
    {
        using var client = OidcBackchannelHttpHandler.CreatePublicOnlyClient(
            hostAddressResolver: (_, _) => Task.FromResult(new[] { IPAddress.Parse(resolvedAddress) }));

        var act = () => client.GetAsync("https://idp.example.test/.well-known/openid-configuration");

        await act.Should().ThrowAsync<HttpRequestException>()
            .WithMessage("*private, loopback, or unresolvable*");
    }

    [UnitTest]
    public async Task CreateClient_DevelopmentPermitsLoopbackAndRefusesRedirects_ProductionBlocksLoopback()
    {
        using var listener = new LocalRedirectAndDiscoveryServer();
        using var devClient = OidcBackchannelHttpHandler.CreateClientAllowingPrivateNetworks();
        using var prodClient = OidcBackchannelHttpHandler.CreatePublicOnlyClient();

        using var devOk = await devClient.GetAsync(listener.OkUrl);
        devOk.StatusCode.Should().Be(HttpStatusCode.OK);

        using var devRedirect = await devClient.GetAsync(listener.RedirectUrl);
        devRedirect.StatusCode.Should().Be(HttpStatusCode.Found);
        listener.RedirectTargetHits.Should().Be(0, "redirects must never be followed on OIDC backchannel clients");

        var prodAct = () => prodClient.GetAsync(listener.OkUrl);
        await prodAct.Should().ThrowAsync<HttpRequestException>();
    }

    [UnitTest]
    public async Task MultiAuthorityAndCache_ProductionMode_RefuseLoopbackAndRedirectDiscovery()
    {
        using var listener = new LocalRedirectAndDiscoveryServer();
        var prodMulti = new MultiAuthorityConfigurationManager([listener.BaseUrl], DiscoveryEndpointPolicy.AllowHttp);
        var prodCache = OidcConfigurationManagerCache.CreatePublicOnly();

        var multiAct = () => prodMulti.GetConfigurationAsync(CancellationToken.None);
        var cacheAct = () => prodCache
            .GetOrCreate(listener.BaseUrl + "/.well-known/openid-configuration", requireHttps: false)
            .GetConfigurationAsync(CancellationToken.None);

        await multiAct.Should().ThrowAsync<Exception>();
        await cacheAct.Should().ThrowAsync<Exception>();

        var devCache = OidcConfigurationManagerCache.CreateAllowingPrivateNetworks();
        var redirectAct = () => devCache
            .GetOrCreate(listener.RedirectUrl, requireHttps: false)
            .GetConfigurationAsync(CancellationToken.None);
        await redirectAct.Should().ThrowAsync<Exception>();
        listener.RedirectTargetHits.Should().Be(0, "OIDC discovery must never follow 3xx redirects");

        var devMulti = new MultiAuthorityConfigurationManager(
            [listener.BaseUrl],
            DiscoveryEndpointPolicy.AllowHttp | DiscoveryEndpointPolicy.AllowPrivateNetworks);
        var config = await devMulti.GetConfigurationAsync(CancellationToken.None);
        config.Issuer.Should().Be(listener.BaseUrl);
    }

    [UnitTest]
    public async Task ReadBoundedBytesAsync_WithinLimitSucceeds_ExceedingLimitThrows()
    {
        using var okContent = new ByteArrayContent(new byte[64]);
        var bytes = await OidcBackchannelHttpHandler.ReadBoundedBytesAsync(okContent, 64, CancellationToken.None);
        bytes.Should().HaveCount(64);

        using var declaredOversized = new ByteArrayContent(new byte[65]);
        var declaredAct = () => OidcBackchannelHttpHandler.ReadBoundedBytesAsync(declaredOversized, 64, CancellationToken.None);
        await declaredAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowed size*");

        using var streamedOversized = new StreamContent(new MemoryStream(new byte[128]));
        var streamedAct = () => OidcBackchannelHttpHandler.ReadBoundedBytesAsync(streamedOversized, 64, CancellationToken.None);
        await streamedAct.Should().ThrowAsync<InvalidOperationException>().WithMessage("*maximum allowed size*");
    }

    private sealed class StubHostEnvironment(string environmentName) : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = environmentName;

        public string ApplicationName { get; set; } = "Honua.Tests";

        public string ContentRootPath { get; set; } = string.Empty;

        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
    }

    private sealed class LocalRedirectAndDiscoveryServer : IDisposable
    {
        private readonly HttpListener _listener = new();
        private int _redirectTargetHits;

        public LocalRedirectAndDiscoveryServer()
        {
            using var probe = new TcpListener(IPAddress.Loopback, 0);
            probe.Start();
            var port = ((IPEndPoint)probe.LocalEndpoint).Port;
            probe.Stop();

            BaseUrl = $"http://127.0.0.1:{port}";
            OkUrl = $"{BaseUrl}/ok";
            RedirectUrl = $"{BaseUrl}/redirect";
            _listener.Prefixes.Add($"{BaseUrl}/");
            _listener.Start();
            _ = Task.Run(ServeAsync);
        }

        public string BaseUrl { get; }

        public string OkUrl { get; }

        public string RedirectUrl { get; }

        public int RedirectTargetHits => Volatile.Read(ref _redirectTargetHits);

        public void Dispose() => _listener.Close();

        private async Task ServeAsync()
        {
            while (_listener.IsListening)
            {
                HttpListenerContext context;
                try
                {
                    context = await _listener.GetContextAsync().ConfigureAwait(false);
                }
                catch (Exception) when (!_listener.IsListening)
                {
                    break;
                }
                catch (HttpListenerException)
                {
                    break;
                }
                catch (ObjectDisposedException)
                {
                    break;
                }

                await HandleRequestAsync(context).ConfigureAwait(false);
            }
        }

        private async Task HandleRequestAsync(HttpListenerContext context)
        {
            var path = context.Request.Url?.AbsolutePath ?? string.Empty;
            if (string.Equals(path, "/redirect", StringComparison.OrdinalIgnoreCase))
            {
                context.Response.StatusCode = (int)HttpStatusCode.Found;
                context.Response.RedirectLocation = $"{BaseUrl}/redirect-target";
                context.Response.Close();
                return;
            }

            if (string.Equals(path, "/redirect-target", StringComparison.OrdinalIgnoreCase))
            {
                Interlocked.Increment(ref _redirectTargetHits);
                await WriteJsonAsync(context.Response, "{}").ConfigureAwait(false);
                return;
            }

            var body = path switch
            {
                "/.well-known/openid-configuration" =>
                    $$"""{"issuer":"{{BaseUrl}}","jwks_uri":"{{BaseUrl}}/jwks","authorization_endpoint":"{{BaseUrl}}/auth","token_endpoint":"{{BaseUrl}}/token"}""",
                "/jwks" => """{"keys":[]}""",
                "/ok" => """{"status":"ok"}""",
                _ => null,
            };
            context.Response.StatusCode = body is null ? 404 : 200;
            await WriteJsonAsync(context.Response, body ?? string.Empty).ConfigureAwait(false);
        }

        private static async Task WriteJsonAsync(HttpListenerResponse response, string body)
        {
            var bytes = Encoding.UTF8.GetBytes(body);
            response.ContentType = "application/json";
            response.ContentLength64 = bytes.Length;
            try
            {
                await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
                response.Close();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                // Listener closed during test teardown.
            }
        }
    }
}
