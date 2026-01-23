// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Domain;

namespace Honua.Core.Features.Metadata.Abstractions;

/// <summary>
/// Compiler pipeline for deriving runtime artifacts and status updates from metadata resources.
/// </summary>
public interface IMetadataCompiler
{
    /// <summary>
    /// Compiles a metadata resource into a derived artifact and status.
    /// </summary>
    Task<MetadataCompilationResult> CompileAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Result of compiling a metadata resource.
/// </summary>
public sealed record MetadataCompilationResult(
    CompiledMetadataArtifact Artifact,
    JsonElement Status);
