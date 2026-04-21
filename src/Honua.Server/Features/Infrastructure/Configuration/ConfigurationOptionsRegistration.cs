// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using Honua.Core.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal interface IConfigurationOptionsRegistration
{
    ConfigurationOptionsMetadata Metadata { get; }

    IReadOnlyList<ConfigurationPropertyAccessor> PropertyAccessors { get; }

    object? GetConfiguredOptions(IServiceProvider serviceProvider);
}

internal sealed class ConfigurationOptionsRegistration<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions> : IConfigurationOptionsRegistration
    where TOptions : class
{
    public ConfigurationOptionsRegistration(
        string sectionName,
        bool isRequired)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        SectionName = sectionName;
        Metadata = ConfigurationOptionsMetadataFactory.Create<TOptions>(sectionName, isRequired);
        PropertyAccessors = ConfigurationOptionsMetadataFactory.CreatePropertyAccessors<TOptions>();
    }

    public string SectionName { get; }

    public ConfigurationOptionsMetadata Metadata { get; }

    public IReadOnlyList<ConfigurationPropertyAccessor> PropertyAccessors { get; }

    public object? GetConfiguredOptions(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configureOptions = serviceProvider.GetServices<IConfigureOptions<TOptions>>().ToArray();
        if (configureOptions.Length == 0)
        {
            return null;
        }

        var options = Activator.CreateInstance<TOptions>();

        foreach (var configure in configureOptions)
        {
            if (configure is IConfigureNamedOptions<TOptions> namedConfigure)
            {
                namedConfigure.Configure(Options.DefaultName, options);
            }
            else
            {
                configure.Configure(options);
            }
        }

        foreach (var postConfigure in serviceProvider.GetServices<IPostConfigureOptions<TOptions>>())
        {
            postConfigure.PostConfigure(Options.DefaultName, options);
        }

        return options;
    }
}

internal sealed class ManualConfigurationOptionsRegistration<
    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions> : IConfigurationOptionsRegistration
    where TOptions : class
{
    private readonly IConfiguration _configuration;

    public ManualConfigurationOptionsRegistration(
        IConfiguration configuration,
        string sectionName,
        bool isRequired)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        _configuration = configuration;
        SectionName = sectionName;
        Metadata = ConfigurationOptionsMetadataFactory.Create<TOptions>(sectionName, isRequired);
        PropertyAccessors = ConfigurationOptionsMetadataFactory.CreatePropertyAccessors<TOptions>();
    }

    public string SectionName { get; }

    public ConfigurationOptionsMetadata Metadata { get; }

    public IReadOnlyList<ConfigurationPropertyAccessor> PropertyAccessors { get; }

    [UnconditionalSuppressMessage(
        "AOT",
        "SYSLIB1104",
        Justification = "This fallback only exists for manually registered options types that do not have a typed options pipeline. NativeAOT startup uses typed registrations instead.")]
    public object? GetConfiguredOptions(IServiceProvider serviceProvider)
    {
        ArgumentNullException.ThrowIfNull(serviceProvider);

        var configureOptions = serviceProvider.GetServices<IConfigureOptions<TOptions>>().ToArray();
        if (configureOptions.Length > 0)
        {
            var options = Activator.CreateInstance<TOptions>();

            foreach (var configure in configureOptions)
            {
                if (configure is IConfigureNamedOptions<TOptions> namedConfigure)
                {
                    namedConfigure.Configure(Options.DefaultName, options);
                }
                else
                {
                    configure.Configure(options);
                }
            }

            foreach (var postConfigure in serviceProvider.GetServices<IPostConfigureOptions<TOptions>>())
            {
                postConfigure.PostConfigure(Options.DefaultName, options);
            }

            return options;
        }

        var instance = Activator.CreateInstance<TOptions>();
        var bindMethod = typeof(ConfigurationBinder).GetMethod(
            nameof(ConfigurationBinder.Bind),
            new[] { typeof(IConfiguration), typeof(object) });
        bindMethod?.Invoke(null, new object?[] { _configuration.GetSection(SectionName), instance });
        return instance;
    }
}

internal sealed record ConfigurationPropertyAccessor(
    string Name,
    Func<object, object?> GetValue,
    IReadOnlyList<ConfigurationValidationAttribute> ValidationAttributes);

internal static class ConfigurationOptionsMetadataFactory
{
    public static ConfigurationOptionsMetadata Create<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties | DynamicallyAccessedMemberTypes.PublicParameterlessConstructor)] TOptions>(
        string sectionName,
        bool isRequired)
        where TOptions : class
    {
        var type = typeof(TOptions);
        var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
        var propertyMetadata = properties.Select(CreatePropertyMetadata).ToList();
        var description = GetTypeDescription(type);

        return new ConfigurationOptionsMetadata(sectionName, type, propertyMetadata, isRequired, description);
    }

    public static IReadOnlyList<ConfigurationPropertyAccessor> CreatePropertyAccessors<
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)] TOptions>()
    {
        return typeof(TOptions)
            .GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(static property => property.CanRead && property.GetIndexParameters().Length == 0)
            .Select(property => new ConfigurationPropertyAccessor(
                property.Name,
                property.GetValue,
                property.GetCustomAttributes<ConfigurationValidationAttribute>().ToArray()))
            .Where(static property => property.ValidationAttributes.Count > 0)
            .ToArray();
    }

    private static ConfigurationPropertyMetadata CreatePropertyMetadata(PropertyInfo property)
    {
        var defaultValue = property.GetCustomAttribute<DefaultValueAttribute>()?.Value;
        var isRequired = property.GetCustomAttributes<RequiredAttribute>().Any()
            || property.GetCustomAttributes<RequiredConfigurationAttribute>().Any();
        var description = GetPropertyDescription(property);
        var validationAttributes = property.GetCustomAttributes<ValidationAttribute>().ToArray();

        return new ConfigurationPropertyMetadata(
            property.Name,
            property.PropertyType,
            defaultValue,
            isRequired,
            description,
            validationAttributes);
    }

    private static string? GetTypeDescription(Type type)
    {
        return type.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
    }

    private static string? GetPropertyDescription(PropertyInfo property)
    {
        return property.GetCustomAttribute<System.ComponentModel.DescriptionAttribute>()?.Description;
    }
}
