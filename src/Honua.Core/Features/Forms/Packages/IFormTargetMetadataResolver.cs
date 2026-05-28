// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Forms.Packages;

/// <summary>
/// Resolves the Metadata V2 entities that back a form package target.
/// </summary>
public interface IFormTargetMetadataResolver
{
    /// <summary>
    /// Resolves the service, publication, and resource addressed by a form target.
    /// </summary>
    /// <param name="target">Form target to resolve.</param>
    /// <param name="cancellationToken">Token used to cancel resolution.</param>
    /// <returns>The resolved metadata entities, or null slots for missing entities.</returns>
    Task<FormTargetMetadataResolution> ResolveAsync(
        FormTargetDefinition? target,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Metadata V2 entities resolved for a form target.
/// </summary>
/// <param name="Service">Service that publishes the target resource.</param>
/// <param name="Publication">Service-local publication selected by the form target layer id.</param>
/// <param name="Resource">Canonical resource backing the publication.</param>
/// <param name="StorageLayerId">Storage-layer id used by feature and attachment stores.</param>
public readonly record struct FormTargetMetadataResolution(
    MetadataV2Service? Service,
    MetadataV2Publication? Publication,
    MetadataV2Resource? Resource,
    int? StorageLayerId);
