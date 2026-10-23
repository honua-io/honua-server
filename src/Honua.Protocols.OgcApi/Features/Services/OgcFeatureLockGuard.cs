// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Infrastructure.Collaboration;

namespace Honua.Protocols.Ogc.Api.Features.Services;

/// <summary>
/// OGC API Features glue for collaborative-editing lock enforcement (#4402): resolves
/// the lease namespace from the collection's publication and shapes a blocked edit as
/// an RFC 7807 <c>423 Locked</c> problem.
/// </summary>
/// <remarks>
/// 423 is the status a lock-aware client already understands, and it is the status the
/// GeoServices per-edit <c>FeatureLocked</c> code maps from
/// (<c>GeoServicesEditErrorCodes.FromHttpStatus</c>), so the two protocols describe the
/// same refusal the same way.
/// </remarks>
internal static class OgcFeatureLockGuard
{
    /// <summary>Problem type identifying a collaborative-editing lock conflict.</summary>
    public const string ProblemType = "https://honua.io/problems/feature-locked";

    /// <summary>
    /// Returns a <c>423 Locked</c> problem when another editor currently holds a lease
    /// on the feature; otherwise <see langword="null"/> and the edit proceeds.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="service">The service the collection is published through.</param>
    /// <param name="publication">The publication backing the collection being edited.</param>
    /// <param name="layerId">The resolved layer id, used when the publication has no layer index.</param>
    /// <param name="objectId">The feature's server-side OBJECTID.</param>
    /// <param name="operation">The edit operation name surfaced on the conflict.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The problem result to return, or <see langword="null"/> to continue.</returns>
    public static async Task<IResult?> RejectIfLockedAsync(
        HttpContext context,
        MetadataV2Service? service,
        MetadataV2Publication publication,
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

    /// <summary>
    /// Returns the client-facing description of the lock conflict blocking this edit,
    /// or <see langword="null"/> when the edit may proceed. Used by the batch path,
    /// which reports per-operation failures rather than a whole-request problem.
    /// </summary>
    /// <param name="context">The request being served.</param>
    /// <param name="service">The service the collection is published through.</param>
    /// <param name="publication">The publication backing the collection being edited.</param>
    /// <param name="layerId">The resolved layer id, used when the publication has no layer index.</param>
    /// <param name="objectId">The feature's server-side OBJECTID.</param>
    /// <param name="operation">The edit operation name surfaced on the conflict.</param>
    /// <param name="cancellationToken">A token to observe for cancellation.</param>
    /// <returns>The conflict description, or <see langword="null"/> to continue.</returns>
    public static async Task<string?> DescribeConflictAsync(
        HttpContext context,
        MetadataV2Service? service,
        MetadataV2Publication publication,
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
