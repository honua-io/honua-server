// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.Infrastructure.OpenData;

/// <summary>
/// Deployment-level Open Data publication capability options.
/// </summary>
internal sealed class OpenDataPublicationOptions
{
    /// <summary>
    /// Configuration section name.
    /// </summary>
    public const string SectionName = "OpenData";

    /// <summary>
    /// Enables anonymous Open Data/DCAT/STAC publication projection for this deployment.
    /// Defaults off so production hosts must explicitly opt in.
    /// </summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Enables the process-local fallback store when no durable provider registers
    /// <see cref="Honua.Core.Features.OpenData.Abstractions.IOpenDataStore"/>.
    /// </summary>
    public bool UseInMemoryStore { get; set; }
}
