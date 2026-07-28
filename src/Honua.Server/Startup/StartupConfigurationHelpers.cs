// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Caching;
using Honua.Core.Features.Infrastructure.Domain;
using Honua.Core.Features.Licensing.Domain;
using Honua.Core.Features.Security;
using Honua.Infrastructure.Authentication;
using Honua.Infrastructure.Helpers;
using Honua.Infrastructure.Licensing;
using Honua.Infrastructure.Security;
using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
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
            // Not a simple .Where(): the filter condition depends on IPAddress.TryParse's
            // `out` value, which the body also needs, so a LINQ predicate can't cleanly
            // carry that state without re-parsing per candidate.
            foreach (var ip in knownProxies
                .Select(static proxy => IPAddress.TryParse(proxy, out var ip) ? ip : null)
                .OfType<IPAddress>())
            {
                options.KnownProxies.Add(ip);
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
    /// Resolves AWS Secrets Manager-backed secret references (<c>aws:secretsmanager:&lt;arn-or-name&gt;</c>)
    /// for the Redis connection-string settings Program.cs reads before <c>WebApplicationBuilder.Build()</c>
    /// runs, so the raw reference is never handed to <c>StackExchange.Redis.ConfigurationOptions.Parse</c>
    /// (honua-server#3011). The honua-iac AWS serverless module wires <c>ConnectionStrings__redis</c> as an
    /// <c>aws:secretsmanager:&lt;arn&gt;</c> reference — the same style already resolved for the Postgres
    /// <c>DefaultConnection</c> string post-Build, through the DI-registered <c>IConnectionSecretResolver</c>
    /// composite (<see cref="Honua.Infrastructure.Helpers.ConnectionStringResolutionHelper.ResolveDefaultConnectionStringAsync"/>)
    /// — but the Redis <c>ConnectionMultiplexer</c> is built directly against <c>builder.Configuration</c>
    /// ahead of service registration, before that composite exists. This resolves the identical reference
    /// style through the exact same mechanism
    /// (<see cref="Honua.Infrastructure.Helpers.ConnectionStringResolutionHelper.ResolveConnectionStringValueAsync"/>),
    /// using a bootstrap-only instance of the SAME AOT-safe <c>AwsSecretsManagerResolver</c> (raw HTTP +
    /// SigV4, no AWSSDK dependency) the Postgres path uses — mirroring the bootstrap-resolver pattern
    /// <see cref="CreateBootstrapLicenseSecretResolvers"/> already establishes for the license snapshot.
    /// <c>env:</c> references are handled first, unconditionally, by that same helper (mirroring
    /// <see cref="ResolveEnvironmentSecretReferences"/> above). Resolved values are written back into
    /// <paramref name="configuration"/> in place so every other in-process reader of these keys
    /// (output-cache wiring, the admin SignalR backplane, deployment-mode config validation) observes the
    /// resolved value without resolving it again itself.
    /// </summary>
    public static async Task ResolveRedisConnectionSecretReferencesAsync(
        ConfigurationManager configuration,
        CancellationToken cancellationToken = default)
    {
        const string primaryKey = "ConnectionStrings:redis";
        const string fallbackKey = "Aspire:StackExchange:Redis:ConnectionString";

        // Match every Redis consumer's null-coalescing precedence: a configured primary value
        // shadows the Aspire fallback, even when the primary is empty. Resolving the shadowed
        // fallback could otherwise fail startup for a value the application never reads.
        var effectiveKey = configuration[primaryKey] is null ? fallbackKey : primaryKey;
        await ResolveRedisConnectionSecretReferenceAsync(configuration, effectiveKey, cancellationToken)
            .ConfigureAwait(false);
    }

    private static async Task ResolveRedisConnectionSecretReferenceAsync(
        ConfigurationManager configuration,
        string key,
        CancellationToken cancellationToken)
    {
        var value = configuration[key];
        if (string.IsNullOrWhiteSpace(value) ||
            !value.StartsWith("aws:secretsmanager:", StringComparison.OrdinalIgnoreCase))
        {
            // Not configured, or not a reference style this bootstrap resolver understands: env:
            // references are already normalized above by ResolveEnvironmentSecretReferences, and a
            // plain connection string needs no resolution.
            return;
        }

        using var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
        using var secretsClient = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
        using var metadataClient = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        using var resolver = new AwsSecretsManagerResolver(
            secretsClient,
            metadataClient,
            loggerFactory.CreateLogger<AwsSecretsManagerResolver>());

        string? resolved;
        try
        {
            resolved = await ConnectionStringResolutionHelper
                .ResolveConnectionStringValueAsync(value, key, resolver, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw new InvalidOperationException(
                $"Failed to resolve the secret-backed connection string for '{key}' from AWS Secrets Manager.",
                ex);
        }

        if (!string.Equals(value, resolved, StringComparison.Ordinal))
        {
            configuration[key] = resolved;
        }
    }

    /// <summary>
    /// Inspect a bootstrap license snapshot to determine whether the running host is
    /// entitled to use Redis as the distributed cache backend.
    /// </summary>
    /// <remarks>
    /// Outside Production the snapshot honors the test/dev-only <c>Licensing:DevGrantEdition</c>
    /// override, identically to the runtime <c>DevLicenseEntitlementService</c> entitlement path.
    /// Without this, a Development + <c>DevGrantEdition=Pro</c> stack with Redis reachable would
    /// fail the gate, the <c>IConnectionMultiplexer</c> would never be wired, and the durable job
    /// store would not register — so authenticated GPServer <c>/execute</c> and <c>/submitJob</c>
    /// returned 503 despite the dev grant entitling Redis (honua-server#1787). DevGrant is never
    /// honored in Production (the host-level guard fails the process closed there).
    /// </remarks>
    public static async Task<bool> IsRedisCacheEntitledAsync(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var redisConnectionString = configuration.GetConnectionString("redis")
            ?? configuration["Aspire:StackExchange:Redis:ConnectionString"];
        if (string.IsNullOrWhiteSpace(redisConnectionString))
        {
            return false;
        }

        var snapshot = await LoadBootstrapLicenseSnapshotAsync(configuration, environment).ConfigureAwait(false);
        return snapshot.HasEntitlement("caching.redis");
    }

    /// <summary>
    /// Inspect a bootstrap license snapshot to determine whether the running host is entitled
    /// to enable the ASP.NET Core output-cache middleware (<c>caching.output-cache</c>, Pro;
    /// #2998). Mirrors <see cref="IsRedisCacheEntitledAsync"/>: <c>Licensing:DevGrantEdition</c>
    /// is honored outside Production (honua-server#1787) and the same license-content secret
    /// resolvers are used (honua-server#1755). When not entitled, <c>Program</c> skips
    /// <c>app.UseOutputCache()</c> while <c>AddOutputCache</c> service registration stays in
    /// place, so endpoints carrying <c>CacheOutput</c> metadata keep serving identical
    /// (uncached) responses in Community deployments.
    /// </summary>
    public static async Task<bool> IsOutputCacheEntitledAsync(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var snapshot = await LoadBootstrapLicenseSnapshotAsync(configuration, environment).ConfigureAwait(false);
        return snapshot.HasEntitlement(FeatureCatalog.OutputCacheKey);
    }

    /// <summary>
    /// Resolves the bootstrap license snapshot the boot-time entitlement gates share. Uses the
    /// SAME license-content secret resolvers that <c>AddHonuaLicensing</c> wires for the
    /// per-request service — without them a Secrets-Manager-only Pro license
    /// (<c>Licensing:LicenseContentSecretRef</c>) resolves as Community at bootstrap
    /// (honua-server#1755) — and honors the dev-only <c>Licensing:DevGrantEdition</c> override
    /// outside Production (honua-server#1787).
    /// </summary>
    private static async Task<LicenseSnapshot> LoadBootstrapLicenseSnapshotAsync(
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        using var loggerFactory = LoggerFactory.Create(static builder => builder.AddConsole());
        var secretResolvers = CreateBootstrapLicenseSecretResolvers(loggerFactory);
        return await FileBackedLicenseService
            .LoadBootstrapSnapshotAsync(
                configuration,
                loggerFactory,
                honorDevGrant: !environment.IsProduction(),
                secretResolvers: secretResolvers)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Constructs the provider-specific <see cref="ILicenseContentSecretResolver"/> set for the
    /// bootstrap entitlement probe, mirroring the resolvers <c>AddHonuaLicensing</c> registers in
    /// DI. The AWSSDK / Azure SDK surfaces stay confined to <c>Honua.Aws</c> / <c>Honua.Azure</c>
    /// behind the same <c>HONUA_EXCLUDE_AWS</c> / <c>HONUA_EXCLUDE_AZURE</c> guards the runtime
    /// wiring uses; when a cloud SDK is excluded that resolver is simply omitted and the probe falls
    /// back to file / inline / Community resolution exactly as before. Constructing the resolvers
    /// here is cheap — each only touches the network when a matching secret reference is configured
    /// and resolved (their cloud clients are created lazily).
    /// </summary>
    private static List<ILicenseContentSecretResolver> CreateBootstrapLicenseSecretResolvers(
        ILoggerFactory loggerFactory)
    {
        var resolvers = new List<ILicenseContentSecretResolver>(capacity: 2);
#if !HONUA_EXCLUDE_AWS
        resolvers.Add(new Honua.Aws.Features.Licensing.AwsSecretsManagerLicenseContentResolver(
            loggerFactory.CreateLogger<Honua.Aws.Features.Licensing.AwsSecretsManagerLicenseContentResolver>()));
#endif
#if !HONUA_EXCLUDE_AZURE
        resolvers.Add(new Honua.Licensing.AzureKeyVaultLicenseContentResolver(
            loggerFactory.CreateLogger<Honua.Licensing.AzureKeyVaultLicenseContentResolver>()));
#endif
#if HONUA_EXCLUDE_AWS && HONUA_EXCLUDE_AZURE
        _ = loggerFactory;
#endif
        return resolvers;
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
        services.AddSingleton<IValidateOptions<Honua.Core.Features.Tiles.TileMatrixSetDefinitionOptions>>(
            new Honua.Core.Features.Tiles.TileMatrixSetOptionsValidator());
        services.AddSingleton<IValidateOptions<CacheOptions>>(new CacheOptionsValidator());
        services.AddSingleton<IValidateOptions<CloudStorageOptions>>(new CloudStorageOptionsValidator());
        services.AddSingleton<IValidateOptions<OidcAuthenticationOptions>>(new OidcAuthenticationOptionsValidator());
        services.AddSingleton<IValidateOptions<FileUploadSecurityOptions>>(new FileUploadSecurityOptionsValidator());
    }

    /// <summary>
    /// Loads the ASP.NET static web assets for the hosted Blazor demo shells (currently the
    /// optional STAC ops demo at <c>/samples/stac-ops</c>) after hardening the load against a
    /// missing compressed-asset content root.
    /// </summary>
    /// <remarks>
    /// The static-web-assets runtime manifest lists content-root directories that the loader
    /// eagerly opens as <c>PhysicalFileProvider</c> instances during host construction. In
    /// sharded Release builds one of those roots — <c>obj/&lt;Config&gt;/&lt;tfm&gt;/compressed/</c>,
    /// the Blazor precompressed-assets output — is sometimes referenced by the manifest but not
    /// materialized on disk, and <c>PhysicalFileProvider</c> throws
    /// <see cref="DirectoryNotFoundException"/> for a missing root. That aborted host
    /// construction and failed every test in the affected shard before any test logic ran
    /// (honua-server#2904). Pre-creating any missing referenced roots lets the loader open them;
    /// an empty <c>compressed/</c> root simply means content negotiation falls back to the
    /// uncompressed asset, which is the behavior the hosted-demo integration tests assert.
    /// </remarks>
    public static void LoadHostedBlazorStaticWebAssets(WebApplicationBuilder builder)
    {
        EnsureStaticWebAssetContentRootsExist();
        builder.WebHost.UseStaticWebAssets();
    }

    private static void EnsureStaticWebAssetContentRootsExist()
    {
        try
        {
            var manifests = Directory.EnumerateFiles(
                AppContext.BaseDirectory,
                "*.staticwebassets.runtime.json",
                SearchOption.TopDirectoryOnly);

            foreach (var stream in manifests.Select(File.OpenRead))
            {
                using (stream)
                {
                    using var document = JsonDocument.Parse(stream);

                    if (!document.RootElement.TryGetProperty("ContentRoots", out var contentRoots) ||
                        contentRoots.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var path in contentRoots.EnumerateArray()
                                 .Select(contentRoot => contentRoot.GetString())
                                 .OfType<string>()
                                 .Where(path => !string.IsNullOrWhiteSpace(path) && !Directory.Exists(path)))
                    {
                        Directory.CreateDirectory(path);
                    }
                }
            }
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or NotSupportedException or
                ArgumentException or JsonException)
        {
            // Best-effort hardening only. If a manifest cannot be read or a root cannot be
            // created, fall through to the loader with its normal behavior — this guard must
            // never be the reason host construction fails.
        }
    }
}
