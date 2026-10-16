// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Net.Http.Headers;
using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Middleware;
using Honua.Infrastructure.MultiTenancy;
using Honua.TestKit.Attributes;
using Honua.TestKit.Helpers;
using Honua.TestKit.Infrastructure;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// Real HTTP callbacks pass through production authentication and shared feature
/// authorization. Only the persisted job and feature storage are test doubles.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer, TestProtocols.Admin)]
[Operation(Operations.Security)]
[Trait("Tier", "Fast")]
public sealed class ScopedJobCallbackAuthenticationTests
{
    private const string JobId = "gp-callback-auth";
    private const string AdminPassword = "Callback-Test-Admin-Password-Only";
    private const string QueryPath = "/rest/services/beta/FeatureServer/1/query?where=1%3D1&f=json";
    private const string EditPath = "/rest/services/beta/FeatureServer/1/applyEdits";

    [IntegrationTest]
    public async Task CurrentCapability_ReadsProtectedData_AndCannotReachAnotherLayerOrService()
    {
        using var fixture = await CallbackFixture.CreateAsync(JobResourceAccess.Read);
        using var client = fixture.CreateClient();

        using var allowed = await client.GetAsync(QueryPath);
        await ServiceRbacTestFixture.AssertStatusAsync(allowed, HttpStatusCode.OK);
        (await allowed.Content.ReadAsStringAsync()).Should().Contain("features").And.NotContain("\"error\"");

        // Both are existing protected publications. Beta also publishes alpha's
        // layer, so this proves the layer boundary within the same service too.
        using var otherLayer = await client.GetAsync("/rest/services/beta/FeatureServer/0/query?f=json");
        await otherLayer.AssertGeoServicesErrorAsync(403);
        using var otherService = await client.GetAsync("/rest/services/alpha/FeatureServer/0/query?f=json");
        await otherService.AssertGeoServicesErrorAsync(403);
        using var anonymous = fixture.Factory.CreateClient();
        using var anonymousRead = await anonymous.GetAsync(QueryPath);
        await anonymousRead.AssertGeoServicesErrorAsync(401, 499);
    }

    [IntegrationTest]
    public async Task ReadOnlyCapability_CannotWrite_OrUseAnAccompanyingAdminKey()
    {
        using var fixture = await CallbackFixture.CreateAsync(JobResourceAccess.Read);
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);

