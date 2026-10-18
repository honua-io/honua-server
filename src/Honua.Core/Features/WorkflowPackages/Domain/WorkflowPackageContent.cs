// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.Collaboration.Operations;

namespace Honua.Core.Features.WorkflowPackages.Domain;

/// <summary>Exact authoring payload comparison, including graph fields excluded from the executable graph hash.</summary>
public static class WorkflowPackageContent
{
    /// <summary>Stable command fingerprint over the requested name and complete graph.</summary>
    public static string Fingerprint(string name, WorkflowGraph graph)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(SavedMapOperationIdempotency.CanonicalPayload(
            JsonSerializer.SerializeToElement(new WorkflowAuthoringContent(name, graph), WorkflowPackageContentJsonContext.Default.WorkflowAuthoringContent)))));

    /// <summary>Compares all graph bytes semantically, including schedules and editor/node metadata.</summary>
    public static bool SameGraph(WorkflowGraph left, WorkflowGraph right)
        => SavedMapOperationIdempotency.CanonicalPayload(JsonSerializer.SerializeToElement(left, WorkflowPackageContentJsonContext.Default.WorkflowGraph)) ==
           SavedMapOperationIdempotency.CanonicalPayload(JsonSerializer.SerializeToElement(right, WorkflowPackageContentJsonContext.Default.WorkflowGraph));

    /// <summary>Rejects a collision instead of laundering an existing immutable version into a new command.</summary>
    public static void RequireInitialVersion(WorkflowPackage expected, WorkflowPackageVersion existing)
    {
        if (existing.Version != 1 || existing.PackageId != expected.PackageId || existing.Ownership != expected.Ownership ||
            existing.CreatedBy != expected.CreatedBy || existing.PackageHash != WorkflowPackageGraphHasher.Compute(expected.Graph) ||
            !SameGraph(existing.Graph, expected.Graph))
            throw new WorkflowPackageGraphConflictException(expected.PackageId);
    }
}

internal sealed record WorkflowAuthoringContent(string Name, WorkflowGraph Graph);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, UseStringEnumConverter = true)]
[JsonSerializable(typeof(WorkflowAuthoringContent))]
[JsonSerializable(typeof(WorkflowGraph))]
internal sealed partial class WorkflowPackageContentJsonContext : JsonSerializerContext;
