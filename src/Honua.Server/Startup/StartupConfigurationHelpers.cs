// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

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
using Honua.Server.Features.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Options;

namespace Honua.Server.Startup;

internal static class StartupConfigurationHelpers
{
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

    public static void ResolveEnvironmentSecretReferences(ConfigurationManager configuration)
    {
        ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:DefaultConnection");
        ResolveEnvironmentSecretReference(configuration, "ConnectionStrings:redis");
        ResolveEnvironmentSecretReference(configuration, "Aspire:StackExchange:Redis:ConnectionString");
        ResolveEnvironmentSecretReference(configuration, "HONUA_ADMIN_PASSWORD");
        ResolveEnvironmentSecretReference(configuration, "Security:ConnectionEncryption:MasterKey");
        ResolveEnvironmentSecretReference(configuration, AuditChainKeyConfigurationKey);
        ResolveEnvironmentSecretReference(configuration, OidcSigningKeyConfigurationKey);
    }

    // The audit-chain key must be resolved before its consumers snapshot it.
    internal const string AuditChainKeyConfigurationKey = "AuditLog:ChainVerification:Key";

    internal const string OidcSigningKeyConfigurationKey = "Oidc:TokenValidation:SymmetricSigningKey";

    internal static readonly IReadOnlyList<string> SecuritySecretReferenceKeys =
    [
        "HONUA_ADMIN_PASSWORD",
        "Security:ConnectionEncryption:MasterKey",
        AuditChainKeyConfigurationKey,
        OidcSigningKeyConfigurationKey
    ];

