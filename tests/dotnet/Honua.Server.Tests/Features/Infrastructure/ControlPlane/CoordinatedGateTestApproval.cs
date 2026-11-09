// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Infrastructure.Security;
using Honua.Server.Tests.Features.Infrastructure.ControlPlane;

namespace Honua.Server.Tests.Features.Infrastructure.ControlPlane;

internal static class CoordinatedGateTestApproval
{
    public static async Task<WorkflowOperationRecord?> ApproveAsync(
        IWorkflowOperationStore store, WorkflowOperationRecord operation, string? reason = "approved")
    {
        using var host = new OperationHumanApprovalTestHost();
        var human = await host.CreateHumanAsync();
        var gate = operation.CoordinatedRelease!.CurrentStep;
        host.GateReviewRequest(human, operation);
        var review = await host.Authority.OpenGateReviewAsync(operation, gate, host.Context);
        host.GateApproveRequest(human, operation, review);
        var service = new CoordinatedReleaseControlService([store], host.Authority);
        return await service.ApproveGateAsync(operation.OperationId, gate,
            CanonicalSecurityActor.Resolve(human.Principal)!.ActorId, reason);
    }
}
