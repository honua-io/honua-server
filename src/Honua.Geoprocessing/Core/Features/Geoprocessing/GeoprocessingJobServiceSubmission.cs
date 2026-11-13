// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Geoprocessing.Abstractions;
using Honua.Core.Features.Geoprocessing.Domain;
using Honua.Infrastructure.MultiTenancy;
using Honua.Infrastructure.Security;

namespace Honua.Geoprocessing;

internal sealed partial class GeoprocessingJobService
{
    private static async Task<bool> ConfirmKeyedReplayAsyncCore(
        IExecutionJobStore jobStore,
        (string JobId, string? LegacyTenantJobId, string ResolvedKey) identity,
        CancellationToken cancellationToken)
    {
        if (await jobStore.GetAsync(identity.JobId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        if (identity.LegacyTenantJobId is not null
            && await jobStore.GetAsync(identity.LegacyTenantJobId, cancellationToken).ConfigureAwait(false) is not null)
        {
            return true;
        }

        var priorFormatId = JobOwnershipSecurity.CreatePriorFormatJobId("gp", identity.ResolvedKey);
        return await jobStore.GetAsync(priorFormatId, cancellationToken).ConfigureAwait(false) is not null;
    }

    private static ExecutionJobQuery CreateJobListQuery(
        GeoprocessingJobListFilter filter,
        string? ownerScope,
        TenantReadScope tenantScope)
        => new()
        {
            Kind = ExecutionJobKind.Geoprocessing,
            Statuses = filter.Statuses,
            RequestedBy = ownerScope,
            ApplyTenantScope = true,
            TenantId = tenantScope.TenantId,
            IncludeUnassignedTenant = tenantScope.IncludesLegacy,
            Cursor = filter.Cursor,
            Limit = Math.Clamp(filter.Limit, 1, MaxJobListPageSize)
        };

    private static (GpResourceProfile Profile, double CostWeight) ResolveAdmissionResources(
        AnalysisPlan plan,
        Dictionary<string, string> specParams,
        bool isCustomCode,
        IProcessCatalog processCatalog)
    {
        specParams.Remove(ExecutionAdmissionEvaluator.PartitionKeyParameterKey);
        specParams.Remove(ExecutionAdmissionEvaluator.CostWeightParameterKey);
        // Per-job serverless sizing: the heaviest catalog-derived resource profile across
        // the plan's steps, overridden by any explicit gp.resource.* request values. Resolved
        // BEFORE the admission cost weight below so the partition throttle can charge by
        // the plan's actual catalog-derived resource class, not merely its node count.
        var resourceProfile = ResolveResourceProfile(
            plan,
            specParams,
            isCustomCode,
            processCatalog);
        // A static costWeight=stepCount treats a 1-step raster/native op the same as a
        // 1-step managed op over ten features — neither reflects real resource cost. Charging the
        // catalog-derived vCPU class (Managed=1, Native=2, Raster=4; see GpResourceProfile) makes
        // the partition throttle in ExecutionAdmissionEvaluator admit fewer concurrent heavy jobs
        // per partition than light ones, while never charging less than one unit per plan step.
        var costWeight = (double)Math.Max(plan.Steps.Count, resourceProfile.Vcpus ?? 1);
        return (resourceProfile, costWeight);
    }
}
