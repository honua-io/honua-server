// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;

namespace Honua.ControlPlane;

internal sealed partial class AwsLambdaGitOpsDeployBackend
{
    public bool RequiresCheckpoint(WorkflowOperationRecord operation)
        => operation.Deploy is { } spec && ReleaseImageDigest.IsImmutable(spec.DesiredRevision);

    public async Task<DeployCheckpoint> PrepareCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing.");
        var client = imageClient ?? throw new InvalidOperationException("Lambda image convergence is not configured.");
        if (!ReleaseImageDigest.IsImmutable(spec.DesiredRevision) || spec.CanaryRamp is not null ||
            !TryResolveCanaryWeightFraction(spec.Parameters, out var weight, out _) || weight.HasValue)
        {
            throw new InvalidOperationException("Lambda image convergence requires an immutable image and no canary traffic.");
        }

        var function = ResolveFunctionName(spec);
        var alias = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var serviceUrl = ResolveServiceUrl(spec.Parameters);
        var before = await client.GetAliasAsync(function, alias, region, serviceUrl, cancellationToken).ConfigureAwait(false);
        if (before.HasWeightedTraffic) throw new InvalidOperationException("Lambda alias has weighted traffic; image convergence requires a stable alias.");
        var candidate = await client.PublishImageAsync(function, spec.DesiredRevision, region, serviceUrl, 60, 1000, cancellationToken).ConfigureAwait(false);
        var after = await client.GetAliasAsync(function, alias, region, serviceUrl, cancellationToken).ConfigureAwait(false);
        if (before != after) throw new InvalidOperationException("Lambda alias changed during image preparation.");
        return new DeployCheckpoint { CandidateRevision = candidate, StableRevision = before.Version, OriginalServiceRevision = before.RevisionId };
    }

    public async Task<DeployObservation> ActivateCheckpointAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy ?? throw new InvalidOperationException("Deploy metadata is missing.");
        var client = imageClient ?? throw new InvalidOperationException("Lambda image convergence is not configured.");
        var alias = await client.GetAliasAsync(ResolveFunctionName(spec), ResolveAliasName(spec.Parameters), ResolveRegion(spec.Parameters), ResolveServiceUrl(spec.Parameters), cancellationToken).ConfigureAwait(false);
        if (alias.HasWeightedTraffic || alias.Version != spec.CurrentRevision || alias.RevisionId != operation.ObservedState || string.IsNullOrWhiteSpace(operation.ProviderOperationId))
            throw new InvalidOperationException("Lambda alias changed after the checkpoint.");
        return new DeployObservation { Status = WorkflowOperationStatus.Submitted, ProviderOperationId = operation.ProviderOperationId, Message = "Published Lambda version is ready for pre-cutover checks." };
    }

    private async Task<DeployObservation> ObserveImageAsync(WorkflowOperationRecord operation, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy!;
        var client = imageClient ?? throw new InvalidOperationException("Lambda image convergence is not configured.");
        var alias = await client.GetAliasAsync(ResolveFunctionName(spec), ResolveAliasName(spec.Parameters), ResolveRegion(spec.Parameters), ResolveServiceUrl(spec.Parameters), cancellationToken).ConfigureAwait(false);
        if (operation.Status == WorkflowOperationStatus.RollbackRequested)
        {
            if (!alias.HasWeightedTraffic && alias.Version == spec.CurrentRevision)
                return await CompleteLambdaRollbackAsync(operation, spec, alias.Version, operation.ProviderOperationId, cancellationToken).ConfigureAwait(false);
            return new DeployObservation { Status = WorkflowOperationStatus.ManualInterventionRequired, Message = "Lambda alias changed during rollback." };
        }
        if (spec.TrafficExposedAt is null && !alias.HasWeightedTraffic && alias.Version == spec.CurrentRevision && alias.RevisionId == operation.ObservedState)
            return new DeployObservation { Status = WorkflowOperationStatus.Submitted, ProviderOperationId = operation.ProviderOperationId, PromotionRecommended = true, Message = "Published Lambda version is ready for pre-cutover checks." };
        if (spec.TrafficExposedAt is not null && !alias.HasWeightedTraffic && alias.Version == operation.ProviderOperationId)
            return new DeployObservation { Status = WorkflowOperationStatus.Succeeded, ProviderOperationId = operation.ProviderOperationId, ObservedRevision = spec.DesiredRevision, Message = "Lambda alias points to the approved image version." };
        return new DeployObservation { Status = WorkflowOperationStatus.ManualInterventionRequired, Message = "Lambda alias changed outside this deployment." };
    }

    private async Task<DeployObservation> MoveImageAliasAsync(WorkflowOperationRecord operation, bool rollback, CancellationToken cancellationToken)
    {
        var spec = operation.Deploy!;
        var client = imageClient ?? throw new InvalidOperationException("Lambda image convergence is not configured.");
        var function = ResolveFunctionName(spec);
        var aliasName = ResolveAliasName(spec.Parameters);
        var region = ResolveRegion(spec.Parameters);
        var url = ResolveServiceUrl(spec.Parameters);
        var alias = await client.GetAliasAsync(function, aliasName, region, url, cancellationToken).ConfigureAwait(false);
        var desired = rollback ? spec.CurrentRevision : operation.ProviderOperationId;
        var expected = rollback ? operation.ProviderOperationId : spec.CurrentRevision;
        if (string.IsNullOrWhiteSpace(desired) || alias.HasWeightedTraffic)
            throw new InvalidOperationException("Lambda checkpoint or alias routing is invalid.");
        if (alias.Version != desired)
        {
            if (alias.Version != expected || (!rollback && alias.RevisionId != operation.ObservedState))
                throw new InvalidOperationException("Lambda alias changed outside this deployment.");
            await client.MoveAliasAsync(function, aliasName, desired, alias.RevisionId, region, url, cancellationToken).ConfigureAwait(false);
        }
        return new DeployObservation { Status = rollback ? WorkflowOperationStatus.RollbackRequested : WorkflowOperationStatus.Reconciling, ProviderOperationId = operation.ProviderOperationId, Message = rollback ? "Lambda rollback was requested." : "Lambda image cutover was submitted." };
    }
}
