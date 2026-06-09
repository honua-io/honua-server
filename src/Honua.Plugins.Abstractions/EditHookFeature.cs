// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.FeatureStore.Domain;

namespace Honua.Plugins.Abstractions;

/// <summary>
/// A single feature within an <see cref="EditHookContext"/>, tagged with its edit kind and
/// the originating request slot so a host can map a per-feature verdict back to the correct
/// protocol response position.
/// </summary>
/// <param name="Kind">Whether the feature is being created, updated, or deleted.</param>
/// <param name="RequestIndex">Index of this feature within its kind's request array (adds/updates/deletes).</param>
/// <param name="ObjectId">The feature's object id for updates/deletes; <see langword="null"/> for creates.</param>
/// <param name="Feature">
/// The resolved feature for creates/updates. For deletes this is the prior snapshot of the
/// row being removed when the host could read it, otherwise an empty feature with the object id.
/// </param>
public sealed record EditHookFeature(
    EditKind Kind,
    int RequestIndex,
    long? ObjectId,
    Feature Feature);
