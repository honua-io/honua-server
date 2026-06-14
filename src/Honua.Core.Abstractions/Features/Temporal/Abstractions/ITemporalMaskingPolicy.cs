// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Temporal.Abstractions;

/// <summary>
/// Decides which attribute fields are redacted from temporal timeline and diff responses (slice 3 of
/// honua-server#1166). The temporal surfaces expose historical attribute values; the masking policy
/// ensures field-level redaction is applied consistently to history reads, not bypassed because the data
/// is historical. The default policy masks nothing; deployments override it to redact sensitive fields.
/// </summary>
public interface ITemporalMaskingPolicy
{
    /// <summary>
    /// Returns whether the named field must be redacted from temporal history responses for the layer.
    /// </summary>
    /// <param name="serviceId">Owning service id.</param>
    /// <param name="layerId">Service-local layer index.</param>
    /// <param name="field">The attribute field name.</param>
    /// <returns>True when the field's historical values must be redacted.</returns>
    bool IsFieldMasked(string serviceId, int layerId, string field);
}
