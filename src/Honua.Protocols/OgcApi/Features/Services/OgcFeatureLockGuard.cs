// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Collaboration;

namespace Honua.Protocols.Ogc.Api.Features.Services;

internal static class OgcFeatureLockGuard
{
    public const string ProblemType = "https://honua.io/problems/feature-locked";

    public static async Task<IResult?> RejectIfLockedAsync(
        HttpContext context,
        MetadataV2Service? service,
        MetadataV2Layer publication,
        int layerId,
        long objectId,
        string operation,
        CancellationToken cancellationToken)
    {
        var description = await DescribeConflictAsync(
            context, service, publication, layerId, objectId, operation, cancellationToken).ConfigureAwait(false);

        return description is null
            ? null
            : Results.Problem(
                statusCode: 423,
                title: "Locked",
                type: ProblemType,
                detail: description);
    }

    public static async Task<string?> DescribeConflictAsync(
        HttpContext context,
        MetadataV2Service? service,
        MetadataV2Layer publication,
        int layerId,
        long objectId,
        string operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(publication);

        var conflict = await FeatureEditLockEnforcement.EvaluateRequestAsync(
            context,
            FeatureEditLockEnforcement.ResolveServiceName(service, publication),
            FeatureEditLockEnforcement.ResolveLayerId(publication, layerId),
            objectId,
            operation,
            cancellationToken).ConfigureAwait(false);

        return conflict is null ? null : FeatureEditLockEnforcement.Describe(conflict);
    }
}
