// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A workspace creation was refused because the owner's active count is at its limit.
/// </summary>
public sealed class WorkspaceQuotaExceededException : InvalidOperationException
{
    /// <summary>Creates a provider-detail-free quota failure.</summary>
    public WorkspaceQuotaExceededException() : base("The active workspace count limit has been reached.")
    {
    }
}
