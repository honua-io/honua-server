// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;

namespace Honua.Server.Features.Admin.Models;

public sealed class TenantResponse
{
    public required string TenantId { get; init; }

    public required string DisplayName { get; init; }

    public required string Status { get; init; }

    public string? Plan { get; init; }

    public DateTimeOffset CreatedAt { get; init; }

    public DateTimeOffset UpdatedAt { get; init; }

    public DateTimeOffset? SuspendedAt { get; init; }

    public DateTimeOffset? DeletedAt { get; init; }
}

public sealed class CreateTenantRequest
{
    [Required]
    [StringLength(128, MinimumLength = 1)]
    public required string TenantId { get; init; }

    [StringLength(256)]
    public string? DisplayName { get; init; }

    [StringLength(64)]
    public string? Plan { get; init; }
}

public sealed class TenantUsageRecordResponse
{
    public required string TenantId { get; init; }

    public required long RequestCount { get; init; }

    public required DateTimeOffset CapturedAt { get; init; }
}

public sealed class TenantUsageResponse
{
    public required IReadOnlyList<TenantUsageRecordResponse> Records { get; init; }
}
