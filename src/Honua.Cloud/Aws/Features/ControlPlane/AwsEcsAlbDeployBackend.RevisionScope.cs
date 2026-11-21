// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class AwsEcsAlbDeployBackend
{
    internal const string RevisionOutsideServiceFamilyMessage =
        "The requested task definition must be a revision of the family the target service already runs, in the same account and region.";

    internal const string RevisionFormatMessage =
        "AWS ECS/ALB deploy workflows need an immutable release image or a full task definition revision ARN.";

    private static void AppendRevisionFormatPlan(DeployOperationSpec spec, List<string> blockingReasons)
    {
        if (!string.IsNullOrWhiteSpace(spec.DesiredRevision) &&
            !ReleaseImageDigest.IsImmutable(spec.DesiredRevision) &&
            !EcsTaskDefinitionRevision.IsRevisionArn(spec.DesiredRevision))
        {
            blockingReasons.Add(RevisionFormatMessage);
        }
    }

    // A caller-named task definition may only move the service between revisions of its own family;
    // another family carries another task role and network shape. An empty value leaves the
    // service's task definition unchanged, so it needs no check.
    private static bool IsAllowedServiceRevision(string? requested, string? serviceTaskDefinition)
        => string.IsNullOrWhiteSpace(requested) ||
            EcsTaskDefinitionRevision.AreSameFamily(requested, serviceTaskDefinition);

    private async Task<DeployObservation?> RefusePromotionOutsideServiceFamilyAsync(
        WorkflowOperationRecord operation,
        AwsEcsAlbDeployTarget target,
        CancellationToken cancellationToken)
    {
        var spec = operation.Deploy!;
        if (ReleaseImageDigest.IsImmutable(spec.DesiredRevision) || string.IsNullOrWhiteSpace(spec.DesiredRevision))
        {
            return null;
        }

        var service = await EcsClient.DescribeServiceAsync(target.Cluster!, target.CanaryService!, target.Region, cancellationToken)
            .ConfigureAwait(false);
        if (IsAllowedServiceRevision(spec.DesiredRevision, service.TaskDefinitionArn))
        {
            return null;
        }

        Log.RevisionOutsideServiceFamily(logger, operation.OperationId, spec.TargetId);
        return new DeployObservation
        {
            Status = WorkflowOperationStatus.Failed,
            ProviderOperationId = operation.ProviderOperationId,
            ObservedRevision = operation.ObservedState,
            Message = RevisionOutsideServiceFamilyMessage
        };
    }
}
