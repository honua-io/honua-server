// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Reflection;
using System.Diagnostics.CodeAnalysis;
using Honua.Core.Configuration;
using Honua.Core.Configuration.Validation;
using Honua.Core.Features.Configuration;
using Honua.Core.Features.Security.Abstractions;
using Honua.Postgres.Features.Security.ConnectionSecretResolvers;
using Honua.Server.Features.Infrastructure.Helpers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

/// <summary>
/// Service collection extensions for registering centralized secret management and configuration validation.
/// </summary>
public static class ConfigurationServiceExtensions
{
    /// <summary>
    /// Adds centralized configuration management with environment-aware defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configuration">The application configuration.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddConfigurationManagement(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var environmentName =
            Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT") ??
            Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT");
        var isDevelopment = string.Equals(
            environmentName,
            Environments.Development,
            StringComparison.OrdinalIgnoreCase);

        return services.AddStandardConfiguration(configuration, isDevelopment);
    }

    /// <summary>
    /// Adds centralized secret management services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration instance</param>
    /// <param name="isDevelopment">Whether running in development environment</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddSecretManagement(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        // Configure secret provider options
        services.Configure<SecretProviderOptions>(
            configuration.GetSection(SecretProviderOptions.SectionName));

        // Configure secure configuration options
        services.Configure<SecureConfigurationOptions>(options =>
        {
            configuration.GetSection(SecureConfigurationOptions.SectionName).Bind(options);

            // Override defaults based on environment
            if (isDevelopment)
            {
                options.FailOnSecretValidationError = false; // Be more lenient in development
            }
        });

        // Register HTTP clients for secret resolvers
        services.AddHttpClient("AzureKeyVault", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Honua-AzureKeyVault/1.0");
        });

        services.AddHttpClient("AzureManagedIdentityMetadata", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
            client.DefaultRequestHeaders.Add("User-Agent", "Honua-AzureManagedIdentity/1.0");
        });

        services.AddHttpClient("AwsSecretsManager", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(30);
            client.DefaultRequestHeaders.Add("User-Agent", "Honua-AwsSecretsManager/1.0");
        });

        services.AddHttpClient("AwsMetadata", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5);
            client.DefaultRequestHeaders.Add("User-Agent", "Honua-AwsMetadata/1.0");
        });

        // Register secret resolvers. When Postgres secure-connection services are
        // already registered, reuse that composite resolver instead of overriding
        // it with a self-referential alias that recurses during DI resolution.
        services.TryAddSingleton<EnvironmentSecretResolver>();
        services.TryAddSingleton<NullSecretResolver>();
        services.TryAddSingleton<AzureKeyVaultResolver>();
        services.TryAddSingleton<AwsSecretsManagerResolver>();
        services.TryAddSingleton<IConnectionSecretResolver>(serviceProvider =>
        {
            var logger = serviceProvider.GetRequiredService<ILogger<CompositeSecretResolver>>();
            var resolvers = new IConnectionSecretResolver[]
            {
                serviceProvider.GetRequiredService<EnvironmentSecretResolver>(),
                serviceProvider.GetRequiredService<AwsSecretsManagerResolver>(),
                serviceProvider.GetRequiredService<AzureKeyVaultResolver>(),
                serviceProvider.GetRequiredService<NullSecretResolver>()
            };

            return new CompositeSecretResolver(resolvers, logger);
        });

        // Register centralized secret provider
        services.AddSingleton<ISecretProvider, SecretProvider>();

        return services;
    }

    /// <summary>
    /// Adds comprehensive configuration validation services to the dependency injection container.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration instance</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddConfigurationValidation(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Register configuration validator and discovery services
        services.AddSingleton<IConfigurationValidator, ConfigurationValidator>();
        services.AddSingleton<IConfigurationDiscovery>(sp => (ConfigurationValidator)sp.GetRequiredService<IConfigurationValidator>());

        // Register standard TTL configuration
        services.Configure<StandardTtlOptions>(configuration.GetSection(StandardTtlOptions.SectionName));

        // Register configuration validation startup service
        services.AddHostedService<ConfigurationValidationStartupService>();

        return services;
    }

    /// <summary>
    /// Registers a configuration options type for validation and automatic secret resolution.
    /// </summary>
    /// <typeparam name="TOptions">The configuration options type</typeparam>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration instance</param>
    /// <param name="sectionName">The configuration section name</param>
    /// <param name="isRequired">Whether this configuration is required for startup</param>
    /// <param name="enableSecretResolution">Whether to enable automatic secret resolution</param>
    /// <returns>The service collection for chaining</returns>
    [UnconditionalSuppressMessage(
        "AOT",
        "SYSLIB1104",
        Justification = "This generic helper is retained for non-AOT/test usage. NativeAOT startup uses concrete registrations in AddStandardConfiguration.")]
    [RequiresUnreferencedCode("Generic configuration binding helper is not trim-safe. Use AddStandardConfiguration or concrete option registrations in NativeAOT-critical paths.")]
    public static IServiceCollection ConfigureWithValidation<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        this IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        bool isRequired = true,
        bool enableSecretResolution = true)
        where TOptions : class
    {
        // Secret provider options must bind directly. Resolving them through
        // ISecretProvider would require constructing the provider to configure
        // the options the provider itself consumes, creating a DI cycle.
        return RegisterConfiguredOptions<TOptions>(
            services,
            configuration,
            sectionName,
            isRequired,
            enableSecretResolution,
            static (collection, config, name) =>
            {
                collection.Configure<TOptions>(options =>
                {
                    var bindMethod = typeof(ConfigurationBinder).GetMethod(
                        nameof(ConfigurationBinder.Bind),
                        new[] { typeof(IConfiguration), typeof(object) });
                    bindMethod?.Invoke(null, new object[] { config.GetSection(name), options! });
                });
            });
    }

    /// <summary>
    /// Adds all standard configuration types with validation and secret resolution.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="configuration">The configuration instance</param>
    /// <param name="isDevelopment">Whether running in development environment</param>
    /// <returns>The service collection for chaining</returns>
    public static IServiceCollection AddStandardConfiguration(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment = false)
    {
        // Add core configuration services
        services.AddSecretManagement(configuration, isDevelopment);
        services.AddConfigurationValidation(configuration);

        // Register standard configuration types
        RegisterConfiguredOptions<StandardTtlOptions>(
            services,
            configuration,
            StandardTtlOptions.SectionName,
            isRequired: true,
            enableSecretResolution: true,
            static (collection, config, name) => collection.Configure<StandardTtlOptions>(config.GetSection(name)));
        RegisterConfiguredOptions<SecretProviderOptions>(
            services,
            configuration,
            SecretProviderOptions.SectionName,
            isRequired: true,
            enableSecretResolution: false,
            static (collection, config, name) => collection.Configure<SecretProviderOptions>(config.GetSection(name)));
        RegisterConfiguredOptions<SecureConfigurationOptions>(
            services,
            configuration,
            SecureConfigurationOptions.SectionName,
            isRequired: true,
            enableSecretResolution: true,
            static (collection, config, name) => collection.Configure<SecureConfigurationOptions>(config.GetSection(name)));
        RegisterConfiguredOptions<Core.Features.Caching.CacheOptions>(
            services,
            configuration,
            Core.Features.Caching.CacheOptions.SectionName,
            isRequired: true,
            enableSecretResolution: true,
            static (collection, config, name) => collection.Configure<Core.Features.Caching.CacheOptions>(config.GetSection(name)));

        return services;
    }

    private static IServiceCollection RegisterConfiguredOptions<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        IServiceCollection services,
        IConfiguration configuration,
        string sectionName,
        bool isRequired,
        bool enableSecretResolution,
        Action<IServiceCollection, IConfiguration, string> bindOptions)
        where TOptions : class
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);
        ArgumentNullException.ThrowIfNull(bindOptions);

        var shouldEnableSecretResolution =
            enableSecretResolution && typeof(TOptions) != typeof(SecretProviderOptions);

        services.AddSingleton<IConfigurationOptionsRegistration>(
            new ConfigurationOptionsRegistration<TOptions>(sectionName, isRequired));

        bindOptions(services, configuration, sectionName);

        if (shouldEnableSecretResolution)
        {
            services.AddSingleton<IPostConfigureOptions<TOptions>, SecretResolutionPostConfigureOptions<TOptions>>();
        }

        services.AddSingleton<IValidateOptions<TOptions>, DataAnnotationValidateOptions<TOptions>>();
        return services;
    }
}

