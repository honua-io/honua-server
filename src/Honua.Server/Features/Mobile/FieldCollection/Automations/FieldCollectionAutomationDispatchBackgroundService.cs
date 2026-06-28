// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Microsoft.Extensions.Options;

namespace Honua.Server.Features.Mobile.FieldCollection.Automations;

/// <summary>
/// Drains the in-process action queue and routes each invocation to the handler
/// registered for its action type, applying bounded retry for retryable failures
/// (#2121). Runs as a single-reader hosted service alongside the channel
/// dispatcher.
/// </summary>
internal sealed partial class FieldCollectionAutomationDispatchBackgroundService : BackgroundService
{
    private readonly ChannelFieldCollectionActionDispatcher _dispatcher;
    private readonly Dictionary<FieldCollectionAutomationActionType, IFieldCollectionActionHandler> _handlers;
    private readonly FieldCollectionAutomationOptions _options;
    private readonly ILogger<FieldCollectionAutomationDispatchBackgroundService> _logger;

    public FieldCollectionAutomationDispatchBackgroundService(
        ChannelFieldCollectionActionDispatcher dispatcher,
        IEnumerable<IFieldCollectionActionHandler> handlers,
        IOptions<FieldCollectionAutomationOptions> options,
        ILogger<FieldCollectionAutomationDispatchBackgroundService> logger)
    {
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        ArgumentNullException.ThrowIfNull(handlers);
        _options = options?.Value ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        var map = new Dictionary<FieldCollectionAutomationActionType, IFieldCollectionActionHandler>();
        foreach (var handler in handlers)
        {
            // Last registration wins so a deployment can override a default handler.
            map[handler.ActionType] = handler;
        }

        _handlers = map;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Enabled)
        {
            LogDisabled(_logger);
            return;
        }

        try
        {
            await foreach (var invocation in _dispatcher.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
            {
                await ProcessAsync(invocation, stoppingToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Graceful shutdown.
        }
    }

    private async Task ProcessAsync(FieldCollectionActionInvocation invocation, CancellationToken cancellationToken)
    {
        var actionType = invocation.Action.ActionType;
        if (!_handlers.TryGetValue(actionType, out var handler))
        {
            LogNoHandler(_logger, actionType, invocation.Action.Id);
            return;
        }

        var maxAttempts = Math.Max(1, _options.MaxAttempts);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            FieldCollectionActionResult result;
            try
            {
                result = await handler.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogHandlerThrew(_logger, actionType, invocation.InvocationId, attempt, ex);
                result = FieldCollectionActionResult.Failure("Action handler threw.", retryable: true);
            }

            if (result.Succeeded)
            {
                LogDelivered(_logger, actionType, invocation.InvocationId, attempt);
                return;
            }

            if (!result.Retryable || attempt == maxAttempts)
            {
                LogFailed(_logger, actionType, invocation.InvocationId, attempt, result.Error ?? "delivery failed");
                return;
            }

            try
            {
                await Task.Delay(ComputeBackoff(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
        }
    }

    private static TimeSpan ComputeBackoff(int attempt)
    {
        // Exponential backoff capped at 30s: 1s, 2s, 4s, ...
        var seconds = Math.Min(30d, Math.Pow(2, attempt - 1));
        return TimeSpan.FromSeconds(seconds);
    }

    [LoggerMessage(EventId = 21220, Level = LogLevel.Information, Message = "FieldCollection automation dispatcher is disabled.")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(EventId = 21221, Level = LogLevel.Debug, Message = "FieldCollection automation delivered. ActionType={ActionType} InvocationId={InvocationId} Attempt={Attempt}")]
    private static partial void LogDelivered(ILogger logger, FieldCollectionAutomationActionType actionType, string invocationId, int attempt);

    [LoggerMessage(EventId = 21222, Level = LogLevel.Warning, Message = "FieldCollection automation failed. ActionType={ActionType} InvocationId={InvocationId} Attempt={Attempt} Error={Error}")]
    private static partial void LogFailed(ILogger logger, FieldCollectionAutomationActionType actionType, string invocationId, int attempt, string error);

    [LoggerMessage(EventId = 21223, Level = LogLevel.Warning, Message = "FieldCollection automation handler threw. ActionType={ActionType} InvocationId={InvocationId} Attempt={Attempt}")]
    private static partial void LogHandlerThrew(ILogger logger, FieldCollectionAutomationActionType actionType, string invocationId, int attempt, Exception exception);

    [LoggerMessage(EventId = 21224, Level = LogLevel.Warning, Message = "No FieldCollection automation handler registered. ActionType={ActionType} ActionId={ActionId}")]
    private static partial void LogNoHandler(ILogger logger, FieldCollectionAutomationActionType actionType, string actionId);
}
