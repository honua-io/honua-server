// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Realtime;
using Honua.Infrastructure.Authentication;

namespace Honua.Server.Features.Realtime;

internal sealed partial class RealtimeDispatcher
{
    private static int InternalPriority(string family) => family switch
    {
        "security" => 0,
        "catalog" => 1,
        _ => 2
    };

    private Task RefreshSecurityAsync(RealtimeEvent realtimeEvent, CancellationToken cancellationToken)
    {
        if (realtimeEvent.Family.Name == RealtimeLogFamilies.Catalog)
        {
            return security!.RefreshAsync(null, cancellationToken);
        }

        var payload = realtimeEvent.Payload;
        if (payload.ValueKind != JsonValueKind.Object
            || !payload.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() is not ("policy-changed" or "principal-changed" or "credential-revoked")
            || !payload.TryGetProperty("subject", out var subject) || subject.ValueKind != JsonValueKind.String)
        {
            // An unrecognized event cannot justify retaining the old policy snapshot.
            return security!.RefreshAsync(null, cancellationToken);
        }

        var credentialKind = payload.TryGetProperty("credentialKind", out var credential) && credential.ValueKind == JsonValueKind.String
            ? credential.GetString() : null;
        return security!.RefreshAsync(new LiveStreamInvalidation(kind.GetString()!, subject.GetString()!, credentialKind), cancellationToken);
    }
}
