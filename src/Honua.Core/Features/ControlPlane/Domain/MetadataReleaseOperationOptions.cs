// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.ControlPlane.Domain;

/// <summary>
/// Configuration for durable metadata release operation records.
/// </summary>
public sealed class MetadataReleaseOperationOptions
{
    /// <summary>
    /// Configuration section name for metadata release operation settings.
    /// </summary>
    public const string SectionName = "ControlPlane:MetadataRelease";

    /// <summary>
    /// Retention for metadata release operation records and their package-ID index entries.
    /// </summary>
    public TimeSpan OperationRetention { get; set; } = TimeSpan.FromDays(30);
}
