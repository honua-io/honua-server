// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.ControlPlane.Abstractions;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.ControlPlane;

// A frozen instance does not prune its pool, so an event-handler host releases pooled database connections at the end of every request.
internal static class ControlPlaneLambdaConnectionRelease
{
    internal const string OptionKey = "ControlPlane:Lambda:ReleaseConnectionsAfterInvocation";

    // This variable is set only on the event-handler functions, never on the API function.
    internal const string HandlerSelectorKey = ControlPlaneEventHandlerHost.HandlerEnvironmentVariable;

    public static bool IsEnabled(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var configured = configuration[OptionKey];
        if (!string.IsNullOrWhiteSpace(configured))
        {
            if (bool.TryParse(configured.Trim(), out var explicitValue))
            {
                return explicitValue;
            }

            throw new InvalidOperationException(string.Format(
                CultureInfo.InvariantCulture,
                "{0} must be 'true' or 'false' (was '{1}').",
                OptionKey,
                configured));
        }

        return IsEventHandlerProfile(configuration);
    }

    public static bool IsEventHandlerProfile(IConfiguration configuration)
        => ControlPlaneEventHandlerHost.IsEventHandler(configuration);

    public static bool HostsDatabasePollingLoops(IConfiguration configuration)
        => !IsEventHandlerProfile(configuration);

    public static IApplicationBuilder UseControlPlaneLambdaConnectionRelease(
        this IApplicationBuilder app,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(app);
        return IsEnabled(configuration)
            ? app.UseMiddleware<ControlPlaneLambdaConnectionReleaseMiddleware>()
            : app;
    }
}

internal sealed partial class ControlPlaneLambdaConnectionReleaseMiddleware
{
    private readonly RequestDelegate _next;
    private readonly IDatabaseConnectionReleaser[] _releasers;
    private readonly ILogger<ControlPlaneLambdaConnectionReleaseMiddleware> _logger;

    public ControlPlaneLambdaConnectionReleaseMiddleware(
        RequestDelegate next,
        IEnumerable<IDatabaseConnectionReleaser> releasers,
        ILogger<ControlPlaneLambdaConnectionReleaseMiddleware> logger)
    {
        _next = next ?? throw new ArgumentNullException(nameof(next));
        ArgumentNullException.ThrowIfNull(releasers);
        _releasers = releasers.ToArray();
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await _next(context).ConfigureAwait(false);
        }
        finally
        {
            await ReleaseAsync().ConfigureAwait(false);
        }
    }

    private async Task ReleaseAsync()
    {
        foreach (var releaser in _releasers)
        {
            try
            {
                // Not the request's token: an aborted invocation must still release.
                await releaser.ReleasePooledConnectionsAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // A failed release must not replace the invocation's own outcome; the next
                // invocation tries again.
                LogReleaseFailed(_logger, releaser.GetType().Name, ex);
            }
        }
    }

    [LoggerMessage(19621, LogLevel.Warning,
        "Releasing pooled database connections after a control-plane invocation failed ({Releaser})")]
    private static partial void LogReleaseFailed(ILogger logger, string releaser, Exception exception);
}
