// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using System.Text;
using FluentAssertions;
using Grpc.Core;
using Grpc.Net.Client;
using Grpc.Net.Client.Web;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Spec.Abstractions;
using Honua.Core.Features.Spec.Domain;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Honua.TestKit.Helpers;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Proto = Geospatial.V1;

namespace Honua.Server.Tests.Features.Spec;

/// <summary>
/// Pins the REST-equivalent admin authorization boundary on every gRPC Spec RPC.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.Grpc)]
public sealed class GrpcSpecAuthorizationIntegrationTests
{
    private const string BootstrapAdminKey = "grpc-spec-authorization-bootstrap-key";

    [IntegrationTest]
    [Operation(Operations.Security)]
    [Endpoint("POST /geospatial.v1.SpecService/PlanSpec")]
    [Endpoint("POST /geospatial.v1.SpecService/ApplySpec")]
    [Endpoint("POST /geospatial.v1.SpecService/CancelApply")]
    [Endpoint("POST /v1/spec/plan")]
    [Endpoint("POST /v1/spec/apply")]
    [Endpoint("POST /v1/spec/cancel")]
    public async Task SpecAuthorization_RealTransportMatrix_MatchesRestAndStopsDeniedCalls()
    {
        var cases = Enum.GetValues<GrpcTransport>()
            .SelectMany(transport => Enum.GetValues<SpecRpc>()
                .SelectMany(rpc => Enum.GetValues<PrincipalProfile>()
                    .Select(profile => (transport, rpc, profile))))
            .ToArray();

        cases.Should().HaveCount(24,
            "the required matrix is 2 transports x 3 RPCs x 4 principal profiles");

        foreach (var transportCases in cases.GroupBy(testCase => testCase.transport))
        {
            using var host = await AuthorizationHost.CreateAsync(transportCases.Key);

            foreach (var testCase in transportCases)
            {
                var because = $"transport={testCase.transport}, rpc={testCase.rpc}, principal={testCase.profile}";
                var key = host.ApiKeys[testCase.profile];
                var before = host.Spies.Snapshot();

                var grpcStatus = await InvokeGrpcAsync(host.GrpcClient, testCase.rpc, key);
                var afterGrpc = host.Spies.Snapshot();
                var restStatus = await InvokeRestAsync(host.RestClient, testCase.rpc, key, host.RequiresHttp2);
                var afterRest = host.Spies.Snapshot();

                if (testCase.profile is not PrincipalProfile.AdminWrite)
                {
                    grpcStatus.Should().Be(
                        testCase.profile is PrincipalProfile.Anonymous
                            ? StatusCode.Unauthenticated
                            : StatusCode.PermissionDenied,
                        because);
                    restStatus.Should().Be(
                        testCase.profile is PrincipalProfile.Anonymous
                            ? HttpStatusCode.Unauthorized
                            : HttpStatusCode.Forbidden,
                        because);
                    afterGrpc.Should().Be(before,
                        $"denied gRPC calls must have zero planner, start, cancel, or artifact/cache effects ({because})");
                    afterRest.Should().Be(afterGrpc,
                        $"denied REST twins must have zero planner, start, cancel, or artifact/cache effects ({because})");
                    continue;
                }

                grpcStatus.Should().BeNull($"admin:write must reach the gRPC handler ({because})");
                restStatus.Should().NotBe(HttpStatusCode.Unauthorized,
                    $"admin:write must reach the REST twin ({because})");
                restStatus.Should().NotBe(HttpStatusCode.Forbidden, because);

                var expected = ExpectedHandlerCalls(testCase.rpc);
                CallSnapshot.Delta(before, afterGrpc).Should().Be(expected,
                    $"the admitted gRPC call must reach only its intended handler seam ({because})");
                CallSnapshot.Delta(afterGrpc, afterRest).Should().Be(expected,
                    $"the admitted REST twin must reach the same intended handler seam ({because})");
            }
        }
    }

    [IntegrationTest]
    [Operation(Operations.Security)]
    public async Task AdminPolicy_AllSupportedCompositions_RetainPermissionCeiling()
    {
        var compositions = Enum.GetValues<PolicyComposition>();
        compositions.Should().HaveCount(3);

        foreach (var composition in compositions)
        {
            using var factory = CreatePolicyFactory(composition);
            _ = factory.CreateClient();

            var provider = factory.Services.GetRequiredService<IAuthorizationPolicyProvider>();
            var policy = await provider.GetPolicyAsync(AuthenticationExtensions.AdminPolicy);

            policy.Should().NotBeNull($"the Admin policy must exist for {composition}");
            policy!.Requirements.Should().Contain(
                requirement => requirement is AdminPermissionRequirement,
                $"{composition} must not drop the method-aware admin permission ceiling");
        }
    }

