// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Queries.Filters;

namespace Honua.Core.Features.Authorization.Abstractions;

/// <summary>Optional paired source capability for one fresh public read operation.</summary>
public interface ICombinedReadSecuritySource
{
    /// <summary>Checks compatibility with the actual registered field-mask source.</summary>
    bool CanResolveWith(IFieldMaskSource fieldSource);

    /// <summary>Evaluates the live principal and current row and field policies together.</summary>
    Task<ReadSecurityResolution> ResolveCombinedAsync(
        MetadataV2Resource resource,
        IFieldMaskSource fieldSource,
        CancellationToken cancellationToken = default);
}

/// <summary>Resolved policy restrictions scoped to one operation; permanent filters are separate.</summary>
/// <param name="RowFilter">Translated caller row restriction, or null if none applies.</param>
/// <param name="MaskedFields">Caller fields to suppress.</param>
public readonly record struct ReadSecurityResolution(SqlFragment? RowFilter, ImmutableArray<string> MaskedFields);
