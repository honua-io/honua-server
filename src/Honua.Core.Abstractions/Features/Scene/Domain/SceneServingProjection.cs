// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Security.Domain;

namespace Honua.Core.Features.Scene.Domain;

/// <summary>
/// Canonical projection helpers that map a persisted <see cref="SceneDatasetRecord"/>
/// onto the lean serving read model. Centralizing the record-to-policy mapping
/// here keeps the authorization posture consistent across every protocol adapter
/// (HTTP, I3S, gRPC) and the Postgres registry, so a change to the record-to-policy
/// rules cannot silently drift between callers.
/// </summary>
public static class SceneServingProjection
{
    /// <summary>
    /// Derives the <see cref="AccessPolicy"/> enforced for hosted serving from a
    /// scene dataset record. A public record carries no policy (anonymous read is
    /// allowed); a non-public record maps to <c>AllowAnonymous=false</c> plus its
    /// configured <see cref="SceneDatasetRecord.AllowedRoles"/>.
    /// </summary>
    /// <param name="record">The persisted scene dataset record.</param>
    /// <returns>
    /// <see langword="null"/> for public records, otherwise the protected
    /// <see cref="AccessPolicy"/> the shared access evaluator understands.
    /// </returns>
    public static AccessPolicy? ToAccessPolicy(SceneDatasetRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return record.IsPublic
            ? null
            : new AccessPolicy
            {
                AllowAnonymous = false,
                AllowedRoles = record.AllowedRoles?.ToArray(),
            };
    }
}
