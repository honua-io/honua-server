// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Server.Features.Admin.Models;

internal static class OperateEventResponseMapper
{
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
