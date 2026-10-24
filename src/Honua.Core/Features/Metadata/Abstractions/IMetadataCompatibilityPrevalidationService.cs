// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Generates Metadata v2 release-package compatibility reports for target environments.
/// </summary>
public interface IMetadataCompatibilityPrevalidationService
{
    /// <summary>
    /// Generates an environment-scoped compatibility report for a persisted or inline release package.
    /// </summary>
    /// <param name="request">Prevalidation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Compatibility report.</returns>
    Task<MetadataCompatibilityReport> PrevalidateAsync(
        MetadataCompatibilityPrevalidationRequest request,
        CancellationToken cancellationToken = default);
}
