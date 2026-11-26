// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Honua.Core.Features.Infrastructure.Resilience;
using Honua.Infrastructure.Models;
using Npgsql;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal static partial class ConfigurationRuntimeMiddleware
{
    private const string ServiceUnavailableDetail =
        "The service is temporarily unavailable. Please try again later.";

    public static IApplicationBuilder UseSharedConfiguration(this IApplicationBuilder app)
    {
        if (!app.ApplicationServices.GetRequiredService<IServiceProviderIsService>()
            .IsService(typeof(NpgsqlDataSource)))
        {
            return app;
        }

        var logger = app.ApplicationServices.GetRequiredService<ILogger<ConfigurationRuntimeRefresh>>();
        return app.Use(async (context, next) =>
        {
            if (IsExemptHealthPath(context.Request.Path))
            {
                await next(context).ConfigureAwait(false);
                return;
            }

            try
            {
                var refresh = context.RequestServices.GetRequiredService<ConfigurationRuntimeRefresh>();
                context.Items[RequestConfigurationGeneration.ContextKey] =
                    await refresh.RefreshAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                await HandleRefreshFailureAsync(context, logger, exception).ConfigureAwait(false);
                return;
            }

            await next(context).ConfigureAwait(false);
        });
    }

    private static bool IsExemptHealthPath(PathString path)
        => path.Equals("/healthz/live", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/healthz/live/", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/healthz/deep", StringComparison.OrdinalIgnoreCase)
            || path.Equals("/healthz/deep/", StringComparison.OrdinalIgnoreCase);

    private static Task HandleRefreshFailureAsync(
        HttpContext context,
        ILogger logger,
        Exception exception)
    {
        if (ExceptionMapper.IsTransientDatabaseFailure(exception))
        {
            RefreshDatabaseUnavailable(logger, StartupDatabaseResilience.DescribeFailureClass(exception));
            return StandardErrorHelpers.CreateFromException(context, exception).ExecuteAsync(context);
        }

        RefreshFailed(logger, exception);
        return StandardErrorHelpers.CreateServiceUnavailable(
            context, ServiceUnavailableDetail, retryable: true).ExecuteAsync(context);
    }

    [LoggerMessage(Level = LogLevel.Error, Message = "Shared authentication configuration could not be refreshed.")]
    private static partial void RefreshFailed(ILogger logger, Exception exception);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Shared authentication configuration refresh deferred; database unavailable: {FailureClass}")]
    private static partial void RefreshDatabaseUnavailable(ILogger logger, string failureClass);
}
