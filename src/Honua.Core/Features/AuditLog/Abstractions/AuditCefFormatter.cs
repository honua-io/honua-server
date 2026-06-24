// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text;

namespace Honua.Core.Features.AuditLog.Abstractions;

/// <summary>
/// Formats an audit record as a single ArcSight Common Event Format (CEF) line
/// for SIEM ingestion (#509).
/// </summary>
/// <remarks>
/// <para>
/// CEF is the line-oriented format consumed natively by ArcSight, Splunk,
/// QRadar, and most enterprise SIEMs. The header is
/// <c>CEF:0|Vendor|Product|Version|SignatureId|Name|Severity|Extension</c>;
/// pipes in the header and <c>=</c>/<c>\</c> in the extension are escaped per the
/// CEF spec so the line round-trips through a SIEM parser unambiguously.
/// </para>
/// </remarks>
public static class AuditCefFormatter
{
    private const string Vendor = "Honua";
    private const string Product = "Honua Server";
    private const string Version = "1";

    /// <summary>
    /// Formats a single audit record as a CEF line (no trailing newline).
    /// </summary>
    /// <param name="record">The audit record.</param>
    /// <returns>A CEF-formatted line.</returns>
    public static string Format(AuditEventRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        var builder = new StringBuilder(256);
        builder.Append("CEF:0|")
            .Append(EscapeHeader(Vendor)).Append('|')
            .Append(EscapeHeader(Product)).Append('|')
            .Append(EscapeHeader(Version)).Append('|')
            .Append(EscapeHeader(record.Action)).Append('|')
            .Append(EscapeHeader(record.EventType.ToString())).Append('|')
            .Append(Severity(record.Outcome)).Append('|');

        // CEF extension: space-separated key=value pairs.
        AppendExtension(builder, "rt", record.Timestamp.ToString("o", CultureInfo.InvariantCulture));
        AppendExtension(builder, "externalId", record.AuditId.ToString(CultureInfo.InvariantCulture));
        AppendExtension(builder, "suser", record.Actor);
        AppendExtension(builder, "suid", record.ActorType.ToString());
        AppendExtension(builder, "act", record.Action);
        AppendExtension(builder, "outcome", record.Outcome.ToString());
        AppendExtension(builder, "cs1Label", "resourceType");
        AppendExtension(builder, "cs1", record.ResourceType);
        AppendExtension(builder, "cs2Label", "resourceId");
        AppendExtension(builder, "cs2", record.ResourceId);
        AppendExtension(builder, "cs3Label", "correlationId");
        AppendExtension(builder, "cs3", record.CorrelationId);
        AppendExtension(builder, "src", record.RemoteIp);
        AppendExtension(builder, "requestClientApplication", record.UserAgent);
        AppendExtension(builder, "msg", string.IsNullOrEmpty(record.Details) ? null : record.Details);

        return builder.ToString().TrimEnd();
    }

    // CEF severity 0-10. Denied/Failure outcomes are security-relevant so they
    // map higher than a routine success.
    private static int Severity(AuditOutcome outcome) => outcome switch
    {
        AuditOutcome.Denied => 7,
        AuditOutcome.Failure => 6,
        _ => 3,
    };

    private static void AppendExtension(StringBuilder builder, string key, string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return;
        }

        builder.Append(key).Append('=').Append(EscapeExtension(value)).Append(' ');
    }

    private static string EscapeHeader(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("|", "\\|", StringComparison.Ordinal);

    private static string EscapeExtension(string value)
        => value.Replace("\\", "\\\\", StringComparison.Ordinal)
            .Replace("=", "\\=", StringComparison.Ordinal)
            .Replace("\n", "\\n", StringComparison.Ordinal)
            .Replace("\r", "\\r", StringComparison.Ordinal);
}
