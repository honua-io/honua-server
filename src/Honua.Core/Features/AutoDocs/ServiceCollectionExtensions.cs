// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AutoDocs.Abstractions;
using Honua.Core.Features.AutoDocs.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.AutoDocs;

/// <summary>
/// Service registration helpers for auto-documentation generation.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the metadata document generator and supporting services.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddAutoDocsCore(this IServiceCollection services)
    {
        services.TryAddSingleton<IMetadataDocumentGenerator, MetadataDocumentGenerator>();
        return services;
    }
}
