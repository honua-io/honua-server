// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Authorization;
using Honua.Core.Features.Studio.Abstractions;
using Honua.Core.Features.Studio.Services;
using Honua.Core.Features.Studio.Services.Bridging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Honua.Core.Features.Studio;

/// <summary>
/// Dependency injection helpers for Studio package lifecycle services.
/// </summary>
public static class StudioServiceCollectionExtensions
{
    /// <summary>
    /// Registers the shared Studio package lifecycle service and fallback in-memory store.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">
    /// Optional application configuration, bound to <see cref="StudioEndUserAuthorizationOptions"/>
    /// (<c>Studio:EndUserAuthorization</c>, honua-server#3001). When omitted, the options bind
    /// to their defaults (<see cref="StudioEndUserAuthorizationOptions.Enabled"/> = <see
    /// langword="false"/>), matching the pre-#3001 admin-only posture (NFR-001).
    /// </param>
    public static IServiceCollection AddStudioPackageLifecycle(
        this IServiceCollection services,
        IConfiguration? configuration = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IStudioPackageStore, InMemoryStudioPackageStore>();
        // ADR-0069 (honua-server#3004): the catalog decides at resolve time which family
        // persistence bridges are available (native forms/analysis stores may be registered
        // before or after this call depending on the host), and the lifecycle service and
        // family registry consume it to route the form/analysis families through their native
        // stores.
        services.TryAddScoped<StudioFamilyPersistenceBridgeCatalog>();
        services.TryAddScoped<IStudioPackageFamilyRegistry, StudioPackageFamilyRegistry>();
        services.TryAddScoped<IStudioPackageValidator, StudioPackageValidator>();
        services.TryAddScoped<IStudioPackageLifecycleService, StudioPackageLifecycleService>();
        services.TryAddScoped<IStudioAuthorizationService, StudioAuthorizationService>();

        var optionsBuilder = services.AddOptions<StudioEndUserAuthorizationOptions>();

        // Admin-role recognition (honua-server#3001 follow-up): bind from the same Oidc:*
        // config key OidcAuthenticationOptions.AdminRoles reads, so StudioAuthorizationService
        // recognizes configured OIDC admin-role aliases (for example "administrator") without
        // Honua.Core referencing Honua.Hosting's OidcAuthenticationOptions type. See
        // AdminRoleOptions for the full rationale.
        var adminRoleOptionsBuilder = services.AddOptions<AdminRoleOptions>();
        if (configuration is not null)
        {
            optionsBuilder.Bind(configuration.GetSection(StudioEndUserAuthorizationOptions.SectionName));
            adminRoleOptionsBuilder.Bind(configuration.GetSection(AdminRoleOptions.SectionName));
        }

        return services;
    }
}
