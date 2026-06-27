// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

/// <summary>
/// Configuration for the audit-trail SIEM export feature (#2157). Bound from the
/// <c>AuditLog:Export</c> configuration section. Disabled by default so the
/// audit trail behaves exactly as before unless an operator opts in.
/// </summary>
public sealed class AuditExportOptions
{
    /// <summary>Configuration section name: <c>AuditLog:Export</c>.</summary>
    public const string SectionName = "AuditLog:Export";

    /// <summary>Master switch for audit export. Default <c>false</c> (off).</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Allowed destination regions for data-residency enforcement. Empty means
    /// unrestricted; otherwise sinks outside this set are dead-lettered.
    /// </summary>
    public string[] AllowedRegions { get; set; } = [];

    /// <summary>Retry/backoff tuning for the export dispatcher.</summary>
    public AuditExportDispatcherOptions Dispatch { get; set; } = new();

    /// <summary>
    /// Retention window in days. <c>0</c> (or less) means retain forever. When
    /// positive, a background sweep (<c>AuditRetentionPruneService</c>) prunes
    /// audit records older than this window via the registered
    /// <see cref="AuditRetentionPolicy"/> pruner (#509).
    /// </summary>
    public int RetentionDays { get; set; }

    /// <summary>
    /// Number of expired audit rows deleted per batch by the retention sweep.
    /// The sweep prunes in bounded chunks across short transactions so the
    /// append-only guard's <c>ACCESS EXCLUSIVE</c> lock is held only briefly per
    /// batch (never across an entire large first sweep), letting inline audit
    /// inserts interleave. Values &lt;= 0 fall back to the pruner default
    /// (<c>5000</c>).
    /// </summary>
    public int RetentionPruneBatchSize { get; set; } = 5000;

    /// <summary>Splunk HTTP Event Collector sink configuration.</summary>
    public SplunkHecSinkOptions Splunk { get; set; } = new();

    /// <summary>Microsoft Sentinel / Log Analytics sink configuration.</summary>
    public SentinelSinkOptions Sentinel { get; set; } = new();

    /// <summary>S3-compatible object-storage sink configuration.</summary>
    public S3SinkOptions S3 { get; set; } = new();

    /// <summary>Syslog (UDP/TCP, CEF payload) sink configuration.</summary>
    public SyslogSinkOptions Syslog { get; set; } = new();

    /// <summary>
    /// Builds an <see cref="AuditRetentionPolicy"/> from <see cref="RetentionDays"/>.
    /// </summary>
    /// <returns>The retention policy (unbounded when <see cref="RetentionDays"/> &lt;= 0).</returns>
    public AuditRetentionPolicy ToRetentionPolicy()
        => new()
        {
            RetentionWindow = RetentionDays > 0 ? TimeSpan.FromDays(RetentionDays) : TimeSpan.Zero,
        };
}

/// <summary>Options for <c>SplunkHecAuditSink</c>.</summary>
public sealed class SplunkHecSinkOptions
{
    /// <summary>Whether this sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Base URL of the Splunk HEC endpoint (e.g. <c>https://splunk.example:8088</c>).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>HEC token used in the <c>Authorization: Splunk &lt;token&gt;</c> header.</summary>
    public string Token { get; set; } = string.Empty;

    /// <summary>Splunk <c>sourcetype</c> assigned to forwarded events.</summary>
    public string SourceType { get; set; } = "honua:audit";

    /// <summary>Optional Splunk index to route events into.</summary>
    public string? Index { get; set; }

    /// <summary>Declared destination region for residency enforcement, or null.</summary>
    public string? Region { get; set; }
}

/// <summary>Options for <c>SentinelAuditSink</c>.</summary>
public sealed class SentinelSinkOptions
{
    /// <summary>Whether this sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Data Collector / Logs Ingestion endpoint URL.</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Value sent verbatim in the <c>Authorization</c> header (shared-key or bearer).</summary>
    public string AuthHeaderValue { get; set; } = string.Empty;

    /// <summary>Custom log type / table name sent in the <c>Log-Type</c> header.</summary>
    public string LogType { get; set; } = "HonuaAudit";

    /// <summary>Declared destination region for residency enforcement, or null.</summary>
    public string? Region { get; set; }
}

/// <summary>Options for <c>S3AuditSink</c>.</summary>
public sealed class S3SinkOptions
{
    /// <summary>Whether this sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>S3-compatible service endpoint (e.g. <c>https://s3.us-east-1.amazonaws.com</c>).</summary>
    public string Endpoint { get; set; } = string.Empty;

    /// <summary>Target bucket name.</summary>
    public string Bucket { get; set; } = string.Empty;

    /// <summary>Object key prefix; a date partition and a GUID file name are appended.</summary>
    public string KeyPrefix { get; set; } = "audit/";

    /// <summary>
    /// Optional value for the <c>Authorization</c> header. When omitted, request
    /// signing (SigV4) is expected to be delegated to a sidecar/proxy or to a
    /// pre-signed endpoint — the sink intentionally does not sign requests.
    /// </summary>
    public string? AuthHeaderValue { get; set; }

    /// <summary>Declared destination region for residency enforcement, or null.</summary>
    public string? Region { get; set; }
}

/// <summary>Options for <c>SyslogAuditSink</c>.</summary>
public sealed class SyslogSinkOptions
{
    /// <summary>Whether this sink is enabled.</summary>
    public bool Enabled { get; set; }

    /// <summary>Syslog collector host name or IP.</summary>
    public string Host { get; set; } = string.Empty;

    /// <summary>Syslog collector port (default 514).</summary>
    public int Port { get; set; } = 514;

    /// <summary>Use TCP transport instead of the default UDP.</summary>
    public bool UseTcp { get; set; }

    /// <summary>Syslog facility code (default 13 = log audit).</summary>
    public int Facility { get; set; } = 13;

    /// <summary>Declared destination region for residency enforcement, or null.</summary>
    public string? Region { get; set; }
}
