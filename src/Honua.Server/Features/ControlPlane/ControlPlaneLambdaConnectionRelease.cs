// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using Honua.Core.Features.Infrastructure.Abstractions;

namespace Honua.ControlPlane;

/// <summary>
/// Decides whether this host releases its pooled database connections at the end of every
/// request, which is what an AWS Lambda control-plane event handler (reconcile, backstop,
/// scheduled tick) must do. Each such invocation can cold-start a new Lambda instance with its
/// own Npgsql pools, and a frozen instance runs nothing — pool pruning included — so its idle
/// connections stay open until RDS runs out of slots (honua-release e2e-cloud-aws run
/// 38125145738: 14 <c>53300</c> rejections and 3 aborted PostGIS preflights in the tick log).
/// </summary>
internal static class ControlPlaneLambdaConnectionRelease
{
    /// <summary>
    /// Explicit switch. When absent the default follows the deployment profile: on for an
    /// event-handler Lambda (<see cref="HandlerSelectorKey"/> is set), off for every other host,
    /// including the API Lambda, where keeping the pool warm between requests is right.
    /// </summary>
    internal const string OptionKey = "ControlPlane:Lambda:ReleaseConnectionsAfterInvocation";

    /// <summary>
    /// Environment variable that selects the control-plane event handler
    /// (<c>batch-event</c> | <c>backstop</c> | <c>scheduled-tick</c>). honua-iac sets it on the
    /// three event-handler functions only, never on the API function.
    /// </summary>
    internal const string HandlerSelectorKey = "HONUA_CONTROL_PLANE_LAMBDA_HANDLER";

    /// <summary>Resolves the effective setting from configuration.</summary>
    /// <param name="configuration">Application configuration (environment variables included).</param>
    /// <returns><see langword="true"/> when every request should end by releasing pooled connections.</returns>
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

        return !string.IsNullOrWhiteSpace(configuration[HandlerSelectorKey]);
    }

    /// <summary>
    /// Adds <see cref="ControlPlaneLambdaConnectionReleaseMiddleware"/> when
    /// <see cref="IsEnabled"/> holds; otherwise leaves the pipeline untouched. Register it ahead
    /// of host validation so a request that is rejected early still releases.
    /// </summary>
    /// <param name="app">Application builder.</param>
    /// <param name="configuration">Application configuration.</param>
    /// <returns>The application builder.</returns>
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

/// <summary>
/// Runs the rest of the pipeline, then — whether it succeeded or threw — releases every
/// provider's pooled database connections, so the Lambda instance holds none once it is frozen.
/// Release runs before the response completes, while the invocation is still live.
/// </summary>
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
