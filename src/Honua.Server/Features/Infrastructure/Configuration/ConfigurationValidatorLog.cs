// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal static partial class ConfigurationValidatorLog
{
    [LoggerMessage(EventId = 9651, Level = LogLevel.Information, Message = "Configuration validation completed successfully. Validated {Count} sections")]
    public static partial void ValidationCompleted(ILogger logger, int count);

    [LoggerMessage(EventId = 9652, Level = LogLevel.Error, Message = "Configuration validation failed with {ErrorCount} errors and {WarningCount} warnings across {SectionCount} sections")]
    public static partial void ValidationFailed(ILogger logger, int errorCount, int warningCount, int sectionCount);

    [LoggerMessage(EventId = 9653, Level = LogLevel.Error, Message = "Configuration error: {Error}")]
    public static partial void ConfigurationError(ILogger logger, string error);

    [LoggerMessage(EventId = 9654, Level = LogLevel.Warning, Message = "Configuration warning: {Warning}")]
    public static partial void ConfigurationWarning(ILogger logger, string warning);

    [LoggerMessage(EventId = 9655, Level = LogLevel.Debug, Message = "Registered configuration options type {OptionsType} for section {SectionName}")]
    public static partial void OptionsTypeRegistered(ILogger logger, string optionsType, string sectionName);

    [LoggerMessage(EventId = 9656, Level = LogLevel.Error, Message = "Failed to validate configuration section {SectionName} of type {OptionsType}")]
    public static partial void ValidateConfigurationSectionFailed(ILogger logger, string sectionName, string optionsType, Exception exception);

    [LoggerMessage(EventId = 9657, Level = LogLevel.Debug, Message = "Failed to resolve configuration instance for {OptionsType}")]
    public static partial void ResolveConfigurationInstanceFailed(ILogger logger, string optionsType, Exception exception);
}
