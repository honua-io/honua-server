// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Server.Features.Admin.Models;

internal sealed class InvestigationResponse
{
    public required string InvestigationId { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? Summary { get; init; }

    public required IReadOnlyList<InvestigationPinResponse> Pins { get; init; }

    public required IReadOnlyList<InvestigationLinkResponse> Links { get; init; }
}

internal sealed class InvestigationSummaryResponse
{
    public required string InvestigationId { get; init; }

    public required string Title { get; init; }

    public required string Status { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }

    public required DateTimeOffset UpdatedAt { get; init; }

    public string? Summary { get; init; }

    public required int PinCount { get; init; }

    public required int LinkCount { get; init; }
}

internal sealed class InvestigationPinResponse
{
    public required long PinId { get; init; }

    public required string EventRef { get; init; }

    public required string EventKind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

internal sealed class InvestigationLinkResponse
{
    public required long LinkId { get; init; }

    public required string ResourceKind { get; init; }

    public required string ResourceId { get; init; }

    public string? Note { get; init; }

    public required DateTimeOffset CreatedAt { get; init; }

    public required string CreatedBy { get; init; }
}

internal sealed class InvestigationPageResponse
{
    public required IReadOnlyList<InvestigationSummaryResponse> Items { get; init; }

    public string? NextCursor { get; init; }
}

internal sealed class CreateInvestigationRequest
{
    public required string Title { get; init; }

    public string? Summary { get; init; }
}

internal sealed class UpdateInvestigationRequest
{
    public string? Title { get; init; }

    public string? Summary { get; init; }

    public string? Status { get; init; }
}

internal sealed class AddInvestigationPinRequest
{
    public required string EventRef { get; init; }

    public required string EventKind { get; init; }

    public required DateTimeOffset OccurredAt { get; init; }

    public string? Note { get; init; }
}

internal sealed class AddInvestigationLinkRequest
{
    public required string ResourceKind { get; init; }

    public required string ResourceId { get; init; }

    public string? Note { get; init; }
}

internal sealed class InvestigationPinAddAuditDetails
{
    public required string EventRef { get; init; }
}

internal sealed class InvestigationLinkAddAuditDetails
{
    public required string ResourceKind { get; init; }
    public required string ResourceId { get; init; }
}
