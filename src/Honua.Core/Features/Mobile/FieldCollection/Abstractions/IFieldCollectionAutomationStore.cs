// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Domain;

namespace Honua.Core.Features.Mobile.FieldCollection.Abstractions;

/// <summary>
/// Read boundary for server-side FieldCollection automation action definitions
/// (#2121). Implementations return the enabled actions relevant to a layer so the
/// trigger can match them against an applied change.
/// </summary>
public interface IFieldCollectionAutomationStore
{
    /// <summary>
    /// Returns the enabled actions that may apply to changes on
    /// <paramref name="layerId"/>. Layer-agnostic actions (those with no layer
    /// scope) must be included alongside actions scoped to the layer. Operation
    /// filtering is left to <see cref="FieldCollectionAutomationMatcher"/> so the
    /// store contract stays a coarse, index-friendly query.
    /// </summary>
    Task<IReadOnlyList<FieldCollectionAutomationAction>> GetEnabledActionsAsync(
        int layerId,
        CancellationToken cancellationToken = default);
}
