// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Diagnostics.CodeAnalysis;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace Honua.Plugins;

/// <summary>
/// DI registration for the Honua plugin/extension SDK (issues #347, #1562).
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers the plugin edit pipeline, computed-field pipeline, output-format registry,
    /// background-service host, any read-only data-store providers, and any compile-time plugins
    /// supplied via <paramref name="configure"/>. The pipelines and the output-format registry are
    /// always registered (as no-ops when no plugins are present or the Enterprise <c>plugin.sdk</c>
    /// entitlement is inactive) so protocol/export handlers can depend on
    /// <see cref="IPluginEditPipeline"/> / <see cref="IComputedFieldPipeline"/> /
    /// <see cref="IFeatureOutputFormatRegistry"/> unconditionally. Data-store plugins are registered
    /// as additional <c>IFeatureDataProvider</c>s picked up by the server's existing provider
    /// registry/router (issue #2856, ADR-0067). Declared inter-plugin dependencies and
    /// minimum-server-version constraints are validated at registration time and fail fast.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration (binds the <c>Plugins</c> section).</param>
    /// <param name="configure">Optional callback to register plugin types via the builder.</param>
    /// <returns>The same service collection for chaining.</returns>
    public static IServiceCollection AddHonuaPlugins(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IHonuaPluginBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<PluginOptions>(configuration.GetSection(PluginOptions.SectionName));

        var builder = new HonuaPluginBuilder(services);
        configure?.Invoke(builder);

        PluginDependencyResolver.Validate(builder.Registrations, ResolveServerVersion());

        services.TryAddSingleton(new PluginCatalog(builder.Registrations));

        // Scoped, not singleton: the pipeline consumes the scoped IAuditLog (PostgresAuditLog
        // needs a per-request DB connection), so a singleton would be a captive dependency that
        // fails ValidateScopes/ValidateOnBuild in Development. Its only consumer —
        // FeatureServerEditsDependencies — is itself scoped, and the validators/hooks it
        // aggregates are singletons (safe to consume from a scoped service).
        services.TryAddScoped<IPluginEditPipeline, PluginEditPipeline>();

        // Computed-field pipeline is read-path only and consumes singleton providers + the
        // singleton license service, so it can be a singleton.
        services.TryAddSingleton<IComputedFieldPipeline, ComputedFieldPipeline>();

        // Output-format registry (issue #2856, ADR-0067). Registered as the real, license-gated
        // registry only when at least one output-format plugin is present; otherwise the export /
        // format-negotiation seam gets the zero-overhead no-op so it can depend on
        // IFeatureOutputFormatRegistry unconditionally. It aggregates singleton IFeatureOutputFormat
        // registrations and the singleton license service, so it can be a singleton.
        if (builder.Registrations.Any(r => r.ProvidesOutputFormat))
        {
            services.TryAddSingleton<IFeatureOutputFormatRegistry, FeatureOutputFormatRegistry>();
        }
        else
        {
            services.TryAddSingleton<IFeatureOutputFormatRegistry>(NoOpFeatureOutputFormatRegistry.Instance);
        }

        // Host plugin background services with failure isolation. Only registered when at least
        // one is present so the common path adds no hosted service.
        if (builder.Registrations.Any(r => r.ProvidesBackgroundService))
        {
            services.AddSingleton<IHostedService, PluginBackgroundServiceHost>();
        }

        return services;
    }

    /// <summary>
    /// Binds a per-plugin configuration section (<c>Plugins:&lt;pluginId&gt;:*</c>) to an options
    /// type with validation. This gives plugins isolated, namespaced configuration beyond the
    /// global <c>Plugins:Enabled</c> kill-switch. Options are validated eagerly at startup
    /// (<c>ValidateOnStart</c>) so misconfiguration fails fast.
    /// </summary>
    /// <typeparam name="TOptions">The plugin options POCO (uses DataAnnotations for validation).</typeparam>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <param name="pluginId">The plugin id whose section to bind.</param>
    /// <returns>The same service collection for chaining.</returns>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "ValidateDataAnnotations reflects over the options type, whose public and non-public properties are preserved by the DynamicallyAccessedMembers annotation on TOptions.")]
    public static IServiceCollection AddPluginOptions<[DynamicallyAccessedMembers(
        DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.NonPublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string pluginId)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(pluginId);

        var sectionPath = $"{PluginOptions.SectionName}:{pluginId}";
        var section = configuration.GetSection(sectionPath);
        services.AddOptions<TOptions>()
            // Bind via the reflection-based binder indirection: the configuration-binding source
            // generator cannot emit a typed binder for the open generic TOptions (SYSLIB1104), so
            // the bind is routed through ReflectionBind to keep the call site out of the
            // generator's syntactic match. The DynamicallyAccessedMembers annotation on TOptions
            // preserves the members the reflection binder needs under trimming/AOT.
            .Configure(options => ReflectionBind(section, options))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        return services;
    }

    // MethodInfo for the reflection-based ConfigurationBinder.Bind(IConfiguration, object).
    // Invoking the binder through reflection keeps the call site out of the configuration-binding
    // source generator's syntactic match, which cannot emit a typed binder for the open generic
    // TOptions (SYSLIB1104). The concrete services.Configure<PluginOptions>(...) bind elsewhere in
    // this file still benefits from the generator.
    private static readonly System.Reflection.MethodInfo BindMethod = ResolveBindMethod();

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Resolves the ConfigurationBinder.Bind(IConfiguration, object) overload by reflection so the bind stays out of the configuration-binding source generator's match; the bound options type's members are preserved by the DynamicallyAccessedMembers annotation on AddPluginOptions' TOptions parameter.")]
    private static System.Reflection.MethodInfo ResolveBindMethod()
        => typeof(ConfigurationBinder).GetMethod(
            nameof(ConfigurationBinder.Bind),
            [typeof(IConfiguration), typeof(object)])
        ?? throw new InvalidOperationException("ConfigurationBinder.Bind(IConfiguration, object) overload was not found.");

    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "The bound options type's members are preserved by the DynamicallyAccessedMembers annotation on AddPluginOptions' TOptions parameter.")]
    private static void ReflectionBind(IConfiguration section, object instance)
        => BindMethod.Invoke(null, [section, instance]);

    private static Version ResolveServerVersion()
    {
        // AssemblyName.Version is trim-/AOT-safe (unlike reading the informational-version
        // attribute). Dev/test builds carry 0.0.0.0; treat an unset/zero version as "newest" so
        // an unstamped local build never trips a plugin's MinimumServerVersion guard.
        var version = typeof(ServiceCollectionExtensions).Assembly.GetName().Version;
        return version is null || version == new Version(0, 0, 0, 0)
            ? new Version(int.MaxValue, 0, 0)
            : version;
    }
}
