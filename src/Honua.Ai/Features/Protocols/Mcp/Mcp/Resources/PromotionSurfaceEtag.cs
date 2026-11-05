// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Deployment.Domain;
using Honua.Core.Features.Publishing.Domain;

namespace Honua.Ai.Protocols.Mcp.Resources;

internal static class PromotionSurfaceEtag
{
    public static string ForPublishedService(PublishedServiceRecord record)
        => Format(record.UpdatedAt.UtcTicks, record.Status.ToString());

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
