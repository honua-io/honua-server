// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

/// <summary>
/// Read-only context supplied to an <see cref="IFeatureValidator"/> for a single feature
/// participating in an edit. Carries the layer/service identity and the kind of edit so
/// plugin validators can apply rules conditionally without coupling to protocol internals.
/// </summary>
/// <param name="ServiceId">The service the edited layer belongs to.</param>
/// <param name="LayerId">The published layer id being edited.</param>
/// <param name="ResourceName">Human-readable layer/resource name, when available.</param>
/// <param name="EditKind">Whether the feature is being created, updated, or deleted.</param>
/// <param name="ObjectId">The feature's object id for updates/deletes; <see langword="null"/> for creates.</param>
/// <param name="Actor">The authenticated actor performing the edit, when available.</param>
public sealed record PluginValidationContext(
    string ServiceId,
    int LayerId,
    string? ResourceName,
    EditKind EditKind,
    long? ObjectId,
    string? Actor);