    private static WebApplicationFactory<Program> CreatePolicyFactory(PolicyComposition composition)
    {
        return new TestWebApplicationFactory()
            .WithWebHostBuilder(builder =>
            {
                builder.UseSetting("HONUA_DEV_AUTH", "false");
                builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapAdminKey);

                if (composition is PolicyComposition.Oidc)
                {
                    builder.UseSetting("Oidc:Enabled", "true");
                    builder.UseSetting("Oidc:Generic:Enabled", "true");
                    builder.UseSetting("Oidc:Generic:Authority", "https://idp.invalid");
                    builder.UseSetting("Oidc:Generic:ClientId", "grpc-spec-auth-test");
                    builder.ConfigureTestServices(services =>
                        services.RemoveAll<IValidateOptions<OidcAuthenticationOptions>>());
                }

                if (composition is PolicyComposition.OptionalMtls)
                {
                    builder.UseSetting("Authentication:ClientCertificates:Mode", "Optional");
                    builder.UseSetting("Authentication:ClientCertificates:EnvironmentId", "test");
                    builder.UseSetting("Capabilities:Experimental:security.mtls:Enabled", "true");
                }
            });
    }

    private static async Task<StatusCode?> InvokeGrpcAsync(
        Proto.SpecService.SpecServiceClient client,
        SpecRpc rpc,
        string? apiKey)
    {
        var headers = apiKey is null ? null : new Metadata { { "x-api-key", apiKey } };

        try
        {
            switch (rpc)
            {
                case SpecRpc.Plan:
                    _ = await client.PlanSpecAsync(new Proto.PlanSpecRequest
                    {
                        Document = BuildDocument()
                    }, headers);
                    break;

                case SpecRpc.Apply:
                    using (var call = client.ApplySpec(new Proto.ApplySpecRequest
                    {
                        Document = BuildDocument(),
                        CacheMode = Proto.SpecCacheMode.ReadWrite,
                        MaxConcurrency = 1
                    }, headers))
                    {
                        await foreach (var _ in call.ResponseStream.ReadAllAsync())
                        {
                            // Drain the response stream so the authorization result reflects a completed RPC.
                        }
                    }
                    break;

                case SpecRpc.Cancel:
                    _ = await client.CancelApplyAsync(new Proto.CancelJobRequest
                    {
                        JobId = "matrix-apply-token"
                    }, headers);
                    break;

                default:
                    throw new ArgumentOutOfRangeException(nameof(rpc), rpc, null);
            }

            return null;
        }
        catch (RpcException exception)
        {
            return exception.StatusCode;
        }
    }

    private static async Task<HttpStatusCode> InvokeRestAsync(
        HttpClient client,
        SpecRpc rpc,
        string? apiKey,
        bool requiresHttp2)
    {
        var path = rpc switch
        {
            SpecRpc.Plan => "/v1/spec/plan",
            SpecRpc.Apply => "/v1/spec/apply",
            SpecRpc.Cancel => "/v1/spec/cancel",
            _ => throw new ArgumentOutOfRangeException(nameof(rpc), rpc, null)
        };
        var body = rpc is SpecRpc.Cancel
            ? "{\"applyToken\":\"matrix-apply-token\"}"
            : "{\"grammarVersion\":\"grammar/1.0\",\"processFamilyVersion\":\"family/1.0\",\"nodes\":[],\"cacheMode\":\"ReadWrite\",\"maxConcurrency\":1}";

        using var request = new HttpRequestMessage(HttpMethod.Post, path)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
        if (rpc is SpecRpc.Apply)
        {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        }
        if (apiKey is not null)
        {
            request.Headers.Add("X-API-Key", apiKey);
        }
        if (requiresHttp2)
        {
            request.Version = HttpVersion.Version20;
            request.VersionPolicy = HttpVersionPolicy.RequestVersionExact;
        }

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        return response.StatusCode;
    }

    private static Proto.CanonicalSpecDocument BuildDocument() => new()
    {
        GrammarVersion = "grammar/1.0",
        ProcessFamilyVersion = "family/1.0"
    };

    private static CallSnapshot ExpectedHandlerCalls(SpecRpc rpc) => rpc switch
    {
        SpecRpc.Plan => new CallSnapshot(Planner: 1, Start: 0, Cancel: 0, Cache: 0),
        SpecRpc.Apply => new CallSnapshot(Planner: 1, Start: 1, Cancel: 0, Cache: 0),
        SpecRpc.Cancel => new CallSnapshot(Planner: 0, Start: 0, Cancel: 1, Cache: 0),
        _ => throw new ArgumentOutOfRangeException(nameof(rpc), rpc, null)
    };

    private enum GrpcTransport
    {
        Native,
        GrpcWeb
    }

    private enum SpecRpc
    {
        Plan,
        Apply,
        Cancel
    }

    private enum PrincipalProfile
    {
        Anonymous,
        AuthenticatedNonAdmin,
        AdminRead,
        AdminWrite
    }

    private enum PolicyComposition
    {
        ApiKey,
        Oidc,
        OptionalMtls
    }

    private sealed class AuthorizationHost : IDisposable
    {
        private AuthorizationHost(
            WebApplicationFactory<Program> factory,
            HttpClient restClient,
            GrpcChannel channel,
            SpecAuthorizationSpies spies,
            IReadOnlyDictionary<PrincipalProfile, string?> apiKeys,
            bool requiresHttp2)
        {
            Factory = factory;
            RestClient = restClient;
            Channel = channel;
            GrpcClient = new Proto.SpecService.SpecServiceClient(channel);
            Spies = spies;
            ApiKeys = apiKeys;
            RequiresHttp2 = requiresHttp2;
        }

        public WebApplicationFactory<Program> Factory { get; }

        public HttpClient RestClient { get; }

        public GrpcChannel Channel { get; }

        public Proto.SpecService.SpecServiceClient GrpcClient { get; }

        public SpecAuthorizationSpies Spies { get; }

        public IReadOnlyDictionary<PrincipalProfile, string?> ApiKeys { get; }

        public bool RequiresHttp2 { get; }

        public static async Task<AuthorizationHost> CreateAsync(GrpcTransport transport)
        {
            var spies = new SpecAuthorizationSpies();
            var proLicense = new TestLicenseEntitlementService(HonuaEdition.Pro);
            WebApplicationFactory<Program> factory = new TestWebApplicationFactory()
                .WithWebHostBuilder(builder =>
                {
                    builder.UseSetting("HONUA_DEV_AUTH", "false");
                    builder.UseSetting("HONUA_ADMIN_PASSWORD", BootstrapAdminKey);
                    builder.ConfigureTestServices(services =>
                    {
                        services.Replace(ServiceDescriptor.Singleton<ISpecPlanner>(spies.Planner));
                        services.Replace(ServiceDescriptor.Singleton<ISpecApplyEngine>(spies.ApplyEngine));
                        services.Replace(ServiceDescriptor.Singleton<IContentHashArtifactCache>(spies.ArtifactCache));
                        services.Replace(ServiceDescriptor.Singleton<ILicenseEntitlementService>(proLicense));
                        services.Replace(ServiceDescriptor.Singleton<ILicenseStatusProvider>(proLicense));
                    });
                });

            if (transport is GrpcTransport.Native)
            {
                factory.UseKestrel(options =>
                    options.Listen(IPAddress.Loopback, 0, listen => listen.Protocols = HttpProtocols.Http2));
            }

            var restClient = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                AllowAutoRedirect = false
            });
            if (transport is GrpcTransport.Native)
            {
                var addresses = factory.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()
                    ?.Addresses
                    ?? throw new InvalidOperationException(
                        "The native Kestrel test host did not expose its server addresses feature.");
                addresses.Should().ContainSingle("the native Kestrel test host must expose its bound loopback address");
                restClient.BaseAddress = new Uri(addresses.Single());
            }
            var channel = transport is GrpcTransport.Native
                ? GrpcChannel.ForAddress(restClient.BaseAddress!, new GrpcChannelOptions
                {
                    HttpHandler = new SocketsHttpHandler
                    {
                        EnableMultipleHttp2Connections = true,
                        UseProxy = false
                    }
                })
                : GrpcChannel.ForAddress("http://localhost", new GrpcChannelOptions
                {
                    HttpHandler = new GrpcWebHandler(GrpcWebMode.GrpcWeb, factory.Server.CreateHandler())
                });

            var store = factory.Services.GetRequiredService<IAdminApiKeyStore>();
            var keys = new Dictionary<PrincipalProfile, string?>
            {
                [PrincipalProfile.Anonymous] = null,
                [PrincipalProfile.AuthenticatedNonAdmin] = await IssueKeyAsync(store, "non-admin", ["read:layers"]),
                [PrincipalProfile.AdminRead] = await IssueKeyAsync(store, "admin-read", ["admin:read"]),
                [PrincipalProfile.AdminWrite] = await IssueKeyAsync(store, "admin-write", ["admin:write"])
            };

            return new AuthorizationHost(
                factory,
                restClient,
                channel,
                spies,
                keys,
                requiresHttp2: transport is GrpcTransport.Native);
        }

        public void Dispose()
        {
            Channel.Dispose();
            RestClient.Dispose();
            Factory.Dispose();
        }

        private static async Task<string> IssueKeyAsync(
            IAdminApiKeyStore store,
            string name,
            IReadOnlyList<string> permissions)
        {
            var result = await store.CreateAsync(
                $"grpc-spec-{name}",
                permissions,
                expiresAt: null,
                createdBy: "authorization-matrix",
                CancellationToken.None);
            return result.Key;
        }
    }

    private sealed class SpecAuthorizationSpies
    {
        public SpecAuthorizationSpies()
        {
            var plan = new SpecPlan
            {
                PlanId = "authorization-matrix-plan",
                GrammarVersion = "grammar/1.0",
                ProcessFamilyVersion = "family/1.0",
                Nodes = []
            };
            Planner = new CountingPlanner(plan);
            ApplyEngine = new CountingApplyEngine(plan);
            ArtifactCache = new CountingArtifactCache();
        }

        public CountingPlanner Planner { get; }

        public CountingApplyEngine ApplyEngine { get; }

        public CountingArtifactCache ArtifactCache { get; }

        public CallSnapshot Snapshot() => new(
            Planner.Calls,
            ApplyEngine.StartCalls,
            ApplyEngine.CancelCalls,
            ArtifactCache.Calls);
    }

    private sealed class CountingPlanner(SpecPlan plan) : ISpecPlanner
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<SpecPlan> PlanAsync(
            CanonicalSpecDocument document,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult(plan);
        }
    }

    private sealed class CountingApplyEngine(SpecPlan plan) : ISpecApplyEngine
    {
        private int _startCalls;
        private int _cancelCalls;

        public int StartCalls => Volatile.Read(ref _startCalls);

        public int CancelCalls => Volatile.Read(ref _cancelCalls);

        public Task<SpecApplyHandle> StartAsync(
            CanonicalSpecDocument document,
            SpecApplyOptions options,
            CancellationToken cancellationToken)
        {
            _ = Interlocked.Increment(ref _startCalls);
            return Task.FromResult(new SpecApplyHandle(
                "matrix-apply-token",
                EmptyEvents(),
                plan));
        }

        public bool TryCancel(string applyToken)
        {
            _ = Interlocked.Increment(ref _cancelCalls);
            return false;
        }

        private static async IAsyncEnumerable<SpecApplyEvent> EmptyEvents()
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingArtifactCache : IContentHashArtifactCache
    {
        private int _calls;

        public int Calls => Volatile.Read(ref _calls);

        public Task<CachedArtifactRef?> TryGetAsync(
            string contentHash,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult<CachedArtifactRef?>(null);
        }

        public Task<Stream?> OpenReadAsync(
            string contentHash,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromResult<Stream?>(null);
        }

        public Task<CachedArtifactRef> PutAsync(
            SpecArtifactPayload payload,
            CancellationToken cancellationToken = default)
        {
            _ = Interlocked.Increment(ref _calls);
            return Task.FromException<CachedArtifactRef>(
                new InvalidOperationException("The authorization matrix must not write artifacts."));
        }
    }

    private sealed record CallSnapshot(int Planner, int Start, int Cancel, int Cache)
    {
        public static CallSnapshot Delta(CallSnapshot before, CallSnapshot after) => new(
            after.Planner - before.Planner,
            after.Start - before.Start,
            after.Cancel - before.Cancel,
            after.Cache - before.Cache);
    }
}