        using var content = ServiceRbacTestFixture.CreateApplyEditsContent();
        using var write = await client.PostAsync(EditPath, content);
        await write.AssertGeoServicesErrorAsync(403);
        using var admin = await client.GetAsync("/api/v1/admin/connections");
        admin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTest]
    public async Task WriteCapability_UsesCanonicalFeatureEditPipeline_AndStillCannotAdminister()
    {
        using var fixture = await CallbackFixture.CreateAsync(JobResourceAccess.Write);
        using var client = fixture.CreateClient();
        using var content = ServiceRbacTestFixture.CreateApplyEditsContent();
        using var write = await client.PostAsync(EditPath, content);
        await ServiceRbacTestFixture.AssertStatusAsync(write, HttpStatusCode.OK);
        (await write.Content.ReadAsStringAsync()).Should().Contain("addResults").And.Contain("\"success\":true");
        using var admin = await client.GetAsync("/api/v1/admin/connections");
        admin.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [IntegrationTheory]
    [InlineData("wrong-job")]
    [InlineData("wrong-attempt")]
    [InlineData("rotated-token")]
    [InlineData("cancel-requested")]
    [InlineData("terminal")]
    [InlineData("queued")]
    [InlineData("revoked")]
    [InlineData("missing-job")]
    [InlineData("owner-mismatch")]
    [InlineData("tenant-mismatch")]
    [InlineData("tenant-header")]
    [InlineData("missing-bearer")]
    [InlineData("duplicate-context")]
    [InlineData("unavailable-store")]
    public async Task InvalidCapability_IsRejectedBeforeDataAndNeverFallsBackToAdmin(string reason)
    {
        using var fixture = await CallbackFixture.CreateAsync(JobResourceAccess.Write);
        using var client = fixture.CreateClient();
        client.DefaultRequestHeaders.Add("X-API-Key", AdminPassword);
        switch (reason)
        {
            case "wrong-job":
                client.DefaultRequestHeaders.Remove(ScopedJobAuthentication.JobIdHeader);
                client.DefaultRequestHeaders.Add(ScopedJobAuthentication.JobIdHeader, "other-job");
                break;
            case "wrong-attempt":
                fixture.Job = fixture.Job! with { AttemptCount = 2 };
                break;
            case "rotated-token":
                fixture.Job = fixture.Job! with
                {
                    Spec = fixture.Job.Spec with { Parameters = new Dictionary<string, string> { ["env.HONUA_JOB_TOKEN"] = new('a', 64) } }
                };
                break;
            case "cancel-requested":
                fixture.Job = fixture.Job! with { CancellationRequestedAt = DateTimeOffset.UtcNow };
                break;
            case "terminal":
                fixture.Job = fixture.Job! with { Status = ExecutionJobStatus.Succeeded };
                break;
            case "queued":
                fixture.Job = fixture.Job! with { Status = ExecutionJobStatus.Queued };
                break;
            case "revoked":
                await fixture.Issuer.RevokeAsync(fixture.Token, CancellationToken.None);
                break;
            case "missing-job":
                fixture.Job = null;
                break;
            case "owner-mismatch":
                fixture.Job = fixture.Job! with
                {
                    Audit = fixture.Job.Audit with { CustomCodeOwnerScope = fixture.Job.Audit.CustomCodeOwnerScope! with { PrincipalId = "other-owner" } }
                };
                break;
            case "tenant-mismatch":
                fixture.Job = fixture.Job! with
                {
                    Audit = fixture.Job.Audit with { CustomCodeOwnerScope = fixture.Job.Audit.CustomCodeOwnerScope! with { TenantId = "other-tenant" } }
                };
                break;
            case "tenant-header":
                client.DefaultRequestHeaders.Add("X-Honua-Tenant", "other-tenant");
                break;
            case "missing-bearer":
                client.DefaultRequestHeaders.Authorization = null;
                break;
            case "duplicate-context":
                client.DefaultRequestHeaders.Add(ScopedJobAuthentication.JobIdHeader, JobId);
                break;
            case "unavailable-store":
                fixture.StoreUnavailable = true;
                break;
        }

        using var response = await client.GetAsync(QueryPath);
        await response.AssertGeoServicesErrorAsync(401);
        // Anonymous endpoints must not silently ignore a malformed explicit credential.
        using var anonymousRoute = await client.GetAsync("/healthz/live");
        anonymousRoute.StatusCode.Should().Be(HttpStatusCode.Unauthorized, reason);
    }

    [UnitTest]
    public async Task ClaimsTransformations_CannotReplaceFrozenScopeWithOwnerOrDefaultAdminRoles()
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new ScopedJobTokenIssuer(cache, NullLogger<ScopedJobTokenIssuer>.Instance);
        var token = await issuer.IssueAsync(Request(JobResourceAccess.Read), CancellationToken.None);
        var validation = await issuer.ValidateAsync(token.Token, JobId, CancellationToken.None);
        var principal = validation!.Principal;
        var source = Substitute.For<IPrincipalMembershipSource>();
        source.ResolveMembershipAsync(Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new PrincipalMembership(true, ["admin"]));
        using var services = new ServiceCollection().BuildServiceProvider();
        var oidc = new OidcClaimsTransformation(
            Options.Create(new OidcAuthenticationOptions { DefaultRole = "admin" }),
            NullLogger<OidcClaimsTransformation>.Instance, services, source);

        var transformed = await oidc.TransformAsync(principal);
        transformed = await new ManagedMembershipClaimsTransformation(source).TransformAsync(transformed);
        transformed.IsInRole("admin").Should().BeFalse();
        transformed.FindAll("permission").Select(claim => claim.Value).Should().Equal("read:beta/Beta Layer");
        source.ReceivedCalls().Should().BeEmpty("the frozen capability must not rehydrate its owner's live role set");
    }

    [UnitTheory]
    [InlineData("tenant-a")]
    [InlineData(null)]
    public async Task FrozenTenant_IgnoresOidcClaimMappingAndAnonymousDefault(string? tenantId)
    {
        using var cache = new MemoryCache(new MemoryCacheOptions());
        var issuer = new ScopedJobTokenIssuer(cache, NullLogger<ScopedJobTokenIssuer>.Instance);
        var token = await issuer.IssueAsync(Request(JobResourceAccess.Read) with { TenantId = tenantId }, CancellationToken.None);
        var validation = await issuer.ValidateAsync(token.Token, JobId, CancellationToken.None);
        var tenant = new RequestTenantContext();
        using var services = new ServiceCollection().AddSingleton<ITenantContext>(tenant).BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services, User = validation!.Principal };
        var called = false;
        var middleware = new TenantContextMiddleware(_ =>
        {
            called = true;
            return Task.CompletedTask;
        }, Options.Create(new TenantContextOptions
        {
            TenantClaimTypes = ["organization"],
            DefaultTenantId = "other-public-tenant"
        }), NullLogger<TenantContextMiddleware>.Instance);

        await middleware.InvokeAsync(context);

        called.Should().BeTrue();
        tenant.TenantId.Should().Be(tenantId);
        tenant.Source.Should().Be(TenantContextSource.Claim);
        tenant.RequireTenantId(out _, out _).Should().Be(tenantId is not null);
    }

    private static ScopedJobTokenRequest Request(JobResourceAccess access) => new(
        "callback-owner", "tenant-a", ["admin"], [], JobId,
        [new JobResourceScopeEntry("beta", "Beta Layer", access)], DateTimeOffset.UtcNow.AddMinutes(10))
    { Attempt = 1 };

    private sealed class CallbackFixture : IDisposable
    {
        private readonly MemoryCache _cache = new(new MemoryCacheOptions());
        public ScopedJobTokenIssuer Issuer { get; }
        public WebApplicationFactory<Program> Factory { get; }
        public ExecutionJobRecord? Job { get; set; }
        public string Token { get; private set; } = string.Empty;
        public bool StoreUnavailable { get; set; }

        private CallbackFixture()
        {
            Issuer = new ScopedJobTokenIssuer(_cache, NullLogger<ScopedJobTokenIssuer>.Instance);
            var jobs = Substitute.For<IExecutionJobStore>();
            jobs.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(call => StoreUnavailable
                ? Task.FromException<ExecutionJobRecord?>(new InvalidOperationException("Store unavailable"))
                : Task.FromResult(call.ArgAt<string>(0) == JobId ? Job : null));
            Factory = ServiceRbacTestFixture.CreateFactory(
                static () => new RbacTestLayerCatalog(betaAlsoIncludesAlphaLayer: true))
                .WithWebHostBuilder(builder => builder.ConfigureTestServices(services =>
                {
                    // The data fixture has no provider-backed CRS detector. Queries still
                    // use the production resolver with its standard fixture dependency.
                    services.RemoveAll<ICrsDetectionService>();
                    services.AddSingleton<ICrsDetectionService, NoopCrsDetectionService>();
                    services.RemoveAll<IScopedJobTokenIssuer>();
                    services.AddSingleton<IScopedJobTokenIssuer>(Issuer);
                    services.RemoveAll<IExecutionJobStore>();
                    services.AddSingleton(jobs);
                    // The base data fixture's header-auth scheme is never selected.
                    // Exercise the production API-key selector and real job handler.
                    services.PostConfigureAll<AuthenticationOptions>(options =>
                    {
                        options.DefaultAuthenticateScheme = AuthenticationExtensions.ApiKeyScheme;
                        options.DefaultChallengeScheme = AuthenticationExtensions.ApiKeyScheme;
                        options.DefaultScheme = AuthenticationExtensions.ApiKeyScheme;
                    });
                    services.Configure<ApiKeyAuthenticationOptions>(options =>
                    {
                        options.AdminPassword = AdminPassword;
                        options.DevAuthBypass = "false";
                    });
                }));
        }

        public static async Task<CallbackFixture> CreateAsync(JobResourceAccess access)
        {
            var fixture = new CallbackFixture();
            var request = Request(access);
            fixture.Token = (await fixture.Issuer.IssueAsync(request, CancellationToken.None)).Token;
            fixture.Job = new ExecutionJobRecord
            {
                OperationId = JobId,
                Status = ExecutionJobStatus.Running,
                AttemptCount = 1,
                CreatedAt = DateTimeOffset.UtcNow,
                UpdatedAt = DateTimeOffset.UtcNow,
                Audit = new OperationAuditInfo
                {
                    CustomCodeOwnerScope = new(request.PrincipalId, request.TenantId, request.Roles, request.Grants, request.ResourceScope)
                },
                Spec = new ExecutionJobSpec
                {
                    TargetKind = BatchComputeTargetKind.AwsBatch,
                    Backend = "aws-batch",
                    Kind = ExecutionJobKind.Geoprocessing,
                    WorkloadName = "callback-auth-test",
                    RuntimeProfile = "custom-code",
                    Parameters = new Dictionary<string, string> { ["env.HONUA_JOB_TOKEN"] = fixture.Token }
                }
            };
            return fixture;
        }

        public HttpClient CreateClient()
        {
            var client = Factory.CreateClient();
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", Token);
            client.DefaultRequestHeaders.Add(ScopedJobAuthentication.JobIdHeader, JobId);
            return client;
        }

        public void Dispose()
        {
            Factory.Dispose();
            _cache.Dispose();
        }
    }
}
