// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Npgsql;

namespace Honua.Server.Features.Infrastructure.Configuration;

internal static class ConfigurationRuntimeMiddleware
{
    public static IApplicationBuilder UseSharedConfiguration(this IApplicationBuilder app)
    {
        if (app.ApplicationServices.GetService<NpgsqlDataSource>() is null) { return app; }
        var refresh = app.ApplicationServices.GetRequiredService<ConfigurationRuntimeRefresh>();
        var logger = app.ApplicationServices.GetRequiredService<ILogger<ConfigurationRuntimeRefresh>>();
        return app.Use(async (context, next) =>
        {
            try
            {
                context.Items[RequestConfigurationGeneration.ContextKey] =
                    await refresh.RefreshAsync(context.RequestAborted).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OperationCanceledException and not OutOfMemoryException)
            {
                logger.LogError(exception, "Shared authentication configuration could not be refreshed.");
                context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                return;
            }
            await next(context).ConfigureAwait(false);
        });
    }
}
