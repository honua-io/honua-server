// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Observability.Domain;

namespace Honua.Core.Features.Observability.Abstractions;

public interface IInvestigationStore
{
    Task<InvestigationRecord> CreateAsync(
        string title,
        string createdBy,
        string? summary,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> GetAsync(string investigationId, CancellationToken cancellationToken = default);

    Task<InvestigationPage> ListAsync(InvestigationFilter filter, CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> UpdateAsync(
        string investigationId,
        string? title,
        string? summary,
        InvestigationStatus? status,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> AddPinAsync(
        string investigationId,
        string eventRef,
        OperateEventKind eventKind,
        DateTimeOffset occurredAt,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> RemovePinAsync(
        string investigationId,
        long pinId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> AddLinkAsync(
        string investigationId,
        InvestigationResourceKind resourceKind,
        string resourceId,
        string? note,
        string actor,
        DateTimeOffset createdAt,
        CancellationToken cancellationToken = default);

    Task<InvestigationRecord?> RemoveLinkAsync(
        string investigationId,
        long linkId,
        DateTimeOffset updatedAt,
        CancellationToken cancellationToken = default);
}
