// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using FluentAssertions;
using Honua.Core.Features.Authorization.Abstractions;
using Honua.Core.Features.Authorization.Domain;
using Honua.Infrastructure.Authentication;
using Honua.TestKit.Attributes;
using Honua.TestKit.Constants;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace Honua.Server.Tests.Infrastructure.Authentication;

/// <summary>
/// End-to-end proof of invariant #6: a hydrated scoped-job principal is authorized
/// by the EXISTING shared RBAC pipeline (<see cref="ServiceDataEditorAuthorization"/>)
/// unchanged. The mint freezes the intersection as <c>data-editor:{service}</c>
/// roles, and the same service-scoped-role check every other request flows through
/// grants in-scope access and denies out-of-scope access — authorization is never
/// forked.
/// </summary>
[SecurityTest]
[Protocol(TestProtocols.FeatureServer)]
[Operation(Operations.Security)]
public sealed class ScopedJobTokenPipelineTests
{
    [UnitTest]
    public async Task HydratedPrincipal_FlowsThroughServiceDataEditorPipeline_EnforcesAttenuation()
    {
        var issuer = CreateIssuer();

        // Submitter could write 'parcels' (and nothing else). Scope requests parcels.
        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: "tenant-A",
                Roles: ["data-editor:parcels"],
                JobId: "gp-pipeline-1",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Write)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-pipeline-1", CancellationToken.None);
        validation.Should().NotBeNull();

        var context = CreateContext(validation!.Principal);

        // In-scope: the existing pipeline GRANTS write to 'parcels' via the frozen
        // service-scoped editor role.
        var inScope = await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "parcels", CancellationToken.None);
        inScope.IsAllowed.Should().BeTrue("the frozen data-editor:parcels role authorizes the in-scope service");

        // Out-of-scope: the SAME pipeline DENIES 'zoning' — the token confers no
        // authority the submitter did not have and the scope did not include.
        var outOfScope = await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "zoning", CancellationToken.None);
        outOfScope.IsAllowed.Should().BeFalse("no grant exists for the out-of-scope service");
    }

    [UnitTest]
    public async Task HydratedReadOnlyPrincipal_IsNotAuthorizedAsServiceEditor()
    {
        var issuer = CreateIssuer();

        // Read-only scope: even though the submitter could write, the scope is read.
        var issuance = await issuer.IssueAsync(
            new ScopedJobTokenRequest(
                PrincipalId: "alice",
                TenantId: null,
                Roles: ["data-editor:parcels"],
                JobId: "gp-pipeline-2",
                ResourceScope: [new JobResourceScopeEntry("parcels", null, JobResourceAccess.Read)],
                ExpiresAt: DateTimeOffset.UtcNow.AddMinutes(10)),
            CancellationToken.None);

        var validation = await issuer.ValidateAsync(issuance.Token, "gp-pipeline-2", CancellationToken.None);
        var context = CreateContext(validation!.Principal);

        // The read-only principal carries NO data-editor role, so the write-oriented
        // service-editor gate denies it (invariant #3 enforced by the shared pipeline).
        var decision = await ServiceDataEditorAuthorization.EvaluateServiceAccessAsync(
            context, "parcels", CancellationToken.None);
        decision.IsAllowed.Should().BeFalse("a read-only scope confers no service-editor (write) authority");
    }

    private static DefaultHttpContext CreateContext(System.Security.Claims.ClaimsPrincipal principal)
    {
        var services = new ServiceCollection();
        services.Configure<RbacOptions>(_ => { });

        return new DefaultHttpContext
        {
            RequestServices = services.BuildServiceProvider(),
            User = principal
        };
    }

    private static ScopedJobTokenIssuer CreateIssuer()
        => new(new MemoryCache(new MemoryCacheOptions()), NullLogger<ScopedJobTokenIssuer>.Instance);
}
