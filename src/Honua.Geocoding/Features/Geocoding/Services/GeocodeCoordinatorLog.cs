// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Microsoft.Extensions.Logging;

namespace Honua.Geocoding.Features.Geocoding.Services;

internal static partial class GeocodeCoordinatorLog
{
    [LoggerMessage(
        EventId = 9850,
        Level = LogLevel.Debug,
        Message = "Provider {ProviderName} does not support {Operation}.")]
    public static partial void CapabilityNotSupported(ILogger logger, string providerName, string operation);

    [LoggerMessage(
        EventId = 9851,
        Level = LogLevel.Debug,
        Message = "{Operation} completed with provider {ProviderName} in {ElapsedMs}ms and returned {ResultCount} results.")]
    public static partial void OperationCompleted(
        ILogger logger,
        string operation,
        string providerName,
        double elapsedMs,
        int resultCount);

    [LoggerMessage(
        EventId = 9852,
        Level = LogLevel.Debug,
        Message = "{Operation} completed with provider {ProviderName} in {ElapsedMs}ms. Found match: {HasMatch}.")]
    public static partial void ReverseOperationCompleted(
        ILogger logger,
        string operation,
        string providerName,
        double elapsedMs,
        bool hasMatch);

    [LoggerMessage(
        EventId = 9853,
        Level = LogLevel.Warning,
        Message = "{Operation} failed with provider {ProviderName} after {ElapsedMs}ms.")]
    public static partial void OperationFailed(
        ILogger logger,
        string operation,
        string providerName,
        double elapsedMs,
        Exception exception);

    [LoggerMessage(
        EventId = 9854,
        Level = LogLevel.Error,
        Message = "All {Operation} attempts failed. Attempted providers: {AttemptedProviders}.")]
    public static partial void AllAttemptsFailed(
        ILogger logger,
        string operation,
        string attemptedProviders,
        Exception? exception);

    [LoggerMessage(
        EventId = 9855,
        Level = LogLevel.Warning,
        Message = "Preferred provider {ProviderName} is not available.")]
    public static partial void PreferredProviderUnavailable(ILogger logger, string providerName);
}
