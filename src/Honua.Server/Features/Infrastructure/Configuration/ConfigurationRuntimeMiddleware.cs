// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal static partial class ConfigurationRuntimeMiddleware
{
    public static IApplicationBuilder UseSharedConfiguration(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<NpgsqlDataSource>() is null) { return app; }
        var refresh = app.ApplicationServices.GetRequiredService<ConfigurationRuntimeRefresh>();
        var logger = app.ApplicationServices.GetRequiredService<ILogger<ConfigurationRuntimeRefresh>>();
        return app.Use(async (context, next) =>
        {
            if (context.Request.Path.Equals("/healthz/live", StringComparison.OrdinalIgnoreCase))
            {
                await next(context).ConfigureAwait(false);
                return;
            }
            try
            {
                context.Items[RequestConfigurationGeneration.ContextKey] =
                    await refresh.RefreshAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                RefreshFailed(logger, exception);
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
    }
    [LoggerMessage(Level = LogLevel.Error, Message = "Shared authentication configuration could not be refreshed.")]
    private static partial void RefreshFailed(ILogger logger, Exception exception);
}
