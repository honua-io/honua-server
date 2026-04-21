// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Import.Abstractions;
using Honua.Core.Features.Import.Domain;
using Honua.Core.Features.Infrastructure.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Postgres.Features.Import;

/// <summary>
/// Dependency injection registration for refactored import services following SOLID principles.
/// Replaces monolithic service registration with focused, segregated services.
/// </summary>
internal static class RefactoredImportServiceCollectionExtensions
{
    /// <summary>
    /// Registers refactored import services using composition and interface segregation
    /// </summary>
    /// <param name="services">Service collection to register services with</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddRefactoredImportServices(this IServiceCollection services)
    {
        // Register segregated import service interfaces following ISP
        services.TryAddScoped<IFileFormatDetectionService, FileFormatDetectionService>();
        // TODO: Implement missing services: FilePreviewService, StreamingImportProcessor
        // services.TryAddScoped<IFilePreviewService, FilePreviewService>();
        // services.TryAddScoped<IStreamingImportProcessor, StreamingImportProcessor>();

        // TODO: Re-enable after implementing missing services
        // Register the composed service that coordinates the segregated services
        // services.TryAddScoped<IFileImportService, RefactoredStreamingFileImportService>();

        return services;
    }

    /// <summary>
    /// Registers refactored import services with custom limits configuration
    /// </summary>
    /// <param name="services">Service collection to register services with</param>
    /// <param name="configureLimits">Action to configure import limits</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddRefactoredImportServices(
        this IServiceCollection services,
        Action<ImportLimits> configureLimits)
    {
        services.Configure(configureLimits);
        return services.AddRefactoredImportServices();
    }

    /// <summary>
    /// Register import services with cloud storage configuration
    /// </summary>
    /// <param name="services">Service collection to register services with</param>
    /// <param name="configureCloudStorage">Action to configure cloud storage options</param>
    /// <returns>Updated service collection</returns>
    public static IServiceCollection AddRefactoredImportServicesWithCloudStorage(
        this IServiceCollection services,
        Action<CloudStorageOptions> configureCloudStorage)
    {
        services.Configure(configureCloudStorage);
        return services.AddRefactoredImportServices();
    }
}