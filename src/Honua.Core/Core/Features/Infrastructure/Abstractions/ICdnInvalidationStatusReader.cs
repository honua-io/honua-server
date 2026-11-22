// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Abstractions;

// Submission and completion are separate: callers retain every provider ID across task restarts.
public interface ICdnInvalidationStatusReader
{
    Task<CdnInvalidationStatusResult> ReadAsync(CdnInvalidationReceipt receipt, CancellationToken cancellationToken = default);
}

public sealed record CdnInvalidationReceipt(string ResourceId, string InvalidationId);

public enum CdnInvalidationStatus
{
    NotConfigured,
    Pending,
    Completed,
    Failed
}

public sealed record CdnInvalidationStatusResult(CdnInvalidationStatus Status, string? Detail = null);
