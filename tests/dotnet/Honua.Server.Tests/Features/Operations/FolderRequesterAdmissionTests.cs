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
using Honua.Server.Features.Admin;
using Honua.Server.Features.Operations;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.OperationsToolset;

public sealed partial class FolderOperationIntegrationTests
{
    [IntegrationTest]
    [Endpoint("POST /api/v1/operations/{id}/submit")]
    public async Task FolderCreate_ReadOnlyCatalogSubmission_PreservesSpecificRefusal()
    {
        var before = await SnapshotAsync();
        var payload = new AdminApiOperationApprovalPayload
        {
            OperationId = "folder.create", TenantId = "public", SchemaName = _fixture.CurrentSchema,
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
                OperationId = payload.OperationId, Kind = OperationClass.AdminConfigChange, TenantId = payload.TenantId,
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
            OperationId = "folder.create", Kind = OperationClass.AdminConfigChange
        }, new OperationProposalPlan(), null, CancellationToken.None);
        var refused = (await prepare.Should().ThrowAsync<FolderProposalRefusal>()).Which;
        refused.Outcome.Should().Be(OperationGatewayOutcome.Blocked);
        refused.Message.Should().Be("Folder proposals require an active authenticated requester; background callers cannot create them.");
    }
}
