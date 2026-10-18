// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Styling.Abstractions;

/// <summary>Canonical conditional human editing of one fully reviewed shared-style scope.</summary>
public interface ISharedStyleEditor
{
    /// <summary>Validates losslessly, reauthorizes and atomically replaces the exact reviewed revision and impact.</summary>
    Task<OgcStyleUpdateResult> UpdateHumanAsync(string styleId, int expectedVersion, string expectedImpactHash,
        string document, bool drawingInfo, CancellationToken cancellationToken = default);
}
