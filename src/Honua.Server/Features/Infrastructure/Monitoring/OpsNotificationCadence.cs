// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Infrastructure.Monitoring;

internal static class OpsNotificationCadence
{
    public static DateTimeOffset NextNotice(OpsFindingSeverity severity, DateTimeOffset firstSeen,
        DateTimeOffset now)
    {
        if (severity != OpsFindingSeverity.Critical)
        {
            return now.AddHours(24);
        }

        if (now < firstSeen.AddMinutes(30))
        {
            return firstSeen.AddMinutes(30);
        }

        if (now < firstSeen.AddHours(2))
        {
            return firstSeen.AddHours(2);
        }

        return now.AddHours(12);
    }
}
