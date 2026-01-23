// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Domain;
using Honua.Server.Features.Admin.Models;

namespace Honua.Server.Features.Admin.Services;

/// <summary>
/// Default metadata compiler implementation.
/// </summary>
internal sealed class DefaultMetadataCompiler : IMetadataCompiler
{
    private const string CompilerVersion = "1.0";

    public Task<MetadataCompilationResult> CompileAsync(
        MetadataResource resource,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var compiledAt = DateTimeOffset.UtcNow;
        var status = new MetadataCompilationStatus
        {
            Ready = true,
            CompiledAt = compiledAt,
            CompilerVersion = CompilerVersion
        };

        var statusElement = JsonSerializer.SerializeToElement(
            status,
            MetadataResourceJsonContext.Default.MetadataCompilationStatus);

        var artifact = new CompiledMetadataArtifact
        {
            ResourceId = resource.Metadata?.Id,
            ApiVersion = resource.ApiVersion,
            Kind = resource.Kind,
            ResourceVersion = resource.Metadata?.ResourceVersion,
            Spec = resource.Spec,
            GeneratedAt = compiledAt,
            CompilerVersion = CompilerVersion
        };

        return Task.FromResult(new MetadataCompilationResult(artifact, statusElement));
    }
}
