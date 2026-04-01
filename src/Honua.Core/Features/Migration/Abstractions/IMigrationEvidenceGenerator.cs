// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Migration.Domain;

namespace Honua.Core.Features.Migration.Abstractions;

/// <summary>
/// Generates parity and cutover-readiness evidence artifacts for migration workflows.
/// </summary>
public interface IMigrationEvidenceGenerator
{
    /// <summary>
    /// Generates an immutable migration evidence report for the supplied request.
    /// </summary>
    /// <param name="request">Evidence generation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Generated evidence report.</returns>
    Task<MigrationEvidenceReport> GenerateAsync(
        MigrationEvidenceRequest request,
        CancellationToken cancellationToken = default);
}
