// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Honua.Core.Features.Mobile.FieldCollection.Abstractions;
using Honua.Core.Features.Mobile.FieldCollection.Domain;
using Microsoft.Extensions.Logging;

namespace Honua.Core.Features.Mobile.FieldCollection;

/// <summary>
/// Default <see cref="IFieldCollectionAutomationTrigger"/>: loads enabled actions
/// for the applied change's layer, matches them, and enqueues each match onto the
/// dispatcher (#2121). Best-effort by design — store or dispatch failures are
/// logged and swallowed so the originating mobile push always succeeds.
/// </summary>
public sealed partial class FieldCollectionAutomationTrigger : IFieldCollectionAutomationTrigger
{
    private readonly IFieldCollectionAutomationStore _store;
    private readonly IFieldCollectionActionDispatcher _dispatcher;
    private readonly ILogger<FieldCollectionAutomationTrigger> _logger;

    /// <summary>
    /// Initializes a new instance of the <see cref="FieldCollectionAutomationTrigger"/> class.
    /// </summary>
    public FieldCollectionAutomationTrigger(
        IFieldCollectionAutomationStore store,
        IFieldCollectionActionDispatcher dispatcher,
        ILogger<FieldCollectionAutomationTrigger> logger)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _dispatcher = dispatcher ?? throw new ArgumentNullException(nameof(dispatcher));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <inheritdoc />
    public async ValueTask OnChangeAppliedAsync(
        FieldCollectionAutomationEvent automationEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(automationEvent);

        IReadOnlyList<FieldCollectionAutomationAction> actions;
        try
        {
            actions = await _store
                .GetEnabledActionsAsync(automationEvent.LayerId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        // Intentionally broad: automation-action lookup is best-effort (see class doc) and must
        // never let an already-applied mobile push fail because of a store error.
        catch (Exception ex)
        {
            // Never let automation evaluation fail an already-applied push.
            LogEvaluationFailed(_logger, automationEvent.LayerId, automationEvent.ChangeId, ex);
            return;
        }

        if (actions.Count == 0)
        {
            return;
        }

        var invocations = FieldCollectionAutomationMatcher.Match(actions, automationEvent);
        if (invocations.Count == 0)
        {
            return;
        }

        var enqueued = 0;
        foreach (var invocation in invocations)
        {
            try
            {
                await _dispatcher.EnqueueAsync(invocation, cancellationToken).ConfigureAwait(false);
                enqueued++;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            // Intentionally broad: one invocation's dispatcher failure must not abort the
            // remaining invocations or fail the already-applied mobile push.
            catch (Exception ex)
            {
                LogEnqueueFailed(_logger, invocation.Action.Id, invocation.InvocationId, ex);
            }
        }

        LogActionsTriggered(_logger, automationEvent.LayerId, automationEvent.ChangeId, enqueued);
    }

    [LoggerMessage(
        EventId = 21210,
        Level = LogLevel.Information,
        Message = "FieldCollection automation triggered. LayerId={LayerId} ChangeId={ChangeId} Enqueued={Enqueued}")]
    private static partial void LogActionsTriggered(ILogger logger, int layerId, string changeId, int enqueued);

    [LoggerMessage(
        EventId = 21211,
        Level = LogLevel.Warning,
        Message = "FieldCollection automation evaluation failed. LayerId={LayerId} ChangeId={ChangeId}")]
    private static partial void LogEvaluationFailed(ILogger logger, int layerId, string changeId, Exception exception);

    [LoggerMessage(
        EventId = 21212,
        Level = LogLevel.Warning,
        Message = "FieldCollection automation enqueue failed. ActionId={ActionId} InvocationId={InvocationId}")]
    private static partial void LogEnqueueFailed(ILogger logger, string actionId, string invocationId, Exception exception);
}
