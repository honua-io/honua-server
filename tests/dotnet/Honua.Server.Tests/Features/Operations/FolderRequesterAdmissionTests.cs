// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Domain;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Core.Features.MultiTenancy.Abstractions;
using Honua.Infrastructure.Security;
using Honua.Infrastructure.Authentication;
using Honua.ControlPlane;
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    [IntegrationTheory]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    [InlineData("dry-run")]
    [InlineData("tenant")]
    [InlineData("operation")]
    public async Task FolderCreate_MismatchedSuppliedPlan_PreservesSpecificRefusal(string mismatch)
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create",
            TenantId = "public",
            SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "InvalidPlan", ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            var invalid = payload with
            {
                DryRun = mismatch == "dry-run",
                TenantId = mismatch == "tenant" ? "other" : payload.TenantId,
                OperationId = mismatch == "operation" ? "folder.delete" : payload.OperationId
            };
            var result = await _fixture.Services.GetRequiredService<IOperationGateway>().CreateApprovalProposalAsync("invalid-" + Guid.NewGuid().ToString("N"), new()
            {
                OperationId = payload.OperationId,
                Kind = OperationClass.AdminConfigChange,
                TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                Plan = new OperationProposalPlan
                {
                    ExecutionPayload = JsonSerializer.Serialize(invalid, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            result.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
            result.Message.Should().Be("The folder proposal payload does not match the requested operation, tenant, or mode.");
            result.ProposalId.Should().BeNull();
        });
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task FolderCreate_RequesterKeyRevokedDuringAdmission_PreservesAuthorityRefusal()
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create",
            TenantId = "public",
            SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "Revoked", ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            var keys = _fixture.Services.GetRequiredService<IAdminApiKeyStore>();
            var validation = await keys.ValidateAsync(_proposer.DefaultRequestHeaders.GetValues("X-API-Key").Single(), CancellationToken.None);
            (await keys.RevokeAsync(validation!.Record.Id, CancellationToken.None)).Should().NotBeNull();
            var result = await _fixture.Services.GetRequiredService<IOperationGateway>().CreateApprovalProposalAsync("revoked-" + Guid.NewGuid().ToString("N"), new()
            {
                OperationId = payload.OperationId,
                Kind = OperationClass.AdminConfigChange,
                TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId,
                Plan = new OperationProposalPlan
                {
                    ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
                }
            });
            result.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
            result.Message.Should().Be(OperationProposerApiKeyAuthority.Refusal);
            result.ProposalId.Should().BeNull();
        });
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task FolderCreate_ReadOnlyCatalogSubmission_PreservesSpecificRefusal()
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create",
            TenantId = "public",
            SchemaName = _fixture.CurrentSchema,
            Parameters = new() { ["name"] = "ReadOnly", ["If-Match"] = before.Etag }
        };
        await WithRequesterContextAsync(payload, async context =>
        {
            using var readOnly = new ServiceCollection()
                .AddSingleton(context.RequestServices.GetRequiredService<ITenantContext>())
                .AddSingleton(context.RequestServices.GetRequiredService<ISchemaContext>()).BuildServiceProvider();
            context.RequestServices = readOnly;
            var plan = new OperationProposalPlan
            {
                Summary = "Create folder",
                ExecutionPayload = JsonSerializer.Serialize(payload, AdminApiOperationApprovalJsonContext.Default.AdminApiOperationApprovalPayload)
            };
            var prepare = () => FolderApprovalAuthorization.PrepareAsync(new OperationGatewayRequest
            {
                OperationId = payload.OperationId,
                Kind = OperationClass.AdminConfigChange,
                TenantId = payload.TenantId,
                RequestedBy = CanonicalSecurityActor.Resolve(context.User)!.ActorId
            }, plan, context, CancellationToken.None);
            var refused = (await prepare.Should().ThrowAsync<FolderProposalRefusal>()).Which;
            refused.Outcome.Should().Be(OperationGatewayOutcome.Failed);
            refused.Message.Should().Be(FolderMutationFailure.ReadOnlyCatalog.Detail);
        });
        (await SnapshotAsync()).Etag.Should().Be(before.Etag);
    }

    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task FolderCreate_MissingRequesterContext_ExplainsRequiredAuthentication()
    {
        var prepare = () => FolderApprovalAuthorization.PrepareAsync(new OperationGatewayRequest
        {
            OperationId = "folder.create",
            Kind = OperationClass.AdminConfigChange
        }, new OperationProposalPlan(), null, CancellationToken.None);
        var refused = (await prepare.Should().ThrowAsync<FolderProposalRefusal>()).Which;
        refused.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
        refused.Message.Should().Be("Folder proposals require an active authenticated requester; background callers cannot create them.");
    }
}
