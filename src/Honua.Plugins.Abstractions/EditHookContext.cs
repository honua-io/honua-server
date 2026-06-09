// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Context describing a batch of feature edits for an <see cref="IEditHook"/>. The same context
/// instance is passed to the before-write hook (where a hook may reject the whole batch) and to
/// the after-write hook (where committed edits can be observed).
/// </summary>
/// <param name="ServiceId">The service the edited layer belongs to.</param>
/// <param name="LayerId">The published layer id being edited.</param>
/// <param name="ResourceName">Human-readable layer/resource name, when available.</param>
/// <param name="Actor">The authenticated actor performing the edit, when available.</param>
/// <param name="CorrelationId">Request correlation/trace id, for joining audit events with logs.</param>
/// <param name="Features">All features participating in the batch, tagged by kind and request slot.</param>
public sealed record EditHookContext(
    string ServiceId,
    int LayerId,
    string? ResourceName,
    string? Actor,
    string? CorrelationId,
    ImmutableArray<EditHookFeature> Features);
