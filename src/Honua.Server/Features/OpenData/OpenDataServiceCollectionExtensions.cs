// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.OpenData.Abstractions;
using Honua.Server.Features.OpenData.Services;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Server.Features.OpenData;

/// <summary>
/// Dependency registration for the open-data publication slice.
/// </summary>
internal static class OpenDataServiceCollectionExtensions
{
    /// <summary>
    /// Adds open-data publication services.
    /// </summary>
    public static IServiceCollection AddOpenDataPublication(this IServiceCollection services)
    {
        services.TryAddSingleton<IOpenDataStore, InMemoryOpenDataStore>();
        services.TryAddScoped<OpenDataPublicationService>();
        return services;
    }
}
