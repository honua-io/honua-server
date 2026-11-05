// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.AuditLog.Export;

namespace Honua.Server.Features.Infrastructure.AuditLog.Export;

public sealed class AuditExportOptions
{
    public const string SectionName = "AuditLog:Export";

    public bool Enabled { get; set; }

    public string[] AllowedRegions { get; set; } = [];

    public AuditExportDispatcherOptions Dispatch { get; set; } = new();

    public int RetentionDays { get; set; }

    // The sweep deletes in bounded batches so the append-only lock is held briefly.
    public int RetentionPruneBatchSize { get; set; } = 5000;

    public SplunkHecSinkOptions Splunk { get; set; } = new();

    public SentinelSinkOptions Sentinel { get; set; } = new();

    public S3SinkOptions S3 { get; set; } = new();

    public SyslogSinkOptions Syslog { get; set; } = new();

    public AuditRetentionPolicy ToRetentionPolicy()
        => new()
        {
            RetentionWindow = RetentionDays > 0 ? TimeSpan.FromDays(RetentionDays) : TimeSpan.Zero,
        };
}

public sealed class SplunkHecSinkOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Token { get; set; } = string.Empty;

    public string SourceType { get; set; } = "honua:audit";

    public string? Index { get; set; }

    public string? Region { get; set; }
}

public sealed class SentinelSinkOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string AuthHeaderValue { get; set; } = string.Empty;

    public string LogType { get; set; } = "HonuaAudit";

    public string? Region { get; set; }
}

public sealed class S3SinkOptions
{
    public bool Enabled { get; set; }

    public string Endpoint { get; set; } = string.Empty;

    public string Bucket { get; set; } = string.Empty;

    public string KeyPrefix { get; set; } = "audit/";

    public string? AuthHeaderValue { get; set; }

    public string? Region { get; set; }
}

public sealed class SyslogSinkOptions
{
    public bool Enabled { get; set; }

    public string Host { get; set; } = string.Empty;

    public int Port { get; set; } = 514;

    public bool UseTcp { get; set; }

    public int Facility { get; set; } = 13;

    public string? Region { get; set; }
}
