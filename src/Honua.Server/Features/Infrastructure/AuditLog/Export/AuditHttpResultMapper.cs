// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Net;
using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Shared HTTP-to-<see cref="AuditSinkResult"/> mapping so every HTTP-based audit
/// sink classifies transport failures consistently: HTTP 5xx and 429 (plus
/// timeouts and connection errors) are retryable; other 4xx responses are
/// permanent.
/// </summary>
internal static class AuditHttpResultMapper
{
    /// <summary>
    /// Maps an HTTP response to a sink result.
    /// </summary>
    /// <param name="sinkType">Sink identifier for the error message.</param>
    /// <param name="status">The response status code.</param>
    /// <returns>A sink result.</returns>
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

    /// <summary>
    /// Maps a transport-level exception (connection failure, timeout, cancellation
    /// not requested by the caller) to a retryable failure.
    /// </summary>
    /// <param name="sinkType">Sink identifier for the error message.</param>
    /// <param name="exception">The thrown exception.</param>
    /// <returns>A retryable sink result.</returns>
    public static AuditSinkResult FromTransportException(string sinkType, Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = string.Create(
            CultureInfo.InvariantCulture,
            $"{sinkType} transport error: {exception.Message}");
        return AuditSinkResult.TransientFailure(message);
    }
}
