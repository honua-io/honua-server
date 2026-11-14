// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.ControlPlane;
using Honua.Core.Features.Identity.Abstractions;
using Honua.Core.Features.Operations.Abstractions;
using Honua.Core.Features.Metadata.Abstractions;
using Honua.Core.Features.RateLimiting.Abstractions;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Authentication.ClientCertificates;
using Honua.Server.Features.Admin.Services;
using Honua.Server.Features.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Npgsql;

namespace Honua.Server.Startup;

internal static class PostgresConfigurationStoreRegistration
{
    public static IServiceCollection AddPostgresConfigurationStores(this IServiceCollection services)
    {
        services.TryAddSingleton(provider => new PostgresConfigurationDocuments(
            provider.GetRequiredService<JobStoreDatabase>(),
            provider.GetRequiredService<Microsoft.AspNetCore.DataProtection.IDataProtectionProvider>(),
            provider.GetService<ILogger<PostgresConfigurationDocuments>>()));
        services.AddHttpContextAccessor();
        services.TryAddScoped<RequestConfigurationGeneration>();
        services.TryAddScoped<IConfigurationGenerationSource>(provider => HasDatabase(provider)
            ? provider.GetRequiredService<RequestConfigurationGeneration>() : null!);
        services.TryAddSingleton<PostgresOidcProviderStore>();
        services.TryAddSingleton<PostgresRateLimitPolicyStore>();
        services.TryAddSingleton<PostgresAgentAuthorityStore>();
        services.TryAddSingleton<IAgentAuthorityStore>(provider => HasDatabase(provider)
            ? provider.GetRequiredService<PostgresAgentAuthorityStore>() : null!);
        services.TryAddSingleton<PostgresClientCertificateTrustStore>();
        services.TryAddSingleton<OidcRuntimeProviderRegistry>();
        services.TryAddSingleton<ConfigurationRuntimeRefresh>();
        services.RemoveAll<IOidcProviderStore>();
        services.AddSingleton<IOidcProviderStore>(provider => HasDatabase(provider)
            ? provider.GetRequiredService<PostgresOidcProviderStore>()
            : new InMemoryOidcProviderStore(provider.GetService<IHttpClientFactory>(), provider.GetRequiredService<OidcRuntimeProviderRegistry>()));
        services.RemoveAll<IRateLimitPolicyStore>();
        services.AddSingleton<IRateLimitPolicyStore>(provider => HasDatabase(provider)
            ? provider.GetRequiredService<PostgresRateLimitPolicyStore>() : new InMemoryRateLimitPolicyStore());
        services.RemoveAll<IClientCertificateTrustStore>();
        services.AddSingleton<IClientCertificateTrustStore>(provider => HasDatabase(provider)
            ? provider.GetRequiredService<PostgresClientCertificateTrustStore>()
            : new InMemoryClientCertificateTrustStore(provider.GetRequiredService<IOptions<ClientCertificateAuthenticationOptions>>()));
        return services;
    }

    private static bool HasDatabase(IServiceProvider provider) => provider.GetService<NpgsqlDataSource>() is not null;
}
