// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>
/// A workspace write was refused because an owner quota would be exceeded.
/// </summary>
public sealed class WorkspaceQuotaExceededException : InvalidOperationException
{
    /// <summary>Creates a provider-detail-free quota failure.</summary>
    public WorkspaceQuotaExceededException() : base("The active workspace count limit has been reached.")
    {
    }
    /// <summary>Creates a quota failure with a curated, provider-detail-free explanation.</summary>
    public WorkspaceQuotaExceededException(string message) : base(message)
    {
    }
}
