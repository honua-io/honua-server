// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Core.Features.Geoprocessing.Domain;

/// <summary>Identifies an output owned by one durable operation and output slot.</summary>
/// <param name="WorkspaceId">Resolved caller-owned workspace.</param>
/// <param name="OperationId">Stable durable operation identity, retained across retries.</param>
/// <param name="OutputSlot">Stable declared output slot.</param>
/// <param name="Kind">Output artifact kind.</param>
/// <param name="Label">Workspace output label.</param>
/// <param name="Overwrite">Whether another operation's output may be replaced.</param>
/// <param name="Reference">Published artifact reference; no external resource is fetched.</param>
public sealed record WorkspaceArtifactPublication(
    string WorkspaceId, string OperationId, int OutputSlot, ArtifactKind Kind,
    string Label, bool Overwrite, string Reference);
