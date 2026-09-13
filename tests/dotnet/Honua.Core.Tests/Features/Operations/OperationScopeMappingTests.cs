// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization.Domain;
using Honua.Core.Features.Operations.Domain;
using Honua.Core.Features.Operations.Services;

namespace Honua.Core.Tests.OperationScopes;

public sealed class OperationScopeMappingTests
{
    [Theory]
    [InlineData("admin.layer.publish", OperatorOperation.Publish)]
    [InlineData("style.apply-preset", OperatorOperation.Publish)]
    [InlineData("admin.connections.create", OperatorOperation.Create)]
    [InlineData("admin.import.upload", OperatorOperation.Create)]
    [InlineData("admin.layer.set-enabled", OperatorOperation.Update)]
    [InlineData("admin.connections.features.refresh", OperatorOperation.Update)]
    [InlineData("admin.connections.delete", OperatorOperation.Delete)]
    [InlineData("admin.import.jobs.cancel", OperatorOperation.Delete)]
    public void TryResolve_AdminApprovalReplay_UsesSemanticOperation(
        string operationId,
        OperatorOperation expected)
    {
        var resolved = OperationScopeMapping.TryResolve(
            new OperationRequest { OperationId = operationId },
            out var operation);

        resolved.Should().BeTrue();
        operation.Should().Be(expected);
    }

    // #3429: the dispatcher refuses every scope-governed submission it cannot map (#4722), so each
    // Studio operation routed through the canonical runtime must resolve, with or without a gateway
    // envelope, to the same operation StudioAuthorizationService uses as its scope ceiling.
    [Theory]
    [InlineData("studio.draft.create", OperatorOperation.Create)]
    [InlineData("studio.draft.update", OperatorOperation.Update)]
    [InlineData("studio.draft.save-version", OperatorOperation.Update)]
    [InlineData("studio.draft.delete", OperatorOperation.Delete)]
    [InlineData("studio.draft.validate", OperatorOperation.Read)]
    [InlineData("studio.draft.preview-plan", OperatorOperation.Read)]
    [InlineData("studio.content.reopen-version", OperatorOperation.Create)]
    [InlineData("studio.content.create-publication-request", OperatorOperation.Publish)]
    [InlineData("studio.content.rollback", OperatorOperation.Rollback)]
    public void TryResolve_StudioRuntimeOperation_ResolvesWithAndWithoutGatewayEnvelope(
        string operationId,
        OperatorOperation expected)
    {
        var routed = new OperationRequest
        {
            OperationId = operationId,
            GatewayRequest = new Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest
            {
                OperationId = operationId,
                Kind = Honua.Core.Features.Guardrails.Domain.OperationClass.StudioDraftMutation,
            },
        };

        OperationScopeMapping.TryResolve(routed, out var routedOperation).Should().BeTrue();
        routedOperation.Should().Be(expected);
        OperationScopeMapping.TryResolve(new OperationRequest { OperationId = operationId }, out var directOperation)
            .Should().BeTrue();
        directOperation.Should().Be(expected);
    }

    [Fact]
    public void TryResolve_UnknownStudioOperation_FailsClosed()
    {
        var routed = new OperationRequest
        {
            OperationId = "studio.content.unknown",
            GatewayRequest = new Honua.Core.Features.ControlPlane.Abstractions.OperationGatewayRequest
            {
                OperationId = "studio.content.unknown",
                Kind = Honua.Core.Features.Guardrails.Domain.OperationClass.StudioDraftMutation,
            },
        };

        OperationScopeMapping.TryResolve(routed, out _).Should().BeFalse();
    }

    [Fact]
    public void TryResolve_UnknownAdminOperation_FailsClosed()
    {
        OperationScopeMapping.TryResolve(
                new OperationRequest { OperationId = "admin.unknown.mutation" },
                out _)
            .Should().BeFalse();
    }
}
