// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Domain.V2;
using Honua.Core.Features.Styling.Domain;

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>
/// Profiles layer field statistics for style suggestion.
/// </summary>
public interface IFieldProfilingService
{
    /// <summary>
    /// Profiles the specified fields for a layer, computing statistics and sample values.
    /// </summary>
    /// <param name="layerId">The layer to profile.</param>
    /// <param name="fields">Metadata v2 schema fields to profile.</param>
    /// <param name="sampleLimit">Maximum rows to sample for profiling.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Profiles for each successfully profiled field.</returns>
    Task<IReadOnlyList<FieldProfile>> ProfileFieldsAsync(
        int layerId,
        IReadOnlyList<MetadataV2Field> fields,
        int sampleLimit,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Retrieves sorted numeric values for a field, used by classification algorithms.
    /// </summary>
    /// <param name="layerId">The layer identifier.</param>
    /// <param name="fieldName">The field name to retrieve values from.</param>
    /// <param name="limit">Maximum number of values to retrieve.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Sorted numeric values.</returns>
    Task<double[]> GetNumericValuesAsync(
        int layerId,
        string fieldName,
        int limit,
        CancellationToken cancellationToken = default);
}
