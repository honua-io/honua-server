// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Text;
using System.Text.Json;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Monitoring;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;

namespace Honua.Server.Tests.Features.Infrastructure.Monitoring;

[Protocol(TestProtocols.TestQuality)]
public sealed class OpsSelfCheckSourceTests
{
    [UnitTheory]
    [InlineData("https://127.0.0.1")]
    [InlineData("https://10.0.0.1")]
    [InlineData("https://169.254.169.254")]
    [InlineData("http://8.8.8.8")]
    [InlineData("https://user:password@8.8.8.8")]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadProviders_UnsafeAuthority_NeverSendsRequest(string authority)
    {
        var factory = new FakeClients("{}");
        var source = Create(factory, [Provider(authority)]);
        Assert.False(Assert.Single(await source.ReadIdentityProvidersAsync(default)).IsReachable);
        Assert.Equal(0, factory.Requests);
    }

    [UnitTheory]
    [InlineData("{\"issuer\":\"https://8.8.8.8\"}", true)]
    [InlineData("{\"issuer\":\"https://8.8.8.8/\"}", true)]
    [InlineData("{\"issuer\":\"https://other.example\"}", false)]
    [InlineData("{\"issuer\":42}", false)]
    [InlineData("[]", false)]
    [InlineData("invalid", false)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadProviders_Discovery_RequiresMatchingIssuer(string body, bool expected)
    {
        var factory = new FakeClients(body);
        var source = Create(factory, [Provider("https://8.8.8.8")]);
        Assert.Equal(expected, Assert.Single(await source.ReadIdentityProvidersAsync(default)).IsReachable);
        Assert.Equal("https://8.8.8.8/.well-known/openid-configuration", factory.LastUri?.AbsoluteUri);
        Assert.Null(factory.Authorization);
    }

    [UnitTheory]
    [InlineData("AzureAd", "https://login.microsoftonline.com/common/v2.0", "https://login.microsoftonline.com/{tenantid}/v2.0", true)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/organizations/v2.0", "https://login.microsoftonline.com/{tenantid}/v2.0", true)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/consumers/v2.0", "https://login.microsoftonline.com/9188040d-6c67-4c5b-b112-36a304b66dad/v2.0", true)]
    [InlineData("AzureAd", "https://login.microsoftonline.us/organizations/v2.0", "https://login.microsoftonline.us/{tenantid}/v2.0", true)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/common/v2.0", "https://other.example/{tenantid}/v2.0", false)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/common/v2.0", "https://login.microsoftonline.com/other-tenant/v2.0", false)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/consumers/v2.0", "https://login.microsoftonline.com/{tenantid}/v2.0", false)]
    [InlineData("AzureAd", "https://login.microsoftonline.com/643bcfd3-a3d1-470c-a659-f9547d6d3447/v2.0", "https://login.microsoftonline.com/{tenantid}/v2.0", false)]
    [InlineData("Generic", "https://login.microsoftonline.com/common/v2.0", "https://login.microsoftonline.com/{tenantid}/v2.0", false)]
    [InlineData("Generic", "https://example.com", "https://example.com/", true)]
    [InlineData("Generic", "https://example.com", "https://other.example", false)]
    [InlineData("Generic", "https://example.com", null, false)]
    [Operation(Operations.TestInfrastructure)]
    public void DiscoveryIssuer_RecognizesEntraAliasesAndPreservesStrictGenericMatching(
        string type, string authority, string? issuer, bool expected)
    {
        Assert.Equal(expected, OpsSelfCheckSource.IsExpectedIssuer(Provider(authority) with { Type = type }, issuer));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadProviders_DisabledOrInvalid_OnlyEnabledProviderReported()
    {
        var factory = new FakeClients("{}");
        var source = Create(factory,
            [Provider("https://8.8.8.8") with { Enabled = false }, Provider("https://8.8.8.8") with { IsValid = false }]);
        Assert.False(Assert.Single(await source.ReadIdentityProvidersAsync(default)).IsReachable);
        Assert.Equal(0, factory.Requests);
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_ValidMetadata_UsesPublicFeedWithoutCredentials()
    {
        var factory = new FakeClients("{\"tag_name\":\"v2026.2\",\"html_url\":\"https://github.com/honua-io/honua-server/releases/tag/v2026.2\"}");
        var release = await Create(factory).ReadLatestReleaseAsync(default);
        Assert.Equal("v2026.2", release?.Version);
        Assert.Equal("https://api.github.com/repos/honua-io/honua-server/releases/latest", factory.LastUri?.AbsoluteUri);
        Assert.Null(factory.Authorization);
    }

    [UnitTheory]
    [InlineData("https://evil.example/releases/tag/v2026.2")]
    [InlineData("https://github.com/other/repo/releases/tag/v2026.2")]
    [InlineData("https://github.com/honua-io/honua-server/releases/tag/v2026.2?redirect=evil")]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_UntrustedNotes_RejectsMetadata(string url)
    {
        var factory = new FakeClients(JsonSerializer.Serialize(new { tag_name = "v2026.2", html_url = url }));
        await Assert.ThrowsAsync<JsonException>(() => Create(factory).ReadLatestReleaseAsync(default));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_MissingPublicRelease_ProducesNoRelease()
    {
        Assert.Null(await Create(new FakeClients("{}") { Status = HttpStatusCode.NotFound }).ReadLatestReleaseAsync(default));
    }

    [UnitTheory]
    [InlineData(HttpStatusCode.Redirect)]
    [InlineData(HttpStatusCode.Forbidden)]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_UnsuccessfulResponse_FailsClosed(HttpStatusCode status)
    {
        await Assert.ThrowsAsync<HttpRequestException>(() => Create(new FakeClients("{}") { Status = status }).ReadLatestReleaseAsync(default));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_OversizedResponse_RejectsBeforeParsing()
    {
        var source = Create(new FakeClients(new string(' ', OpsSelfCheckSource.MaximumResponseBytes + 1)));
        await Assert.ThrowsAsync<HttpRequestException>(() => source.ReadLatestReleaseAsync(default));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public async Task ReadRelease_CallerCancelled_PropagatesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Create(new FakeClients("{}")).ReadLatestReleaseAsync(cancellation.Token));
    }

    [UnitTest]
    [Operation(Operations.TestInfrastructure)]
    public void Handler_Defaults_RejectRedirectsCookiesAndProxy()
    {
        using var handler = Assert.IsType<SocketsHttpHandler>(OpsSelfCheckHttpHandler.Create());
        Assert.False(handler.AllowAutoRedirect);
        Assert.False(handler.UseCookies);
        Assert.False(handler.UseProxy);
        Assert.NotNull(handler.ConnectCallback);
    }

    private static OpsSelfCheckSource Create(FakeClients factory, IReadOnlyList<OidcProviderRegistration>? providers = null)
        => new(factory, () => providers ?? [], new(null, "2026.1"));

    private static OidcProviderRegistration Provider(string authority)
        => new("643bcfd3-a3d1-470c-a659-f9547d6d3447", "Generic", "Test", authority,
            "client", "never-send-secret", ["openid"], null, true, true);

    private sealed class FakeClients(string body) : HttpMessageHandler, IHttpClientFactory
    {
        public int Requests { get; private set; }
        public Uri? LastUri { get; private set; }
        public string? Authorization { get; private set; }
        public HttpStatusCode Status { get; init; } = HttpStatusCode.OK;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests++;
            LastUri = request.RequestUri;
            Authorization = request.Headers.Authorization?.ToString();
            return Task.FromResult(new HttpResponseMessage(Status) { Content = new StringContent(body, Encoding.UTF8, "application/json") });
        }
    }
}