    // A secret reference must never become literal key material.
    public static async Task ResolveSecuritySecretReferencesAsync(
        ConfigurationManager configuration,
        bool isProduction,
        CancellationToken cancellationToken = default)
    {
        const string awsSecretsManagerPrefix = "aws:secretsmanager:";
        var keys = SecuritySecretReferenceKeys;

        // Normalize references for direct callers as well. Program.cs finalizes source precedence
        // before its initial environment-reference pass, so security-file overrides are already visible.
        foreach (var key in keys)
        {
            ResolveEnvironmentSecretReference(configuration, key);
        }

        if (!keys.Any(key => configuration[key]?.StartsWith(
                awsSecretsManagerPrefix,
                StringComparison.OrdinalIgnoreCase) == true))
        {
            return;
        }

        using var lease = BootstrapSecretResolver.Create();
        var resolver = lease.Resolver;

        await ResolveSecuritySecretReferencesAsync(configuration, resolver, keys, isProduction, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task ResolveSecuritySecretReferencesAsync(
        ConfigurationManager configuration,
        Honua.Core.Features.Security.Abstractions.IConnectionSecretResolver resolver,
        IEnumerable<string> keys,
        bool isProduction,
        CancellationToken cancellationToken = default)
    {
        const string awsSecretsManagerPrefix = "aws:secretsmanager:";
        foreach (var key in keys)
        {
            var reference = configuration[key];
            if (string.IsNullOrWhiteSpace(reference) ||
                !reference.StartsWith(awsSecretsManagerPrefix, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!resolver.CanResolve(reference))
            {
                throw new InvalidOperationException(
                    $"The AWS Secrets Manager reference configured for security setting '{key}' is invalid or cannot be resolved.");
            }

            string? resolved;
            try
            {
                resolved = await resolver.ResolveSecretAsync(reference, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                if (string.Equals(key, OidcSigningKeyConfigurationKey, StringComparison.OrdinalIgnoreCase))
                {
                    // Provider exceptions may contain the secret reference or material. The host
                    // prints startup exceptions, so keep this authentication failure value-free.
                    throw new InvalidOperationException(
                        $"Failed to resolve the security setting '{key}' from AWS Secrets Manager.");
                }

                throw new InvalidOperationException(
                    $"Failed to resolve the security setting '{key}' from AWS Secrets Manager.",
                    ex);
            }

            if (string.IsNullOrEmpty(resolved) ||
                (string.Equals(key, OidcSigningKeyConfigurationKey, StringComparison.OrdinalIgnoreCase) &&
                 string.IsNullOrWhiteSpace(resolved)))
            {
                throw new InvalidOperationException(
                    $"AWS Secrets Manager returned an empty value for the security setting '{key}'.");
            }

            // Authentication handlers deliberately keep the reference and resolve it on each
            // request so a warm process observes secret rotation. Resolve once here only to fail
            // startup on an inaccessible or weak production credential. The connection-encryption
            // master key is different: changing it while a process is live would make existing
            // ciphertext unreadable, so that key remains a process-lifetime snapshot. The audit-chain
            // key is a snapshot for the same reason: a mid-process change breaks chain verification.
            // OIDC static keys are captured by bearer options at startup and require a restart to rotate.
            if (string.Equals(key, "HONUA_ADMIN_PASSWORD", StringComparison.OrdinalIgnoreCase))
            {
                if (isProduction)
                {
                    AdminPasswordValidation.ValidateProductionPassword(resolved);
                }

                continue;
            }

            SetResolvedSecretValue(configuration, key, resolved);
        }
    }

    private static void ResolveEnvironmentSecretReference(ConfigurationManager configuration, string key)
    {
        var value = configuration[key];
        string? resolved;
        try
        {
            resolved = SecretReferenceResolver.ResolveEnvironmentReference(value, key);
        }
        catch (InvalidOperationException) when (string.Equals(key, OidcSigningKeyConfigurationKey, StringComparison.OrdinalIgnoreCase))
        {
            // Startup exceptions are logged. Keep the signing-key reference out of the
            // diagnostic and its inner exceptions, as with AWS resolution failures.
            throw new InvalidOperationException(
                $"Failed to resolve the security setting '{key}' from the environment.");
        }

        if (!string.Equals(value, resolved, StringComparison.Ordinal))
        {
            SetResolvedSecretValue(configuration, key, resolved);
        }
    }

    private static void SetResolvedSecretValue(ConfigurationManager configuration, string key, string? value)
    {
        // Indexer writes modify provider instances only. Reordering sources rebuilds
        // those instances and reloads the original environment/JSON references.
        // InitialData survives rebuilding and keeps this process-lifetime snapshot last.
        configuration.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
    }

    // The Redis reference is resolved before the client parses it, so the raw reference is never handed to the multiplexer.
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

    // A path that already names a file is left alone, so a mounted certificate wins.
    public static async Task EnsureKeyRingCertificateMaterializedAsync(
        ConfigurationManager configuration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        var path = configuration[OperationSecretKeyRingProtection.CertificatePathKey];
        if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
        {
            return;
        }

        var material = configuration[OperationSecretKeyRingProtection.CertificateMaterialKey];
        if (string.IsNullOrWhiteSpace(material))
        {
            return;
        }

        material = await ResolveKeyRingMaterialAsync(material, cancellationToken).ConfigureAwait(false);
        var written = OperationSecretKeyRingProtection.WritePkcs12Material(material);
        SetResolvedSecretValue(configuration, OperationSecretKeyRingProtection.CertificatePathKey, written.Path);
        if (!string.IsNullOrEmpty(written.Password))
        {
            SetResolvedSecretValue(
                configuration,
                OperationSecretKeyRingProtection.CertificatePasswordKey,
                written.Password);
        }
    }

    private static async Task<string> ResolveKeyRingMaterialAsync(
        string material,
        CancellationToken cancellationToken)
    {
        // Production validation accepts env/aws/azure references for this key, so an
        // env: reference has to resolve here too - otherwise it would validate and
        // then die in WritePkcs12Material as "not a PKCS#12 bundle".
        material = SecretReferenceResolver.ResolveEnvironmentReference(
            material,
            OperationSecretKeyRingProtection.CertificateMaterialKey) ?? material;

        const string awsSecretsManagerPrefix = "aws:secretsmanager:";
        if (!material.StartsWith(awsSecretsManagerPrefix, StringComparison.OrdinalIgnoreCase))
        {
            return material;
        }

        using var lease = BootstrapSecretResolver.Create();
        var resolver = lease.Resolver;
        var resolved = await resolver.ResolveSecretAsync(material, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(resolved))
        {
            throw new InvalidOperationException(
                $"'{OperationSecretKeyRingProtection.CertificateMaterialKey}' did not resolve to a certificate.");
        }

        return resolved;
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

        using var lease = BootstrapSecretResolver.Create();
        var resolver = lease.Resolver;

        await ResolveRedisConnectionSecretReferenceAsync(configuration, key, resolver, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static async Task ResolveRedisConnectionSecretReferenceAsync(
        ConfigurationManager configuration,
        string key,
        Honua.Core.Features.Security.Abstractions.IConnectionSecretResolver resolver,
        CancellationToken cancellationToken = default)
    {
        var value = configuration[key];
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
            SetResolvedSecretValue(configuration, key, resolved);
        }
    }

    // The development grant is never honored in Production.
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

    // caching.output-cache (Pro) is deliberately NOT gated here. The Redis-cache gate has to run
    // at boot because it decides service registration (IConnectionMultiplexer, the durable job
    // store), but the output-cache middleware is a pipeline branch, so it is gated per request
    // against the live license snapshot in Program.cs instead — a boot-time capture would keep
    // serving cached responses after a Pro license expired at runtime and would ignore a license
    // applied through ILicenseManager.ApplyLicenseAsync.

    // Bootstrap uses the same licence resolvers as the request path, so a secret-only licence is not read as the community edition.
    internal static async Task<LicenseSnapshot> LoadBootstrapLicenseSnapshotAsync(
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

    private static List<ILicenseContentSecretResolver> CreateBootstrapLicenseSecretResolvers(
        ILoggerFactory loggerFactory)
    {
        var resolvers = new List<ILicenseContentSecretResolver>(capacity: 2);
#if !HONUA_EXCLUDE_AWS
        resolvers.Add(new Honua.Cloud.Aws.Features.Licensing.AwsSecretsManagerLicenseContentResolver(
            loggerFactory.CreateLogger<Honua.Cloud.Aws.Features.Licensing.AwsSecretsManagerLicenseContentResolver>()));
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

    public static void LoadHostedBlazorStaticWebAssets(WebApplicationBuilder builder)
    {
        EnsureStaticWebAssetContentRootsExist();
        builder.WebHost.UseStaticWebAssets();
    }

    // Missing content roots are created before the host builder, because the development loader throws when a declared root is absent.
    public static void EnsureStaticWebAssetContentRootsExist()
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
