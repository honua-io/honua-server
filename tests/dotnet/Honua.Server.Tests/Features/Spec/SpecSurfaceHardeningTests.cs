// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Honua.TestKit.Security;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Tests.Features.Spec;

// Validate, artifact and apply-limit rows on a host with real admin gating. Spies replace the
// planner, apply engine and artifact cache so each case can prove what the handler reached.
[SecurityTest]
[Protocol(TestProtocols.Spec)]
public sealed class SpecSurfaceHardeningTests(SpecSurfaceHardeningTests.GatedHost host)
    : IClassFixture<SpecSurfaceHardeningTests.GatedHost>
{
    private const string ValidateBody = """{"text":"grammar \"v1.0\""}""";
    private const string UnknownHash = "0000000000000000000000000000000000000000000000000000000000000000";

    public sealed class GatedHost : IAsyncLifetime
    {
        private const string BootstrapAdminKey = "spec-surface-hardening-bootstrap-key";

        internal SpyPlanner Planner { get; } = new();

        internal SpyApplyEngine Engine { get; } = new();

        internal SpyArtifactCache Cache { get; } = new();

        internal WebApplicationFactory<Program> Factory { get; private set; } = null!;

        internal HttpClient Client { get; private set; } = null!;

        internal IReadOnlyDictionary<string, string> Keys { get; private set; } =
            new Dictionary<string, string>();

        public async Task InitializeAsync()
        {
            var license = new TestLicenseEntitlementService(HonuaEdition.Pro);
            Factory = new TestWebApplicationFactory().WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapAdminKey);
                builder.ConfigureTestServices(services =>
                {
                    services.Replace(ServiceDescriptor.Singleton<ISpecPlanner>(Planner));
                    services.Replace(ServiceDescriptor.Singleton<ISpecApplyEngine>(Engine));
                    services.Replace(ServiceDescriptor.Singleton<IContentHashArtifactCache>(Cache));
                    services.Replace(ServiceDescriptor.Singleton<ILicenseEntitlementService>(license));
                    services.Replace(ServiceDescriptor.Singleton<ILicenseStatusProvider>(license));
                });
            });
            Client = Factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
            var store = Factory.Services.GetRequiredService<IAdminApiKeyStore>();
            Keys = new Dictionary<string, string>
            {
                ["non-admin"] = await IssueKeyAsync(store, "non-admin", "read:layers"),
                ["admin-read"] = await IssueKeyAsync(store, "admin-read", "admin:read"),
                ["admin-write"] = await IssueKeyAsync(store, "admin-write", "admin:write")
            };
        }

        public async Task DisposeAsync()
        {
            Client.Dispose();
            await Factory.DisposeAsync();
        }

        private static async Task<string> IssueKeyAsync(IAdminApiKeyStore store, string name, string permission)
        {
            var result = await store.CreateAsync(
                $"spec-surface-{name}",
                [permission],
                expiresAt: null,
                createdBy: "spec-surface-hardening",
                CancellationToken.None);
            return result.Key;
        }
    }

    [IntegrationTheory]
    [Operation(Operations.Security)]
    [Endpoint("POST /v1/spec/validate")]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("non-admin", HttpStatusCode.Forbidden)]
    [InlineData("admin-read", HttpStatusCode.Forbidden)]
    [InlineData("admin-write", HttpStatusCode.OK)]
    public async Task Validate_PerPrincipal_AdmitsOnlyAdminWrite(string profile, HttpStatusCode expected)
    {
        using var request = Post("/v1/spec/validate", ValidateBody, profile);
        using var response = await host.Client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(expected, body);
        if (expected != HttpStatusCode.OK)
        {
            body.Should().NotContain("diagnostics", "a refused caller must not reach the validator");
        }
    }

    [IntegrationTheory]
    [Operation(Operations.Security)]
    [Endpoint("GET /v1/spec/artifact/{hash}")]
    [InlineData("anonymous", HttpStatusCode.Unauthorized)]
    [InlineData("non-admin", HttpStatusCode.Forbidden)]
    [InlineData("admin-write", HttpStatusCode.NotFound)]
    public async Task Artifact_PerPrincipal_RefusesBeforeTheCache(string profile, HttpStatusCode expected)
    {
        var cacheCallsBefore = host.Cache.Calls;
        using var request = new HttpRequestMessage(HttpMethod.Get, $"/v1/spec/artifact/{UnknownHash}");
        AddKey(request, profile);

        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(expected);
        var reachedCache = host.Cache.Calls > cacheCallsBefore;
        reachedCache.Should().Be(expected == HttpStatusCode.NotFound,
            "only an admitted caller may look up cached artifacts by hash");
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("GET /v1/spec/artifact/{hash}")]
    public async Task Artifact_TraversalShapedHashes_NeverServeContentOrFail()
    {
        foreach (var payload in SecurityTestScenarios.PathTraversalPayloads)
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                $"/v1/spec/artifact/{Uri.EscapeDataString(payload)}");
            AddKey(request, "admin-write");

            using var response = await host.Client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();

            response.StatusCode.Should().BeOneOf([HttpStatusCode.BadRequest, HttpStatusCode.NotFound], payload);
            body.Should().NotContain("root:", payload);
            response.Headers.Contains("X-Spec-Content-Hash").Should().BeFalse(payload);
        }
    }

    [IntegrationTheory]
    [Operation(Operations.Query)]
    [Endpoint("POST /v1/spec/validate")]
    [InlineData("{not json")]
    [InlineData("[]")]
    [InlineData("\"grammar v1.0\"")]
    public async Task Validate_MalformedOrNonObjectBody_Returns400InvalidRequestBody(string body)
    {
        using var request = Post("/v1/spec/validate", body, "admin-write");
        using var response = await host.Client.SendAsync(request);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        using var payload = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        payload.RootElement.GetProperty("code").GetString().Should().Be(SpecDiagnosticCodes.InvalidRequestBody);
    }

    [IntegrationTheory]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(17, 16)]
    [InlineData(int.MaxValue, 16)]
    [InlineData(0, 4)]
    [InlineData(-5, 4)]
    [InlineData(null, 4)]
    public async Task Apply_MaxConcurrency_IsClampedToTheServerCeiling(int? requested, int expected)
    {
        var maxConcurrency = requested is int value
            ? $",\"maxConcurrency\":{value.ToString(System.Globalization.CultureInfo.InvariantCulture)}"
            : string.Empty;
        using var request = Post("/v1/spec/apply", ApplyBody(maxConcurrency), "admin-write");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Headers.GetValues("X-Spec-Apply-Token").Should().ContainSingle(SpyApplyEngine.Token);
        host.Engine.LastOptions.Should().NotBeNull();
        host.Engine.LastOptions!.MaxConcurrency.Should().Be(expected);
        expected.Should().BeLessThanOrEqualTo(SpecEndpoints.MaxApplyConcurrency);
    }

    [IntegrationTheory]
    [Operation(Operations.ProcessExecution)]
    [Endpoint("POST /v1/spec/apply")]
    [InlineData("missing-accept")]
    [InlineData("unknown-cache-mode")]
    [InlineData("malformed-body")]
    public async Task Apply_RefusedRequest_NeverStartsTheEngineOrIssuesAToken(string refusal)
    {
        var startsBefore = host.Engine.StartCalls;
        var body = refusal switch
        {
            "unknown-cache-mode" => ApplyBody(",\"cacheMode\":999"),
            "malformed-body" => "{\"nodes\":",
            _ => ApplyBody(string.Empty)
        };
        using var request = Post("/v1/spec/apply", body, "admin-write");
        if (refusal != "missing-accept")
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }

        using var response = await host.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest, refusal);
        response.Headers.Contains("X-Spec-Apply-Token").Should().BeFalse(refusal);
        response.Content.Headers.ContentType?.MediaType.Should().NotBe("text/event-stream", refusal);
        host.Engine.StartCalls.Should().Be(startsBefore, $"a refused apply must not start a run ({refusal})");
    }

    private static string ApplyBody(string extra)
        => "{\"grammarVersion\":\"grammar/1.0\",\"processFamilyVersion\":\"family/1.0\",\"nodes\":[]" + extra + "}";

    private HttpRequestMessage Post(string path, string body, string profile)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        AddKey(request, profile);
        return request;
    }

    private void AddKey(HttpRequestMessage request, string profile)
    {
        if (host.Keys.TryGetValue(profile, out var key))
        {
            request.Headers.Add("X-API-Key", key);
        }
    }

    internal static SpecPlan EmptyPlan() => new()
    {
        PlanId = "spec-surface-hardening-plan",
        GrammarVersion = "grammar/1.0",
        ProcessFamilyVersion = "family/1.0",
        Nodes = []
    };

    internal sealed class SpyPlanner : ISpecPlanner
    {
        public Task<SpecPlan> PlanAsync(CanonicalSpecDocument document, CancellationToken cancellationToken = default)
            => Task.FromResult(EmptyPlan());
    }

    internal sealed class SpyApplyEngine : ISpecApplyEngine
    {
        internal const string Token = "spec-surface-hardening-token";

        private int _startCalls;

        public int StartCalls => Volatile.Read(ref _startCalls);

        public SpecApplyOptions? LastOptions { get; private set; }

        public Task<SpecApplyHandle> StartAsync(
            CanonicalSpecDocument document,
            SpecApplyOptions options,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _startCalls);
            LastOptions = options;
            return Task.FromResult(new SpecApplyHandle(Token, NoEvents(), EmptyPlan()));
        }

        public bool TryCancel(string applyToken) => false;

        private static async IAsyncEnumerable<SpecApplyEvent> NoEvents()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    internal sealed class SpyArtifactCache : IContentHashArtifactCache
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CachedArtifactRef?> TryGetAsync(string contentHash, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult<CachedArtifactRef?>(null);
        }

        public Task<Stream?> OpenReadAsync(string contentHash, CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult<Stream?>(null);
        }

        public Task<CachedArtifactRef> PutAsync(SpecArtifactPayload payload, CancellationToken cancellationToken = default)
            => Task.FromException<CachedArtifactRef>(
                new InvalidOperationException("The hardening cases must not write artifacts."));
    }
}
