// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.AuditLog.Abstractions;

public interface IAuditLogExporter
{
    IAsyncEnumerable<AuditEventRecord> ExportAsync(
        AuditExportFilter filter,
        CancellationToken cancellationToken = default);
}

public sealed record AuditExportFilter
{
    public DateTimeOffset? From { get; init; }

    public DateTimeOffset? To { get; init; }

    public string? Actor { get; init; }

    public string? ResourceType { get; init; }

    public string? Action { get; init; }

    public int? Limit { get; init; }
}
