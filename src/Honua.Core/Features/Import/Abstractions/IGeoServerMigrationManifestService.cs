// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Translates GeoServer discovery into deterministic migration manifests.
/// </summary>
public interface IGeoServerMigrationManifestService
{
    /// <summary>
    /// Discovers the source GeoServer instance and translates it into a migration manifest.
    /// </summary>
    /// <param name="request">Translation request.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Deterministic migration manifest for review and later replay.</returns>
    Task<MigrationManifest> TranslateAsync(
        GeoServerTranslationRequest request,
        CancellationToken cancellationToken = default);
}
