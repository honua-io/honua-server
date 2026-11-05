// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

internal static class AuditHttpResultMapper
{
    public static AuditSinkResult FromStatus(string sinkType, HttpStatusCode status)
    {
        var code = (int)status;
        if (code is >= 200 and < 300)
        {
            return AuditSinkResult.Success();
        }

        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{sinkType} responded with HTTP {code}.");

        if (code >= 500 || status == HttpStatusCode.TooManyRequests)
        {
            return AuditSinkResult.TransientFailure(message);
        }

        return AuditSinkResult.PermanentFailure(message);
    }

    public static AuditSinkResult FromTransportException(string sinkType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{sinkType} transport error: {exception.Message}");
        return AuditSinkResult.TransientFailure(message);
    }
}
