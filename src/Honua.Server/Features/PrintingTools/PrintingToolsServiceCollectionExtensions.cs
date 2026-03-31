// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Threading.Channels;
using Honua.Core.Features.Infrastructure.Abstractions;
using Honua.Server.Features.PrintingTools.Models;

namespace Honua.Server.Features.PrintingTools;

/// <summary>
/// Registers PrintingTools services.
/// </summary>
internal static class PrintingToolsServiceCollectionExtensions
{
    public static IServiceCollection AddPrintingTools(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        // Bounded channel for async print job queuing
        var channel = Channel.CreateBounded<PrintJob>(new BoundedChannelOptions(32)
        {
            SingleReader = true,
            FullMode = BoundedChannelFullMode.Wait
        });
        services.AddSingleton(channel);
        services.AddSingleton<PrintRenderConcurrencyGate>();
        services.AddSingleton<PrintJobCancellationTokens>();
        services.AddSingleton<IJobCancellationNotifier>(
            sp => sp.GetRequiredService<PrintJobCancellationTokens>());
        services.AddHostedService<PrintingToolsBackgroundService>();

        return services;
    }
}
