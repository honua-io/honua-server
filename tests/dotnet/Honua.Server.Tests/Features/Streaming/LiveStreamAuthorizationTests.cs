// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Server.Startup;
using Honua.Server.Features.Streaming;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;
using Microsoft.Extensions.Options;
using Honua.TestKit.Attributes;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute;
using System.Security.Claims;

namespace Honua.Server.Tests.Features.Streaming;

public sealed partial class LiveStreamAuthorizationTests
{
    public enum CredentialEnd
    {
        TokenRevoked,
        Expired,
        BackingKeyRevoked
    }

    [UnitTheory]
    [InlineData(CredentialEnd.TokenRevoked)]
    [InlineData(CredentialEnd.Expired)]
    [InlineData(CredentialEnd.BackingKeyRevoked)]
    public async Task Filter_EventDrivenCredentialRevocationAndExpiryCancelWithoutPeriodicAuthentication(CredentialEnd end)
    {
        var expire = end == CredentialEnd.Expired;
        var backingCredential = end == CredentialEnd.BackingKeyRevoked;
        var authentication = Substitute.For<IAuthenticationService>();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        registrations.AddSingleton<LiveStreamSecurityRegistry>();
        await using var services = registrations.BuildServiceProvider();
        var registry = services.GetRequiredService<LiveStreamSecurityRegistry>();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")],
            PortalTokenAuthenticationExtensions.PortalTokenScheme));
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>()).Returns(
            AuthenticateResult.Success(new AuthenticationTicket(context.User, PortalTokenAuthenticationExtensions.PortalTokenScheme)));
        context.Features.Set(new LiveStreamCredentialFeature(new("PortalToken", "token-hash"),
            expire ? DateTimeOffset.UtcNow.AddSeconds(-1) : DateTimeOffset.UtcNow.AddMinutes(5),
            backingCredential ? new("AdminApiKey", "source-key") : null));
        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async invocation =>
        {
            if (!expire)
            {
                await registry.RefreshAsync(backingCredential
                    ? new("credential-revoked", "source-key", "AdminApiKey")
                    : new("credential-revoked", "token-hash", "PortalToken"), CancellationToken.None);
            }
            var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var registration = invocation.HttpContext.RequestAborted.Register(() => cancelled.TrySetResult());
            await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Results.Empty;
        });
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await authentication.Received(expire ? 0 : 1).AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>());
    }

    [UnitTest]
    public async Task Filter_SyntheticTicketRetainsExpiredAdmission_DoesNotAdmitEndpoint()
    {
        var authentication = Substitute.For<IAuthenticationService>();
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        registrations.AddSingleton<LiveStreamSecurityRegistry>();
        await using var services = registrations.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "alice")], "context.User"));
        context.Features.Set(new LiveStreamAdmissionFeature(PortalTokenAuthenticationExtensions.PortalTokenScheme,
            DateTimeOffset.UtcNow.AddSeconds(-1)));
        var admitted = false;

        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), _ =>
        {
            admitted = true;
            return ValueTask.FromResult<object?>(Results.Empty);
        });

        admitted.Should().BeFalse();
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        await authentication.DidNotReceive().AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>());
    }

    [UnitTest]
    public async Task Filter_RevokedBeforeRegistration_DoesNotAdmitEndpoint()
    {
        await using var services = CreateServices(registrations => registrations.AddSingleton<LiveStreamSecurityRegistry>());
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var token = await IssueAsync(issuer, "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        context.User = (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Principal!;
        await issuer.RevokeAsync(token.Token, CancellationToken.None);
        await services.GetRequiredService<LiveStreamSecurityRegistry>().RefreshAsync(null, CancellationToken.None);
        var admitted = false;

        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), _ =>
        {
            admitted = true;
            return ValueTask.FromResult<object?>(Results.Empty);
        });

        admitted.Should().BeFalse("revocation consumed before registration must be observed by admission authentication");
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
    }

    public enum KeyTransport
    {
        Query,
        Header
    }

    public enum AdmissionTicket
    {
        Handler,
        Synthetic
    }

    [UnitTheory]
    [InlineData(KeyTransport.Query, AdmissionTicket.Handler)]
    [InlineData(KeyTransport.Header, AdmissionTicket.Handler)]
    [InlineData(KeyTransport.Query, AdmissionTicket.Synthetic)]
    [InlineData(KeyTransport.Header, AdmissionTicket.Synthetic)]
    public async Task Filter_PortalTransportApiKey_PreservesAdmissionHandler(KeyTransport transport, AdmissionTicket ticket)
    {
        var syntheticTicket = ticket == AdmissionTicket.Synthetic;
        await using var services = await CreatePortalApiKeyServicesAsync();
        using var scope = services.CreateScope();
        var context = await PresentPortalApiKeyAsync(scope.ServiceProvider, transport, ticket);
        var initial = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);
        initial.Succeeded.Should().BeTrue();
        if (syntheticTicket)
        {
            context.Features.Get<LiveStreamAdmissionFeature>()!.ExpiresAt.Should().Be(initial.Properties!.ExpiresUtc);
            initial.Properties.ExpiresUtc.Should().NotBeNull();
        }
        context.User = initial.Principal!;
        CanonicalSecurityActor.StampRequestBinding(context.User, "tenant-a");
        context.Features.Set<IAuthenticateResultFeature>(new PolicyAuthenticationFeature
        {
            AuthenticateResult = syntheticTicket
                ? AuthenticateResult.Success(new AuthenticationTicket(context.User, "context.User")) : initial
        });
        var admitted = false;

        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            await context.Features.Get<LiveStreamPolicyRefreshFeature>()!.RevalidateAsync!(
                static _ => Task.FromResult(true), CancellationToken.None);
            admitted = !context.RequestAborted.IsCancellationRequested;
            return Results.Empty;
        });

        admitted.Should().BeTrue("portal-carried keys require the portal handler even when their actor scheme is ApiKey");
    }

    [UnitTest]
    public async Task Filter_EndpointDenialCancelsPendingCheck_DoesNotStartAnSseResponse()
    {
        var authentication = Substitute.For<IAuthenticationService>();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>()).Returns(async call =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<HttpContext>(0).RequestAborted);
            return AuthenticateResult.NoResult();
        });
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        await using var services = registrations.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "denied")],
            PortalTokenAuthenticationExtensions.PortalTokenScheme));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        output.Length.Should().Be(0, "normal cancellation must not overwrite a typed denial with a terminal stream frame");
    }

    [UnitTest]
    public async Task Filter_RevalidationFailsWhileTheEndpointIsStillRunning_KeepsTheEndpointDenial()
    {
        // The runner can starve the endpoint continuation past the revalidation
        // deadline. That check then fails closed while the endpoint is still about
        // to answer, which is the ordering the flaky run above hit by accident.
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>()).Returns(async call =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, call.ArgAt<HttpContext>(0).RequestAborted);
            return AuthenticateResult.NoResult();
        });
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        await using var services = registrations.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "denied")],
            PortalTokenAuthenticationExtensions.PortalTokenScheme));
        using var output = new MemoryStream();
        context.Response.Body = output;
        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async invocation =>
        {
            var deauthorized = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var registration = invocation.HttpContext.RequestAborted.Register(() => deauthorized.TrySetResult());
            await deauthorized.Task.WaitAsync(TimeSpan.FromSeconds(30));
            return Results.StatusCode(StatusCodes.Status403Forbidden);
        });
        ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        output.Length.Should().Be(0, "a response the endpoint never started carries its outcome in the status code, not an SSE frame");
        await ((IResult)result!).ExecuteAsync(context);
        context.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        output.Length.Should().Be(0, "executing the typed denial must preserve an empty response body");
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Filter_RevalidationCancelsEndpoint_EndsOnlyAnAlreadyStartedStream(bool responseStarted, bool swallowCancellation)
    {
        var authentication = Substitute.For<IAuthenticationService>();
        authentication.AuthenticateAsync(Arg.Any<HttpContext>(), Arg.Any<string>())
            .Returns(AuthenticateResult.NoResult());
        var registrations = new ServiceCollection();
        registrations.AddLogging();
        registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(
            PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        registrations.AddSingleton(authentication);
        await using var services = registrations.BuildServiceProvider();
        var context = new DefaultHttpContext { RequestServices = services };
        context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "revoked")],
            PortalTokenAuthenticationExtensions.PortalTokenScheme));
        using var output = new MemoryStream();
        var response = Substitute.For<IHttpResponseFeature>();
        response.HasStarted.Returns(responseStarted);
        response.StatusCode = StatusCodes.Status200OK;
        context.Features.Set(response);
        context.Response.Body = output;

        var result = await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async invocation =>
        {
            try
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, invocation.HttpContext.RequestAborted)
                    .WaitAsync(TimeSpan.FromSeconds(30));
            }
            catch (OperationCanceledException) when (swallowCancellation && invocation.HttpContext.RequestAborted.IsCancellationRequested)
            {
                // SSE endpoints can catch cancellation during their first write
                // and return normally before the response has started.
            }
            return Results.Empty;
        });

        if (responseStarted)
        {
            result.Should().BeSameAs(Results.Empty);
            System.Text.Encoding.UTF8.GetString(output.ToArray()).Should().Be(
                "event: status\ndata: {\"status\":\"error\",\"code\":\"access-revoked\"}\n\n");
        }
        else
        {
            await ((IResult)result!).ExecuteAsync(context);
            context.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            ((IStatusCodeHttpResult)result!).StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
            output.Length.Should().Be(0, "cancellation before streaming starts must return an HTTP denial");
        }
    }

    [UnitTest]
    public async Task Revalidate_RevokedRealToken_DoesNotReuseCachedRequestAuthentication()
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var token = await IssueAsync(issuer, "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        var initial = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);
        initial.Succeeded.Should().BeTrue();
        context.User = initial.Principal!;
        CanonicalSecurityActor.StampRequestBinding(context.User, "tenant-a");
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeTrue();

        var admitted = false;
        context.Features.Set<IAuthenticateResultFeature>(new PolicyAuthenticationFeature
        {
            AuthenticateResult = AuthenticateResult.Success(new AuthenticationTicket(context.User, "context.User"))
        });
        await new LiveStreamAuthorizationFilter().InvokeAsync(
            EndpointFilterInvocationContext.Create(context), _ =>
            {
                admitted = true;
                return ValueTask.FromResult<object?>(Results.Empty);
            });
        admitted.Should().BeTrue("a framework-normalized scheme must resolve to its registered authentication handler");
        var identity = (ClaimsIdentity)context.User.Identity!;
        foreach (var claim in identity.FindAll(CanonicalSecurityActor.AuthenticationSchemeClaim).ToArray()) { identity.RemoveClaim(claim); }
        admitted = false;
        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), _ =>
        {
            admitted = true;
            return ValueTask.FromResult<object?>(Results.Empty);
        });
        admitted.Should().BeTrue("a synthetic authorization ticket must not hide an otherwise registered handler on a tenantless principal");

        await issuer.RevokeAsync(token.Token, CancellationToken.None);
        (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Succeeded
            .Should().BeTrue("ASP.NET caches authentication on the original request; this is the regression trigger");
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeFalse("the fresh handler must observe production token revocation");
    }

    [Theory]
    [InlineData("tenant-b", "reader")]
    [InlineData("tenant-a", "admin")]
    [Trait("Category", "Unit")]
    [Trait("Tier", "Fast")]
    public async Task Revalidate_ReplacementChangesTenantOrRoles_RejectsCapturedScope(string tenant, string role)
    {
        await using var services = CreateServices();
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var original = await IssueAsync(issuer, "tenant-a", ["reader"]);
        var replacement = await IssueAsync(issuer, tenant, [role]);
        var context = Context(scope.ServiceProvider, original.Token);
        var initial = await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme);
        initial.Succeeded.Should().BeTrue();
        context.User = initial.Principal!;
        context.Request.QueryString = new QueryString("?token=" + replacement.Token);
        (await LiveStreamAuthorizationFilter.RevalidateAsync(context, PortalTokenAuthenticationExtensions.PortalTokenScheme, CancellationToken.None))
            .Should().BeFalse("a stream's tenant and permission scope must remain bound to its admitted identity");
    }

    [UnitTest]
    public void CatalogFreshness_LiveReadRequiresCurrentRevisionDespiteRecentProbe()
    {
        var context = new DefaultHttpContext();
        context.Request.Method = HttpMethods.Get;
        context.Request.Path = "/api/v1/features/stream";
        var requirement = new HttpMetadataGraphFreshnessRequirement(new HttpContextAccessor { HttpContext = context });
        requirement.RequireCurrentRevision.Should().BeFalse();

        context.Features.Set(new LiveStreamPolicyRefreshFeature());

        requirement.RequireCurrentRevision.Should().BeTrue("catalog revocation must bypass the ordinary GET probe interval");
    }

    [UnitTest]
    public async Task Filter_SharingRevoked_ReloadsAudienceBeforePolicyRefresh()
    {
        var audience = Substitute.For<IPortalAudienceSnapshotProvider>();
        audience.GetServiceIdsAsync("subject-a", Arg.Is<TenantReadScope>(read => read.TenantId == "tenant-a"), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlySet<string>>(new HashSet<string>()));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        await using var services = CreateServices(registrations =>
        {
            registrations.AddSingleton(audience);
            registrations.AddSingleton(tenant);
            registrations.AddSingleton<LiveStreamSecurityRegistry>();
        });
        using var scope = services.CreateScope();
        var token = await IssueAsync(services.GetRequiredService<IPortalTokenIssuer>(), "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        context.User = (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Principal!;
        PortalAudienceSnapshot.Set(context, new HashSet<string> { "svc" });
        var registry = services.GetRequiredService<LiveStreamSecurityRegistry>();

        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            context.Features.Get<LiveStreamPolicyRefreshFeature>()!.SetRefresh(_ =>
                Task.FromResult(PortalAudienceSnapshot.HasService(context, "svc")));
            await registry.RefreshAsync(null, CancellationToken.None);
            context.RequestAborted.IsCancellationRequested.Should().BeTrue("the refreshed audience no longer grants this service");
            PortalAudienceSnapshot.HasService(context, "svc").Should().BeFalse();
            return Results.Empty;
        });
        await audience.Received(2).GetServiceIdsAsync("subject-a", Arg.Is<TenantReadScope>(read => read.TenantId == "tenant-a"), Arg.Any<CancellationToken>());
    }

    [UnitTest]
    public async Task Filter_AdmissionAndInvalidationSerializeAudienceRefreshes()
    {
        var gated = new GatedAudience();
        var audience = Substitute.For<IPortalAudienceSnapshotProvider>();
        audience.GetServiceIdsAsync("subject-a", Arg.Is<TenantReadScope>(read => read.TenantId == "tenant-a"), Arg.Any<CancellationToken>())
            .Returns(call => gated.ReadAsync(call.Arg<CancellationToken>()));
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("tenant-a");
        await using var services = CreateServices(registrations =>
        {
            registrations.AddSingleton(audience);
            registrations.AddSingleton(tenant);
            registrations.AddSingleton<LiveStreamSecurityRegistry>();
        });
        using var scope = services.CreateScope();
        var token = await IssueAsync(services.GetRequiredService<IPortalTokenIssuer>(), "tenant-a", ["reader"]);
        var context = Context(scope.ServiceProvider, token.Token);
        context.User = (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Principal!;
        var registry = services.GetRequiredService<LiveStreamSecurityRegistry>();
        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            var policies = context.Features.Get<LiveStreamPolicyRefreshFeature>()!;
            policies.SetRefresh(_ => Task.FromResult(PortalAudienceSnapshot.HasService(context, "svc")));
            var admission = policies.RevalidateAsync!(static _ => Task.FromResult(true), CancellationToken.None);
            await gated.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var invalidation = registry.RefreshAsync(null, CancellationToken.None);
            await Task.Delay(100);
            gated.Resume.TrySetResult();
            await Task.WhenAll(admission, invalidation);
            gated.Overlapped.Should().BeFalse();
            PortalAudienceSnapshot.HasService(context, "svc").Should().BeFalse();
            context.RequestAborted.IsCancellationRequested.Should().BeTrue();
            return Results.Empty;
        });
    }

    [UnitTest]
    public async Task Filter_ReplayRevokedBackingKeyWithWarmCache_DeliversNoEventsBeforeSecurityConsumer()
    {
        var keyStore = new InMemoryAdminApiKeyStore();
        await using var services = CreateServices(registrations =>
        {
            registrations.AddSingleton<IAdminApiKeyStore>(keyStore);
            registrations.AddSingleton(Options.Create(new PortalTokenAuthenticationOptions { SourceRevalidationSeconds = 600 }));
            registrations.AddSingleton<IPortalTokenSourceValidator, PortalTokenSourceValidator>();
            registrations.AddSingleton<LiveStreamSecurityRegistry>();
        });
        using var scope = services.CreateScope();
        var issuer = services.GetRequiredService<IPortalTokenIssuer>();
        var key = await keyStore.CreateAsync("stream", ["admin:*"], DateTimeOffset.UtcNow.AddHours(1),
            "test", CancellationToken.None);
        var issuance = await IssueFromManagedKeyAsync(issuer, key.Record);
        var context = Context(scope.ServiceProvider, issuance.Token);
        context.Response.Body = new MemoryStream();
        context.User = (await context.AuthenticateAsync(PortalTokenAuthenticationExtensions.PortalTokenScheme)).Principal!;
        var store = CreateStoreWithOneChange();
        using var manager = new FeatureStreamSessionManager(Options.Create(new FeatureStreamOptions()),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<FeatureStreamSessionManager>.Instance,
            Honua.Server.Tests.Infrastructure.Telemetry.TestTelemetry.CreateFeatureStreamMetrics());

        await new LiveStreamAuthorizationFilter().InvokeAsync(EndpointFilterInvocationContext.Create(context), async _ =>
        {
            // Keep the consumer idle: replay must independently re-read the backing grant.
            await keyStore.RevokeAsync(key.Record.Id, CancellationToken.None);
            Assert.NotNull(await issuer.ValidateAsync(issuance.Token,
                new PortalTokenBinding("https://stream-proof.example/", null), CancellationToken.None));
            var security = CreateReplayRevalidatedSecurity(context);
            var replay = typeof(FeatureStreamEndpoints).GetMethod("ReplayToSseAsync",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
            var task = (Task<long>)replay.Invoke(null,
                [context.Response, store, 0L, 100, Microsoft.Extensions.Logging.Abstractions.NullLogger.Instance,
                 Guid.NewGuid(), manager, context.RequestAborted,
                 new StreamSubscriptionFilter(subscriberSecurity: security), null, 0L, null])!;
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(0, context.Response.Body.Length);
            return Results.Empty;
        });
    }

    private static async Task<ServiceProvider> CreatePortalApiKeyServicesAsync()
    {
        var store = new InMemoryAdminApiKeyStore();
        var key = await store.CreateAsync("stream-reader", ["read:test"], DateTimeOffset.UtcNow.AddMinutes(10), "test", CancellationToken.None);
        return CreateServices(registrations =>
        {
            registrations.AddSingleton(new PortalApiKey(key.Key));
            registrations.AddSingleton(new ApiKeyAuthenticationDependencies(
                Options.Create(new ApiKeyAuthenticationOptions { EnvironmentName = "Production", AdminPassword = "Stream-proof-bootstrap-password-42!" }),
                secretResolver: null, adminApiKeyStore: store));
            registrations.AddAuthentication().AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(AuthenticationExtensions.ApiKeyScheme, _ => { });
        });
    }

    private sealed record PortalApiKey(string Key);

    private static async Task<HttpContext> PresentPortalApiKeyAsync(
        IServiceProvider services, KeyTransport transport, AdmissionTicket ticket)
    {
        var key = services.GetRequiredService<PortalApiKey>().Key;
        var context = Context(services, key);
        if (transport == KeyTransport.Header)
        {
            context.Request.QueryString = QueryString.Empty;
            context.Request.Headers["X-Esri-Authorization"] = "Bearer " + key;
        }

        if (ticket == AdmissionTicket.Synthetic)
        {
            var options = Substitute.For<IOptionsMonitor<PortalTokenAuthenticationOptions>>();
            options.CurrentValue.Returns(new PortalTokenAuthenticationOptions { Enabled = true });
            await new PortalTokenAuthenticationMiddleware(_ => Task.CompletedTask, options).InvokeAsync(context);
            context.User.Identity!.IsAuthenticated.Should().BeTrue();
        }

        return context;
    }

    private static Task<PortalTokenIssuance> IssueFromManagedKeyAsync(IPortalTokenIssuer issuer, AdminApiKeyRecord key)
        => issuer.IssueAsync(new PortalTokenIssueRequest(
            "alice", null, null, ["admin"], PortalTokenClientType.Referer, "https://stream-proof.example/",
            DateTimeOffset.UtcNow.AddMinutes(30), Source: new PortalCredentialSource(
                PortalCredentialSourceKind.ManagedApiKey, Reference: key.Id.ToString("D"),
                Version: PortalCredentialSourceVersion.ForManagedKey(key))), CancellationToken.None);

    private static FakeFeatureChangeEventStore CreateStoreWithOneChange()
    {
        var store = new FakeFeatureChangeEventStore();
        store.Append(new Honua.Infrastructure.Events.FeatureChangeEventRequest
        {
            ServiceId = "test",
            LayerId = 0,
            ObjectId = 1,
            Operation = "update",
            Protocol = "test",
            RequestId = "request"
        });
        return store;
    }

    // Replay re-runs the stream's admission check, as the endpoint wires it for event-driven streams.
    private static StreamSubscriberSecurity CreateReplayRevalidatedSecurity(HttpContext context)
    {
        var policies = context.Features.Get<LiveStreamPolicyRefreshFeature>()!;
        var security = new StreamSubscriberSecurity(["test"],
            new Dictionary<(string, int), StreamLayerReadPolicy> { [("test", 0)] = new([], []) },
            new Dictionary<(string, int), StreamLayerReadPolicy>());
        security.EnableEventDrivenRefresh();
        security.ReplayRevalidateAsync = async ct =>
        {
            await policies.RevalidateAsync!(static _ => Task.FromResult(true), ct);
            context.RequestAborted.ThrowIfCancellationRequested();
        };
        return security;
    }

    // Grants "svc" on the first read, holds the second read open, and revokes it on later reads.
    private sealed class GatedAudience
    {
        private int _running;
        private int _calls;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource Resume { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public bool Overlapped { get; private set; }

        public async Task<IReadOnlySet<string>> ReadAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _running) != 1)
            {
                Overlapped = true;
            }

            try
            {
                var call = Interlocked.Increment(ref _calls);
                if (call == 2)
                {
                    Started.TrySetResult();
                    await Resume.Task.WaitAsync(cancellationToken);
                }

                return call <= 2 ? new HashSet<string> { "svc" } : new HashSet<string>();
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }
    }

    private static ServiceProvider CreateServices(Action<IServiceCollection>? configure = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddMemoryCache();
        services.AddSingleton<Honua.Infrastructure.Security.ISecurityTokenStore, Honua.Infrastructure.Security.InMemorySecurityTokenStore>();
        services.AddSingleton<IPortalTokenIssuer, PortalTokenIssuer>();
        services.AddAuthentication(PortalTokenAuthenticationExtensions.PortalTokenScheme)
            .AddScheme<AuthenticationSchemeOptions, PortalTokenAuthenticationHandler>(PortalTokenAuthenticationExtensions.PortalTokenScheme, _ => { });
        configure?.Invoke(services);
        return services.BuildServiceProvider();
    }

    private sealed class PolicyAuthenticationFeature : IAuthenticateResultFeature
    {
        public AuthenticateResult? AuthenticateResult { get; set; }
    }

    private static Task<PortalTokenIssuance> IssueAsync(IPortalTokenIssuer issuer, string tenant, string[] roles) =>
        issuer.IssueAsync(new PortalTokenIssueRequest("subject-a", "Subject A", tenant, roles,
            PortalTokenClientType.Referer, "https://stream-proof.example/", DateTimeOffset.UtcNow.AddMinutes(1)), CancellationToken.None);

    private static DefaultHttpContext Context(IServiceProvider services, string token)
    {
        var context = new DefaultHttpContext { RequestServices = services };
        context.Request.QueryString = new QueryString("?token=" + token);
        context.Request.Headers.Referer = "https://stream-proof.example/";
        return context;
    }
}
