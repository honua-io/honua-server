// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// Maps a normalized <see cref="OperateEvent"/> onto its wire shape (<see cref="OperateEventResponse"/>).
/// Shared by the REST Operate timeline (<c>GET /api/v1/admin/observability/events</c>) and the realtime
/// <c>operate-events</c> hub group (#2554) so a live-pushed event is byte-identical to the same event read
/// back from the timeline API — the reconnect gap-fill contract relies on that identity.
/// </summary>
internal static class OperateEventResponseMapper
{
    /// <summary>Projects the domain event onto the camel-cased, kind/severity-lowercased wire shape.</summary>
    /// <param name="value">The normalized operate event.</param>
    /// <returns>The wire response.</returns>
    public static OperateEventResponse Map(OperateEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        return new OperateEventResponse
        {
            EventId = value.EventId,
            Kind = ToCamel(value.Kind.ToString()),
            Severity = value.Severity.ToString().ToLowerInvariant(),
            OccurredAt = value.OccurredAt,
            Title = value.Title,
            Summary = value.Summary,
            ServiceId = value.ServiceId,
            LayerId = value.LayerId,
            ObjectId = value.ObjectId,
            Actor = value.Actor,
            CorrelationId = value.CorrelationId,
            TraceId = value.TraceId,
            RequestId = value.RequestId,
            OperationId = value.OperationId,
            ReleaseId = value.ReleaseId,
            ReplicaId = value.ReplicaId,
            ChangeSetId = value.ChangeSetId,
            ResourceRef = value.ResourceRef,
            ProviderLinks = value.ProviderLinks?.Select(link => new OperateProviderLinkResponse
            {
                Provider = link.Provider,
                Label = link.Label,
                Url = link.Url
            }).ToArray(),
            DetailsJson = value.DetailsJson
        };
    }

    private static string ToCamel(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        return char.ToLowerInvariant(value[0]) + value[1..];
    }
}
