// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Configuration;

internal static partial class ConfigurationServiceExtensionsLog
{
    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve secrets in configuration options {OptionsType}")]
    public static partial void OptionsSecretResolutionFailed(ILogger logger, string optionsType, Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Resolved environment secret reference for {OptionsType}.{PropertyName}")]
    public static partial void EnvironmentSecretReferenceResolved(ILogger logger, string optionsType, string propertyName);

    [LoggerMessage(Level = LogLevel.Error, Message = "Failed to resolve secret reference for {OptionsType}.{PropertyName}: {SecretRef}")]
    public static partial void SecretReferenceResolutionFailed(
        ILogger logger,
        string optionsType,
        string propertyName,
        string secretRef,
        Exception exception);

    [LoggerMessage(Level = LogLevel.Information, Message = "Starting configuration validation...")]
    public static partial void ConfigurationValidationStarting(ILogger logger);

    [LoggerMessage(
        Level = LogLevel.Warning,
        Message = "Configuration validation completed with {ErrorCount} errors and {WarningCount} warnings, but startup will continue in {Environment} environment")]
    public static partial void ConfigurationValidationContinuingWithErrors(
        ILogger logger,
        int errorCount,
        int warningCount,
        string environment);

    [LoggerMessage(Level = LogLevel.Error, Message = "Configuration validation failed due to unexpected error")]
    public static partial void ConfigurationValidationUnexpectedFailure(ILogger logger, Exception exception);
}
