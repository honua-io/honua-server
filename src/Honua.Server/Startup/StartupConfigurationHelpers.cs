// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Security;
using Honua.Server.Features.Infrastructure.Authentication;
using Honua.Server.Features.Infrastructure.Helpers;
using Honua.Server.Features.Infrastructure.Licensing;
using Honua.Server.Features.Infrastructure.Security;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Startup;

/// <summary>
/// Low-level configuration helpers invoked early in <c>Program.cs</c> (before service
/// registration begins): forwarded-headers wiring, environment-secret reference resolution,
/// security-configuration source ordering, the IValidateOptions catalogue, and the bootstrap
/// Redis-cache entitlement probe.
/// </summary>
internal static class StartupConfigurationHelpers
{
    /// <summary>Configure forwarded-headers middleware. Returns whether the middleware should be enabled.</summary>
    public static bool ConfigureForwardedHeaders(IServiceCollection services, IConfiguration configuration)
    {
        var enabled = configuration.GetValue<bool>("ForwardedHeaders:Enabled");
        if (!enabled)
        {
            return false;
        }

        services.Configure<ForwardedHeadersOptions>(options =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor |
                                       ForwardedHeaders.XForwardedProto |
                                       ForwardedHeaders.XForwardedHost;
            options.ForwardLimit = configuration.GetValue<int?>("ForwardedHeaders:ForwardLimit") ?? 1;

            var knownProxies = configuration.GetSection("ForwardedHeaders:KnownProxies").Get<string[]>() ?? [];
            foreach (var proxy in knownProxies)
            {
                if (IPAddress.TryParse(proxy, out var ip))
                {
                    options.KnownProxies.Add(ip);
                }
            }
        });

        return true;
    }

    /// <summary>
    /// Pre-resolve secret-reference syntax (e.g. <c>env:VAR_NAME</c>) for connection strings
    /// that the host reads before DI is built.
    /// </summary>
    public static void ResolveEnvironmentSecretReferences(ConfigurationManager configuration)
    {
        ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:DefaultConnection");
        ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:redis");
        ResolveEnvironmentSecretReference(configuration, "Aspire:StackExchange:Redis:ConnectionString");
    }

    private static void ResolveEnvironmentSecretReference(ConfigurationManager configuration, string key)
    {
        var value = configuration[key];
        var resolved = SecretReferenceResolver.ResolveEnvironmentReference(value, key);
        if (!string.Equals(value, resolved, StringComparison.Ordinal))
        {
            configuration[key] = resolved;
        }
    }

    /// <summary>
    /// Inspect a bootstrap license snapshot to determine whether the running host is
    /// entitled to use Redis as the distributed cache backend.
    /// </summary>
    public static async Task<bool> IsRedisCacheEntitledAsync(IConfiguration configuration)
    {
        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return false;
        }

        using var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
        var snapshot = await FileBackedLicenseService
            .LoadBootstrapSnapshotAsync(configuration, loggerFactory)
            .ConfigureAwait(false);
        return snapshot.HasEntitlement("caching.redis");
    }

    /// <summary>
    /// Load the optional <c>appsettings.Security.json</c> file and re-order it in the
    /// configuration source chain so that environment-specific overrides win.
    /// </summary>
    public static void AddSecurityConfiguration(ConfigurationManager configuration, IHostEnvironment environment)
    {
        const string securitySettingsFile = "appsettings.Security.json";
        configuration.AddJsonFile(securitySettingsFile, optional: true, reloadOnChange: true);

        var sources = configuration.Sources;
        var securityIndex = -1;
        for (var i = sources.Count - 1; i >= 0; i--)
        {
            if (sources[i] is JsonConfigurationSource jsonSource &&
                string.Equals(jsonSource.Path, securitySettingsFile, StringComparison.OrdinalIgnoreCase))
            {
                securityIndex = i;
                break;
            }
        }

        if (securityIndex < 0)
        {
            return;
        }

        var securitySource = sources[securityIndex];
        sources.RemoveAt(securityIndex);

        var envSettingsPath = $"appsettings.{environment.EnvironmentName}.json";
        var insertIndex = -1;
        for (var i = 0; i < sources.Count; i++)
        {
            if (sources[i] is JsonConfigurationSource jsonSource &&
                string.Equals(jsonSource.Path, envSettingsPath, StringComparison.OrdinalIgnoreCase))
            {
                insertIndex = i;
                break;
            }
        }

        if (insertIndex < 0)
        {
            for (var i = 0; i < sources.Count; i++)
            {
                if (sources[i] is JsonConfigurationSource jsonSource &&
                    string.Equals(jsonSource.Path, "appsettings.json", StringComparison.OrdinalIgnoreCase))
                {
                    insertIndex = i + 1;
                    break;
                }
            }
        }

        if (insertIndex < 0)
        {
            insertIndex = 0;
        }

        sources.Insert(insertIndex, securitySource);
    }

    /// <summary>
    /// Registers IValidateOptions&lt;T&gt; implementations for the configuration classes that
    /// can fail-fast the host on invalid settings.
    /// </summary>
    public static void RegisterConfigurationValidators(IServiceCollection services)
    {
        services.AddSingleton<IValidateOptions<LimitsOptions>>(new LimitsOptionsValidator());
        services.AddSingleton<IValidateOptions<CacheOptions>>(new CacheOptionsValidator());
        services.AddSingleton<IValidateOptions<CloudStorageOptions>>(new CloudStorageOptionsValidator());
        services.AddSingleton<IValidateOptions<OidcAuthenticationOptions>>(new OidcAuthenticationOptionsValidator());
        services.AddSingleton<IValidateOptions<FileUploadSecurityOptions>>(new FileUploadSecurityOptionsValidator());
    }
}
