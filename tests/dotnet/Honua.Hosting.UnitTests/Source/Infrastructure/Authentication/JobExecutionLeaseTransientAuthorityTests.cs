// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.Security.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.MultiTenancy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class JobExecutionLeaseTransientAuthorityTests
{
    [Theory]
    [InlineData("original")]
    [InlineData("copy")]
    [InlineData("different-job")]
    [InlineData("metadata-tenant")]
    [InlineData("foreign-current-principal")]
    [InlineData("revoked")]
    [InlineData("no-job-scope")]
    public async Task ExecutionTenant_ComesOnlyFromOriginalCurrentLeaseObjectAndActualJobScope(string mutation)
    {
        using var fixture = await Fixture.CreateAsync("selected-tenant");
        using var targets = new ServiceCollection().AddScoped<RequestTenantContext>()
            .AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>()).BuildServiceProvider(validateScopes: true);
        using var target = targets.CreateScope();
        var copy = new ClaimsPrincipal(fixture.Principal.Identities.Select(identity => identity.Clone()));
        var caller = mutation == "copy" ? copy : fixture.Principal;
        var context = fixture.Context with { CurrentAgentPrincipal = mutation == "foreign-current-principal" ? copy : fixture.Principal };
        if (mutation == "metadata-tenant") context = context with { TenantId = "caller-job-metadata" };
        var job = mutation == "different-job" ? fixture.Job with { OperationId = "foreign-job" } : fixture.Job;
        ((ClaimsIdentity)caller.Identity!).AddClaim(new Claim("tenant_id", "caller-token-tenant"));
        if (mutation == "revoked") await fixture.Keys.RevokeAsync(fixture.KeyId, default);
        async Task Bind()
        {
            using var execution = mutation == "no-job-scope" ? null : JobSecurityScope.BeginExecution(job, context);
            await JobExecutionLeaseTenantContext.BindExecutionTenantAsync(target.ServiceProvider, caller, default);
        }
        if (mutation == "original")
        {
            await Bind();
            Assert.Equal("selected-tenant", target.ServiceProvider.GetRequiredService<RequestTenantContext>().TenantId);
        }
        else
        {
            await Assert.ThrowsAsync<UnauthorizedAccessException>(Bind);
            Assert.False(target.ServiceProvider.GetRequiredService<RequestTenantContext>().IsInitialized);
        }
    }

    [Theory]
    [InlineData("conflicting-tenant")]
    [InlineData(null)]
    public async Task ExecutionTenant_NeverOverwritesConflictingOrExplicitUnscopedContext(string? existing)
    {
        using var fixture = await Fixture.CreateAsync("selected-tenant");
        using var targets = new ServiceCollection().AddScoped<RequestTenantContext>()
            .AddScoped<ITenantContext>(provider => provider.GetRequiredService<RequestTenantContext>()).BuildServiceProvider(validateScopes: true);
        using var target = targets.CreateScope();
        var retained = target.ServiceProvider.GetRequiredService<RequestTenantContext>();
        retained.Set(existing, TenantContextSource.Header);
        using var execution = JobSecurityScope.BeginExecution(fixture.Job, fixture.Context with { CurrentAgentPrincipal = fixture.Principal });
        await Assert.ThrowsAsync<UnauthorizedAccessException>(() => JobExecutionLeaseTenantContext.BindExecutionTenantAsync(target.ServiceProvider, fixture.Principal, default));
        Assert.Equal(existing, retained.TenantId);
    }

    [Fact]
    public async Task TransientAuthority_RequiresActualJobScopeAndFreshStoredCredential()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
        using (JobSecurityScope.Begin(fixture.Context))
        {
            Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
        }
        using (JobSecurityScope.BeginExecution(fixture.Job, fixture.Context))
        {
            Assert.True(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
            await Assert.ThrowsAsync<UnauthorizedAccessException>(() => AgentJobSecurityBridge.RevalidateAsync(fixture.Context with
            {
                ExecutionLease = fixture.Context.ExecutionLease! with { ExpiresAt = fixture.Context.ExecutionLease.ExpiresAt.AddDays(1) }
            }, fixture.Services, default));
            using (JobSecurityScope.Begin(fixture.Context with { Claims = [new("department", "admin")] }))
            {
                Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
            }
            Assert.True(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
            using (JobSecurityScope.Begin(fixture.Context with
            {
                ExecutionLease = fixture.Context.ExecutionLease! with { ExpiresAt = fixture.Context.ExecutionLease.ExpiresAt.AddDays(1) }
            }))
            {
                Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
            }
            await fixture.Keys.RotateAsync(fixture.KeyId, default);
            Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
        }
    }

    [Fact]
    public async Task TransientAuthority_DoesNotExposeOrdinaryAdminOrStoredKeyVersion()
    {
        using var fixture = await Fixture.CreateAsync();
        Assert.False(fixture.Principal.IsInRole("admin"));
        Assert.Equal(AdminApiKeyPermission.AdminAccessLevel.None, AdminApiKeyPermission.ResolveAccessLevel(fixture.Principal));
        Assert.Null(StoredApiKeyCredentialVersion.GetStamped(fixture.Principal));
        Assert.True(AgentCredentialAuthority.IsAgent(fixture.Principal));
        var copy = new ClaimsPrincipal(fixture.Principal.Identities.Select(identity => identity.Clone()));
        using var scope = JobSecurityScope.BeginExecution(fixture.Job, fixture.Context);
        Assert.False(await AgentCredentialAuthority.IsCurrentAsync(copy, fixture.Services, default));
    }

    [Fact]
    public async Task ActualResourceAndOperatorEvaluators_IntersectExactScopeBeforeAdminBypass()
    {
        using var fixture = await Fixture.CreateAsync();
        using var scope = JobSecurityScope.BeginExecution(fixture.Job, fixture.Context);
        var allowed = new MetadataV2Resource { Metadata = new() { Id = "allowed", Name = "0" } };
        var other = allowed with { Metadata = allowed.Metadata with { Id = "other" } };
        Assert.True((await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(fixture.Services, fixture.Principal,
            null, false, allowed, null, AuthorizationOperation.Insert, default)).IsAllowed);
        Assert.False((await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(fixture.Services, fixture.Principal,
            null, false, allowed, null, AuthorizationOperation.Query, default)).IsAllowed);
        Assert.False((await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(fixture.Services, fixture.Principal,
            null, false, other, null, AuthorizationOperation.Insert, default)).IsAllowed);
        var evaluator = new OperatorAuthorizationEvaluator(fixture.Services.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RbacOptions()), NullLogger<OperatorAuthorizationEvaluator>.Instance, fixture.Services);
        Assert.True((await evaluator.EvaluateAsync(fixture.Principal, new()
        { ResourceType = OperatorResourceType.Process, ResourceId = "analysis", Operation = OperatorOperation.Execute })).IsAllowed);
        Assert.False((await evaluator.EvaluateAsync(fixture.Principal, new()
        {
            ResourceType = OperatorResourceType.Process,
            ResourceId = "analysis",
            Operation = OperatorOperation.Execute,
            ApprovalReasonCodes = ["changed-approval-reason"]
        })).IsAllowed);
        Assert.False((await evaluator.EvaluateAsync(fixture.Principal, new()
        { ResourceType = OperatorResourceType.Deployment, ResourceId = "other", Operation = OperatorOperation.Publish })).IsAllowed);
        await fixture.Keys.RevokeAsync(fixture.KeyId, default);
        Assert.False((await AccessPolicyHelpers.EvaluateResourceAccessCoreAsync(fixture.Services, fixture.Principal,
            null, false, allowed, null, AuthorizationOperation.Insert, default)).IsAllowed);
    }

    [Fact]
    public async Task TransientAuthority_RejectsDifferentJobOrModifiedImmutableIntent()
    {
        using var fixture = await Fixture.CreateAsync();
        using (JobSecurityScope.BeginExecution(fixture.Job with { OperationId = "other" }, fixture.Context))
        {
            Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
        }
        using (JobSecurityScope.BeginExecution(fixture.Job with { Spec = fixture.Job.Spec with { Backend = "different" } }, fixture.Context))
        {
            Assert.False(await AgentCredentialAuthority.IsCurrentAsync(fixture.Principal, fixture.Services, default));
        }
    }

    private sealed class Fixture : IDisposable
    {
        internal required ServiceProvider Services { get; init; }
        internal required InMemoryAdminApiKeyStore Keys { get; init; }
        internal required Guid KeyId { get; init; }
        internal required ExecutionJobRecord Job { get; init; }
        internal required JobSecurityContext Context { get; init; }
        internal required ClaimsPrincipal Principal { get; init; }

        internal static async Task<Fixture> CreateAsync(string? tenant = null)
        {
            var keys = new InMemoryAdminApiKeyStore();
            var created = await keys.CreateAsync("worker", ["agent:workspace", "admin:write"], null, null, default);
            var services = new ServiceCollection().AddSingleton<IAdminApiKeyStore>(keys)
                .AddSingleton<IAccessPolicyEvaluator, AccessPolicyEvaluator>()
                .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions())).BuildServiceProvider();
            var original = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(created.Record);
            var selectedTenant = new RequestTenantContext();
            selectedTenant.Set(tenant, TenantContextSource.Claim);
            var context = await AgentJobSecurityBridge.CaptureAsync(original, new RbacOptions(), services, selectedTenant, default);
            var now = DateTimeOffset.UtcNow;
            var job = new ExecutionJobRecord
            {
                OperationId = "job",
                Status = ExecutionJobStatus.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Spec = new() { TargetKind = BatchComputeTargetKind.LocalProcess, Backend = "local", Kind = ExecutionJobKind.Geoprocessing, WorkloadName = "analysis" },
                Audit = new() { RequestedBy = context.OwnerActorId, TenantId = context.TenantId, SubmitterSecurityContext = context }
            };
            var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), TimeProvider.System);
            var lease = protector.ProtectValidatedBinding(job, new()
            {
                Kind = job.Spec.Kind,
                ProviderBinding = "server-derived-closure",
                Resources = [new() { ResourceType = OperatorResourceType.Catalog, ResourceId = "allowed", StorageLayerId = 1, Operation = AuthorizationOperation.Insert }],
                Operations = [new() { ResourceType = OperatorResourceType.Process, ResourceId = "analysis", Operation = OperatorOperation.Execute }]
            }, new() { IsAllowed = true, DecisionId = "canonical-policy", MaximumExpiresAt = now.AddHours(1) }, null, now.AddMinutes(30));
            context = context with { ExecutionLease = lease };
            job = job with { Audit = job.Audit with { SubmitterSecurityContext = context } };
            var binding = protector.UnprotectAndMatchAuthoritativeJob(job);
            var source = await AgentCredentialAuthority.RevalidateLeaseLineageAsync(protector, binding, job, services, default)
                ?? throw new InvalidOperationException("Fixture credential is not current.");
            var principal = JobExecutionLeaseTransientAuthority.Bind(source, protector, binding, job, context, TimeProvider.System,
                async cancellationToken => await AgentCredentialAuthority.RevalidateLeaseLineageAsync(protector, binding, job, services, cancellationToken) is not null);
            return new() { Services = services, Keys = keys, KeyId = created.Record.Id, Job = job, Context = context, Principal = principal };
        }

        public void Dispose() => Services.Dispose();
    }
}
