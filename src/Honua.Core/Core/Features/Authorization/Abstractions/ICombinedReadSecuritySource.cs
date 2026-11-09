// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>
/// Optional capability for a row-policy source that can evaluate fresh row and field
/// restrictions together for one public read when the registered field source is compatible.
/// </summary>
public interface ICombinedReadSecuritySource
{
    /// <summary>
    /// Returns whether <paramref name="fieldSource"/> is the compatible field-mask source.
    /// </summary>
    /// <param name="fieldSource">The field-mask source registered for the same read.</param>
    /// <returns><see langword="true"/> when both restrictions can be resolved together.</returns>
    bool CanResolveWith(IFieldMaskSource fieldSource);

    /// <summary>
    /// Evaluates the live principal and the current row and field policies for one operation.
    /// </summary>
    /// <param name="resource">Resource being read.</param>
    /// <param name="fieldSource">The compatible field-mask source.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The row predicate and masked fields for this operation. Permanent filters are separate.</returns>
    Task<ReadSecurityResolution> ResolveCombinedAsync(
        MetadataV2Resource resource,
        IFieldMaskSource fieldSource,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolved policy restrictions scoped to one operation.</summary>
/// <param name="RowFilter">Translated caller row restriction, or null when none applies.</param>
/// <param name="MaskedFields">Caller fields to suppress.</param>
public readonly record struct ReadSecurityResolution(SqlFragment? RowFilter, ImmutableArray<string> MaskedFields);
