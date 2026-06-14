// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Read-only context supplied to an <see cref="IComputedFieldProvider"/> when projecting a
/// derived/virtual field onto a queried feature. Carries the layer/service identity so plugins
/// can scope computed fields without coupling to protocol internals.
/// </summary>
/// <param name="ServiceId">The service the queried layer belongs to.</param>
/// <param name="LayerId">The published layer id being queried.</param>
/// <param name="ResourceName">Human-readable layer/resource name, when available.</param>
/// <param name="Actor">The authenticated actor performing the query, when available.</param>
public sealed record ComputedFieldContext(
    string ServiceId,
    int LayerId,
    string? ResourceName,
    string? Actor);
