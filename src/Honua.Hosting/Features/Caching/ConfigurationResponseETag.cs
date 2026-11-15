// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Metadata.Abstractions;

namespace Honua.Infrastructure.Caching;

internal static class ConfigurationResponseETag
{
    internal static IConfigurationGenerationSource? GetSource(HttpContext context)
    {
        IServiceProvider? services;
        try
        {
            services = context.RequestServices;
        }
        catch (InvalidOperationException)
        {
            return null;
        }

        return services?.GetService<IConfigurationGenerationSource>();
    }

    internal static async ValueTask<string> ForResponseAsync(HttpContext context, string etag)
    {
        var source = GetSource(context);
        if (source is null)
        {
            return etag;
        }

        var generation = await source.GetGenerationAsync(context.RequestAborted).ConfigureAwait(false);
        return string.Concat(etag.AsSpan(0, etag.Length - 1),
            "-g", generation.ToString(CultureInfo.InvariantCulture), "\"");
    }
}
