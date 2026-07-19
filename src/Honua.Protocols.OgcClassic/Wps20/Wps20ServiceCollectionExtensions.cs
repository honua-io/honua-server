// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Protocols.Ogc.Classic.Wps20;

internal static class Wps20ServiceCollectionExtensions
{
    internal static IServiceCollection AddWps20(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<Wps20Options>(configuration.GetSection(Wps20Options.SectionName));
        services.PostConfigure<Wps20Options>(options =>
        {
            if (bool.TryParse(configuration["HONUA_WPS_CITE_ECHO_PROCESS_ENABLED"], out var enabled))
            {
                options.EnableConformanceEcho = enabled;
            }
            var processId = configuration["HONUA_CITE_WPS20_ECHO_PROCESS_ID"];
            if (!string.IsNullOrWhiteSpace(processId))
            {
                options.ConformanceEchoProcessId = processId.Trim();
            }
        });
        services.AddHttpClient(Wps20ConformanceEcho.ReferenceClientName, client =>
        {
            client.Timeout = TimeSpan.FromSeconds(10);
        }).ConfigurePrimaryHttpMessageHandler(static () => new HttpClientHandler
        {
            AllowAutoRedirect = false
        });
        services.AddSingleton<Wps20ConformanceEcho>();
        return services;
    }
}
