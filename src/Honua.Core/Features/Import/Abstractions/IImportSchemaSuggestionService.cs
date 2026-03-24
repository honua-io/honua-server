// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Domain;

namespace Honua.Core.Features.Import.Abstractions;

/// <summary>
/// Computes schema suggestions for imported data based on preview analysis.
/// Suggestions are advisory and returned before final import commit.
/// </summary>
public interface IImportSchemaSuggestionService
{
    /// <summary>
    /// Generates schema suggestions from a file preview result.
    /// </summary>
    /// <param name="preview">The file preview containing sample data and detected metadata.</param>
    /// <param name="fileName">The original file name.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Advisory schema suggestions for the import.</returns>
    Task<SchemaSuggestion> SuggestAsync(
        FilePreview preview,
        string fileName,
        CancellationToken cancellationToken = default);
}
