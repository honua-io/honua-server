// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Claims;
using FluentAssertions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Core.Features.WorkflowPackages.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Features.WorkflowPackages;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace Honua.Server.Tests.Features.WorkflowPackages;

public sealed class WorkflowPackageOwnershipTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Store_PreservesFirstOwnerIncludingLegacyUnknown_AndCopiesVersion(bool legacy)
    {
        var store = new InMemoryWorkflowPackageStore();
        var owner = legacy ? null : new WorkflowPackageOwnership { OwnerActorId = "actor:first", TenantId = "tenant:first" };
        var package = new WorkflowPackage
        {
            PackageId = "package", Name = "Package", Graph = new WorkflowGraph { Nodes = [] },
            CreatedAt = DateTimeOffset.UtcNow, UpdatedAt = DateTimeOffset.UtcNow, Ownership = owner
        };
        await store.SavePackageAsync(package);
        var saved = await store.SavePackageAsync(package with
        {
            Ownership = new WorkflowPackageOwnership { OwnerActorId = "actor:other", TenantId = "tenant:other" }
        });
        saved.Ownership.Should().Be(owner);
        var hash = WorkflowPackageGraphHasher.Compute(package.Graph);
        var version = await store.CreateVersionAsync(package.PackageId, hash, package.Graph,
            WorkflowPackageValidationResult.Success(hash), "other-display-name");
        version.Ownership.Should().Be(owner);
    }

    [Fact]
    public async Task Service_StampsCanonicalActor_AndCannotLaunderMetadataIntoOwnership()
    {
        var store = new InMemoryWorkflowPackageStore();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("trusted-tenant");
        var service = new WorkflowPackageService(store, null!, null!, TimeProvider.System,
            NullLogger<WorkflowPackageService>.Instance,
            authorSecurityContextCapturer: new WorkflowAuthorSecurityContextCapturer(tenantContext: tenant));
        var principal = new ClaimsPrincipal(new ClaimsIdentity(
            [new Claim(ClaimTypes.NameIdentifier, "subject", ClaimValueTypes.String, "trusted-issuer"),
             new Claim(ClaimTypes.Name, "display-name")], "authenticated-test"));
        var request = new SaveWorkflowPackageRequest
        {
            Name = "Test", Graph = new WorkflowGraph { Nodes = [] },
            Metadata = new Dictionary<string, string> { ["ownerActorId"] = "forged", ["tenantId"] = "forged" }
        };
        var saved = await service.SavePackageAsync(request, principal, CancellationToken.None);
        saved.Ownership.Should().NotBeNull();
        saved.Ownership!.OwnerActorId.Should().Be(CanonicalSecurityActor.Resolve(principal)!.ActorId);
        saved.Ownership.TenantId.Should().Be("trusted-tenant");
        var other = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "other")], "authenticated-test"));
        var updated = await service.SavePackageAsync(request with { PackageId = saved.PackageId }, other, CancellationToken.None);
        updated.Ownership.Should().Be(saved.Ownership);
    }

    [Fact]
    public void MissingTenantResolverOrUnstableActorCannotCreateTrustedOwnership()
    {
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.NameIdentifier, "owner")], "Bearer"));
        new WorkflowAuthorSecurityContextCapturer().CaptureOwnership(principal).Should().BeNull();
        var tenant = Substitute.For<ITenantContext>();
        tenant.TenantId.Returns("trusted-tenant");
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        new WorkflowAuthorSecurityContextCapturer(tenantContext: tenant).CaptureOwnership(anonymous).Should().BeNull();
    }
}