/// <summary>
/// Post-configuration service that resolves secrets in options after binding.
/// </summary>
internal sealed class SecretResolutionPostConfigureOptions<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions> : IPostConfigureOptions<TOptions>
    where TOptions : class
{
    private readonly ISecretProvider _secretProvider;
    private readonly ILogger<SecretResolutionPostConfigureOptions<TOptions>> _logger;

    public SecretResolutionPostConfigureOptions(
        ISecretProvider secretProvider,
        ILogger<SecretResolutionPostConfigureOptions<TOptions>> logger)
    {
        _secretProvider = secretProvider ?? throw new ArgumentNullException(nameof(secretProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public void PostConfigure(string? name, TOptions options)
    {
        if (options == null) return;

        var optionsTypeName = typeof(TOptions).Name;

        try
        {
            ResolveSecretsInOptions(options);
        }
        catch (Exception ex)
        {
            ConfigurationServiceExtensionsLog.OptionsSecretResolutionFailed(_logger, optionsTypeName, ex);
            throw;
        }
    }

    private void ResolveSecretsInOptions(TOptions options)
    {
        var optionsTypeName = typeof(TOptions).Name;
        var properties = typeof(TOptions).GetProperties(
            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

        foreach (var property in properties)
        {
            if (property.PropertyType == typeof(string) && property.CanWrite && property.CanRead)
            {
                var value = property.GetValue(options) as string;
                if (string.IsNullOrWhiteSpace(value))
                {
                    continue;
                }

                try
                {
                    if (SecretReferenceResolver.IsEnvironmentReference(value))
                    {
                        var resolvedValue = SecretReferenceResolver.ResolveEnvironmentReference(
                            value,
                            $"{optionsTypeName}.{property.Name}");
                        property.SetValue(options, resolvedValue);
                        ConfigurationServiceExtensionsLog.EnvironmentSecretReferenceResolved(
                            _logger,
                            optionsTypeName,
                            property.Name);
                        continue;
                    }

                    if (_secretProvider.IsSecretReference(value))
                    {
                        throw new InvalidOperationException(
                            $"Configuration option '{optionsTypeName}.{property.Name}' uses secret reference '{value}', but only env: references are supported for startup-bound option binding.");
                    }
                }
                catch (Exception ex)
                {
                    ConfigurationServiceExtensionsLog.SecretReferenceResolutionFailed(
                        _logger,
                        optionsTypeName,
                        property.Name,
                        value,
                        ex);
                    throw;
                }
            }
        }
    }
}

/// <summary>
/// Startup service that validates all configuration at application start.
/// </summary>
internal sealed class ConfigurationValidationStartupService : IHostedService
{
    private readonly IConfigurationValidator _validator;
    private readonly IHostEnvironment _environment;
    private readonly SecureConfigurationOptions _secureOptions;
    private readonly ILogger<ConfigurationValidationStartupService> _logger;

    public ConfigurationValidationStartupService(
        IConfigurationValidator validator,
        IHostEnvironment environment,
        IOptions<SecureConfigurationOptions> secureOptions,
        ILogger<ConfigurationValidationStartupService> logger)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _environment = environment ?? throw new ArgumentNullException(nameof(environment));
        _secureOptions = secureOptions.Value ?? throw new ArgumentNullException(nameof(secureOptions));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ConfigurationServiceExtensionsLog.ConfigurationValidationStarting(_logger);

        try
        {
            var validationResult = await _validator.ValidateAllAsync(
                _environment.IsDevelopment(),
                _environment.IsEnvironment("Test"))
                .ConfigureAwait(false);

            if (!validationResult.IsValid)
            {
                var shouldFail = _secureOptions.FailOnSecretValidationError && !_environment.IsDevelopment();

                if (shouldFail)
                {
                    throw new InvalidOperationException(
                        $"Configuration validation failed with {validationResult.TotalErrors} errors. " +
                        "Application startup aborted. See logs for details.");
                }

                ConfigurationServiceExtensionsLog.ConfigurationValidationContinuingWithErrors(
                    _logger,
                    validationResult.TotalErrors,
                    validationResult.TotalWarnings,
                    _environment.EnvironmentName);
            }
            else
            {
                ConfigurationValidatorLog.ValidationCompleted(_logger, validationResult.Results.Count);
            }
        }
        catch (Exception ex) when (!(ex is InvalidOperationException))
        {
            ConfigurationServiceExtensionsLog.ConfigurationValidationUnexpectedFailure(_logger, ex);

            if (_secureOptions.FailOnSecretValidationError && !_environment.IsDevelopment())
            {
                throw;
            }
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}

/// <summary>
/// Data annotation validate options that integrates with the configuration validator.
/// </summary>
internal sealed class DataAnnotationValidateOptions<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions> : IValidateOptions<TOptions>
    where TOptions : class
{
    private readonly IConfigurationValidator _validator;
    private readonly string _sectionName;

    public DataAnnotationValidateOptions(IConfigurationValidator validator, IConfiguration configuration)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));

        // Try to determine section name from registered metadata
        try
        {
            var metadata = _validator.GetOptionsMetadata<TOptions>();
            _sectionName = metadata.SectionName;
        }
        catch
        {
            _sectionName = typeof(TOptions).Name;
        }
    }

    public ValidateOptionsResult Validate(string? name, TOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("Options instance is null");
        }

        var result = _validator.ValidateOptions(options, _sectionName, isDevelopment: false);

        if (result.IsValid)
        {
            return ValidateOptionsResult.Success;
        }

        var failures = result.Errors.Select(e => e.ErrorMessage ?? "Validation failed").ToArray();
        return ValidateOptionsResult.Fail(failures);
    }
}
