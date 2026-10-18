// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Import.Domain;

/// <summary>Server-captured ownership of an import, retained with its progress.</summary>
/// <param name="OwnerActorId">Canonical authenticated actor that accepted the operation.</param>
/// <param name="TenantId">Resolved request tenant, or null when tenant resolution is disabled.</param>
public sealed record ImportOperationOwnership(string OwnerActorId, string? TenantId);
