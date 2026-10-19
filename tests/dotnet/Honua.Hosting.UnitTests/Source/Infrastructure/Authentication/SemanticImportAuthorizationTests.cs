// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using Honua.Core.Features.Authorization;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Hosting.UnitTests.Infrastructure.Authentication;

public sealed class SemanticImportAuthorizationTests
{
    [Theory]
    [InlineData("admin:read", false, true)]
    [InlineData("admin:read", true, false)]
    [InlineData("admin:write", true, true)]
    public async Task NamedAdminPolicy_UsesTypedImportSemanticsWithoutHttpContext(string grant, bool write, bool allowed)
    {
        var keys = new InMemoryAdminApiKeyStore();
        var record = await keys.CreateAsync("operator", [grant], null, null, default);
        using var services = Services(keys);
        var principal = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(record.Record);
        Assert.Equal(allowed, await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "configured-target", write, default));
    }

    [Fact]
    public async Task BoundedLease_UsesExactImportScopeAndCurrentStoredCredentialWithoutAdminRoleProjection()
    {
        var keys = new InMemoryAdminApiKeyStore();
        var record = await keys.CreateAsync("executor", ["agent:workspace", "admin:write"], null, null, default);
        using var services = Services(keys);
        var source = ApiKeyAuthenticationHandler.CreateStoredKeyPrincipal(record.Record);
        var original = await AgentJobSecurityBridge.CaptureAsync(source, new RbacOptions(), services, null, default);
        var now = DateTimeOffset.UtcNow;
        var job = new ExecutionJobRecord
        {
            OperationId = "typed-import", Status = ExecutionJobStatus.Running, CreatedAt = now, UpdatedAt = now,
            Spec = new() { Kind = ExecutionJobKind.FileImport, TargetKind = BatchComputeTargetKind.LocalProcess,
                Backend = "local", WorkloadName = "file-import" },
            Audit = new() { RequestedBy = original.OwnerActorId, TenantId = original.TenantId, SubmitterSecurityContext = original }
        };
        var scope = new JobExecutionLeaseScope { Kind = job.Spec.Kind, Resources = [], ProviderBinding = "closed-import-plan",
            Operations = [new SemanticImportAuthorizationResource("configured-target", true).OperatorRequest] };
        var protector = new JobExecutionLeaseProtector(new EphemeralDataProtectionProvider(), TimeProvider.System);
        var lease = protector.ProtectValidatedBinding(job, scope,
            new() { IsAllowed = true, DecisionId = "benign-canonical", MaximumExpiresAt = now.AddHours(1) }, null, now.AddMinutes(30));
        var accepted = job with { Audit = job.Audit with { SubmitterSecurityContext = original with { ExecutionLease = lease } } };
        var proof = protector.UnprotectAndMatchAuthoritativeJob(accepted);
        var principal = JobExecutionLeaseTransientAuthority.Bind(source, protector, proof, accepted,
            accepted.Audit.SubmitterSecurityContext!, TimeProvider.System,
            async cancellationToken => await AgentCredentialAuthority.RevalidateLeaseLineageAsync(protector, proof,
                accepted, services, cancellationToken) is not null);
        var execution = accepted.Audit.SubmitterSecurityContext! with { CurrentAgentPrincipal = principal, CredentialExpiresAt = lease.ExpiresAt };
        Assert.False(principal.IsInRole("admin"));
        Assert.False(await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "configured-target", true, default));
        using (JobSecurityScope.BeginExecution(accepted, execution))
        {
            Assert.True(await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "configured-target", true, default));
            Assert.False(await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "other-target", true, default));
            Assert.False(await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "configured-target", false, default));
            var copy = new ClaimsPrincipal(principal.Identities.Select(identity => identity.Clone()));
            Assert.False(await ImportAdminAuthorization.IsAuthorizedAsync(services, copy, "configured-target", true, default));
            _ = await keys.RotateAsync(record.Record.Id, default);
            Assert.False(await ImportAdminAuthorization.IsAuthorizedAsync(services, principal, "configured-target", true, default));
        }
    }

    private static ServiceProvider Services(InMemoryAdminApiKeyStore keys)
    {
        var collection = new ServiceCollection().AddLogging().AddSingleton<IAdminApiKeyStore>(keys)
            .AddSingleton<IOptions<RbacOptions>>(Options.Create(new RbacOptions()))
            .AddSingleton<IOperatorScopeAuthorizer, OperatorScopeAuthorizer>();
        collection.AddApiKeyAuthentication(new ConfigurationBuilder().Build());
        return collection.BuildServiceProvider();
    }
}
