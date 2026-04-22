// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.Metadata.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Metadata;

/// <summary>
/// Service collection extensions for registering unified metadata services.
/// </summary>
public static class MetadataServiceCollectionExtensions
{
    /// <summary>
    /// Adds unified metadata services to the service collection.
    /// </summary>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddUnifiedMetadata(this IServiceCollection services)
    {
        // Register the core metadata provider
        services.TryAddSingleton<IMetadataProvider, UnifiedMetadataProvider>();

        return services;
    }

    /// <summary>
    /// Adds a capabilities formatter for a specific protocol.
    /// </summary>
    /// <typeparam name="TCapabilities">The capabilities type for the protocol</typeparam>
    /// <typeparam name="TFormatter">The formatter implementation</typeparam>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddCapabilitiesFormatter<TCapabilities, TFormatter>(
        this IServiceCollection services)
        where TFormatter : class, ICapabilitiesFormatter<TCapabilities>
    {
        services.TryAddSingleton<ICapabilitiesFormatter<TCapabilities>, TFormatter>();
        return services;
    }

    /// <summary>
    /// Adds a global capabilities formatter for a specific protocol.
    /// </summary>
    /// <typeparam name="TCapabilities">The capabilities type for the protocol</typeparam>
    /// <typeparam name="TFormatter">The formatter implementation</typeparam>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddGlobalCapabilitiesFormatter<TCapabilities, TFormatter>(
        this IServiceCollection services)
        where TFormatter : class, IGlobalCapabilitiesFormatter<TCapabilities>
    {
        services.TryAddSingleton<IGlobalCapabilitiesFormatter<TCapabilities>, TFormatter>();
        return services;
    }

    /// <summary>
    /// Adds a multi-format capabilities formatter for a specific protocol.
    /// </summary>
    /// <typeparam name="TCapabilities">The capabilities type for the protocol</typeparam>
    /// <typeparam name="TFormatter">The formatter implementation</typeparam>
    /// <param name="services">The service collection to add services to</param>
    /// <returns>The service collection for method chaining</returns>
    public static IServiceCollection AddMultiFormatCapabilitiesFormatter<TCapabilities, TFormatter>(
        this IServiceCollection services)
        where TFormatter : class, IMultiFormatCapabilitiesFormatter<TCapabilities>
    {
        services.TryAddSingleton<IMultiFormatCapabilitiesFormatter<TCapabilities>, TFormatter>();
        return services;
    }

    /// <summary>
    /// Gets all registered capabilities formatters for a specific protocol type.
    /// </summary>
    /// <typeparam name="TCapabilities">The capabilities type to find formatters for</typeparam>
    /// <param name="serviceProvider">The service provider</param>
    /// <returns>All registered formatters for the protocol</returns>
    public static IEnumerable<ICapabilitiesFormatter<TCapabilities>> GetCapabilitiesFormatters<TCapabilities>(
        this IServiceProvider serviceProvider)
    {
        return serviceProvider.GetServices<ICapabilitiesFormatter<TCapabilities>>();
    }

    /// <summary>
    /// Gets the capabilities formatter for a specific protocol.
    /// </summary>
    /// <typeparam name="TCapabilities">The capabilities type</typeparam>
    /// <param name="serviceProvider">The service provider</param>
    /// <param name="protocolName">The protocol name to find</param>
    /// <returns>The formatter for the protocol, or null if not found</returns>
    public static ICapabilitiesFormatter<TCapabilities>? GetCapabilitiesFormatter<TCapabilities>(
        this IServiceProvider serviceProvider,
        string protocolName)
    {
        return serviceProvider.GetServices<ICapabilitiesFormatter<TCapabilities>>()
            .FirstOrDefault(f => f.Protocol.Equals(protocolName, StringComparison.OrdinalIgnoreCase));
    }
}
