// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Server.Features.Protocols.Mcp.Resources;

/// <summary>
/// Monotonic weak ETag derived from promotion-surface lifecycle timestamps.
/// Lets agents observe lifecycle transitions by polling ETag without depending on
/// a subscription surface; the deployment transition audit trail already supplies
/// the strict-monotonic ordering guarantee.
/// </summary>
internal static class PromotionSurfaceEtag
{
    /// <summary>
    /// Builds a weak ETag for a <see cref="PublishedServiceRecord"/> from its
    /// <see cref="PublishedServiceRecord.UpdatedAt"/> stamp.
    /// </summary>
    public static string ForPublishedService(PublishedServiceRecord record)
        => Format(record.UpdatedAt.UtcTicks, record.Status.ToString());

    /// <summary>
    /// Builds a weak ETag for a <see cref="Deployment"/> from the last transition
    /// timestamp (falling back to <see cref="Deployment.UpdatedAt"/> when the audit
    /// trail is empty). The transition audit trail is append-only, so successive
    /// reads always observe a non-decreasing ETag.
    /// </summary>
    public static string ForDeployment(Deployment deployment)
    {
        var latestTransitionAt = deployment.Transitions.Count > 0
            ? deployment.Transitions[^1].At
            : deployment.UpdatedAt;
        var anchor = latestTransitionAt > deployment.UpdatedAt
            ? latestTransitionAt
            : deployment.UpdatedAt;
        return Format(anchor.UtcTicks, deployment.Status.ToString());
    }

    private static string Format(long ticks, string status)
        => string.Create(
            CultureInfo.InvariantCulture,
            $"W/\"{ticks:x}-{status}\"");
}
