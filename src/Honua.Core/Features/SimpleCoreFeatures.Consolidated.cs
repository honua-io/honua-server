// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.AutoDocs.Abstractions;
using Honua.Core.Features.AutoDocs.Services;
using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Services;
using Honua.Core.Features.Infrastructure.ServiceRegistration;
using Honua.Core.Features.Styling.Abstractions;
using Honua.Core.Features.Styling.Services;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Core.Features;

/// <summary>
/// Consolidated service registration for simple core features that follow identical patterns.
/// Demonstrates consolidation of 3 separate ServiceCollectionExtensions files into 1.
/// </summary>
public static class SimpleCoreFeatures
{
    /// <summary>
    /// Add all simple core feature services using consolidated patterns.
    /// Replaces AddImportSuggestionsCore, AddAutoDocsCore, and AddStyleSuggestionCore.
    /// </summary>
    public static IServiceCollection AddSimpleCoreFeatures(this IServiceCollection services)
    {
        // Register all simple core features using consolidated pattern
        services
            .AddSimpleCoreFeature<IImportSchemaSuggestionService, ImportSchemaSuggestionService>(ServiceLifetime.Singleton)
            .AddSimpleCoreFeature<IMetadataDocumentGenerator, MetadataDocumentGenerator>(ServiceLifetime.Singleton)
            .AddSimpleCoreFeature<IStyleSuggestionService, StyleSuggestionService>();

        return services;
    }

    /// <summary>
    /// Add import suggestions core services (individual method for backward compatibility).
    /// </summary>
    public static IServiceCollection AddImportSuggestionsCore(this IServiceCollection services)
    {
        return services.AddSimpleCoreFeature<IImportSchemaSuggestionService, ImportSchemaSuggestionService>(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Add auto-documentation core services (individual method for backward compatibility).
    /// </summary>
    public static IServiceCollection AddAutoDocsCore(this IServiceCollection services)
    {
        return services.AddSimpleCoreFeature<IMetadataDocumentGenerator, MetadataDocumentGenerator>(ServiceLifetime.Singleton);
    }

    /// <summary>
    /// Add style suggestion core services (individual method for backward compatibility).
    /// </summary>
    public static IServiceCollection AddStyleSuggestionCore(this IServiceCollection services)
    {
        return services.AddSimpleCoreFeature<IStyleSuggestionService, StyleSuggestionService>();
    }
}