// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Monitoring;

/// <summary>
/// The configured export policy for exception details recorded on spans, reachable from
/// providers that cannot reference <c>Honua.ServiceDefaults</c> (honua-server#5475).
/// </summary>
/// <remarks>
/// <c>HonuaTelemetry.ConfigureExceptionRecording</c> installs the policy from
/// <c>Tracing:ExportExceptionDetails</c> and <c>Tracing:MaxExceptionDetailLength</c>. Span event
/// attributes cannot be rewritten once the event is added, so code that puts an exception message
/// on an <see cref="System.Diagnostics.ActivityEvent"/> must take it from here: the span processor
/// that sanitizes span tags never sees event attributes. Until a policy is installed nothing is
/// exported, matching the <c>ExportExceptionDetails = false</c> default.
/// </remarks>
public static class TelemetryExceptionDetailPolicy
{
    private static volatile Func<string?, string?> _exportableMessage = static _ => null;

    /// <summary>
    /// Installs the policy.
    /// </summary>
    /// <param name="exportableMessage">
    /// Maps a raw exception message to the text that may be exported (sanitized and bounded), or
    /// <see langword="null"/> when exception details must not be exported.
    /// </param>
    public static void Configure(Func<string?, string?> exportableMessage)
    {
        ArgumentNullException.ThrowIfNull(exportableMessage);
        _exportableMessage = exportableMessage;
    }

    /// <summary>
    /// Returns the exception message as the configured policy allows it to be exported, or
    /// <see langword="null"/> when details are not exported.
    /// </summary>
    /// <param name="exception">The exception being recorded.</param>
    public static string? ExportableMessage(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        var message = _exportableMessage(exception.Message);
        return string.IsNullOrWhiteSpace(message) ? null : message;
    }
}
