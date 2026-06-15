// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Reflection;
using Honua.Core.Features.AuditLog.Abstractions;
using Honua.Core.Features.Licensing.Abstractions;
using Honua.Core.Features.Licensing.Domain;
using Honua.Plugins.Abstractions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Honua.Plugins;

/// <summary>
/// Default <see cref="IPluginEditPipeline"/>. Aggregates the registered
/// <see cref="IFeatureValidator"/>s and <see cref="IEditHook"/>s, runs them in deterministic
/// plugin-id order, and gates the whole thing behind the Enterprise <c>plugin.sdk</c> entitlement
/// plus the operator kill-switch. When unlicensed/disabled it allows every edit and does no work.
/// </summary>
internal sealed partial class PluginEditPipeline : IPluginEditPipeline
{
    private const string ValidateExtensionPoint = "validate";
    private const string FieldValidateExtensionPoint = "validate-field";
    private const string BeforeHookExtensionPoint = "before-hook";
    private const string AfterHookExtensionPoint = "after-hook";

    private readonly IFeatureValidator[] _validators;
    private readonly IFieldValidator[] _fieldValidators;
    private readonly IEditHook[] _hooks;
    private readonly ILicenseEntitlementService _licensing;
    private readonly IAuditLog _auditLog;
    private readonly ILogger<PluginEditPipeline> _logger;
    private readonly bool _enabledByConfig;
    private readonly PluginCircuitBreaker _afterHookBreaker = new();
    private int _unlicensedLogged;

    public PluginEditPipeline(
        IEnumerable<IFeatureValidator> validators,
        IEnumerable<IFieldValidator> fieldValidators,
        IEnumerable<IEditHook> hooks,
        ILicenseEntitlementService licensing,
        IAuditLog auditLog,
        IOptions<PluginOptions> options,
        ILogger<PluginEditPipeline> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _validators = OrderByPluginId(validators);
        _fieldValidators = OrderByPluginId(fieldValidators);
        _hooks = OrderByPluginId(hooks);
        _licensing = licensing ?? throw new ArgumentNullException(nameof(licensing));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _enabledByConfig = options.Value.Enabled;
    }

    /// <inheritdoc />
    public bool HasPlugins => _enabledByConfig
        && (_validators.Length > 0 || _fieldValidators.Length > 0 || _hooks.Length > 0);

    /// <inheritdoc />
    public async ValueTask<PluginEditOutcome> ValidateAndRunBeforeHooksAsync(
        EditHookContext context,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HasPlugins || !IsEntitled())
        {
            return PluginEditOutcome.Allowed;
        }

        var rejections = ImmutableArray.CreateBuilder<PluginEditRejection>();

        // Per-feature validators run over incoming features (creates/updates). Deletes carry no
        // inbound attributes for field-level rules, so validators do not see them.
        foreach (var item in context.Features)
        {
            if (item.Kind == EditKind.Delete || (_validators.Length == 0 && _fieldValidators.Length == 0))
            {
                continue;
            }

            var validationContext = new PluginValidationContext(
                context.ServiceId,
                context.LayerId,
                context.ResourceName,
                item.Kind,
                item.ObjectId,
                context.Actor);

            var rejected = false;
            foreach (var validator in _validators)
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var measure = PluginMetrics.Measure(PluginIdOf(validator), ValidateExtensionPoint);
                PluginValidationResult result;
                try
                {
                    result = await validator.ValidateAsync(item.Feature, validationContext, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch
                {
                    measure.MarkFailed();
                    throw;
                }

                if (!result.IsValid)
                {
                    rejections.Add(new PluginEditRejection(
                        item.Kind, item.RequestIndex, item.ObjectId, result.ErrorCode, result.ErrorMessage!));
                    await AuditRejectionAsync(context, item.ObjectId, result.ErrorMessage!, cancellationToken)
                        .ConfigureAwait(false);
                    rejected = true;
                    break; // short-circuit remaining validators for this feature
                }
            }

            // Field-level validators run after feature-level rules pass. Each only fires for the
            // attribute it governs; the first field rejection short-circuits the rest for this feature.
            if (!rejected)
            {
                foreach (var fieldValidator in _fieldValidators)
                {
                    if (!TryGetAttribute(item.Feature, fieldValidator.FieldName, out var value))
                    {
                        continue;
                    }

                    cancellationToken.ThrowIfCancellationRequested();
                    using var measure = PluginMetrics.Measure(PluginIdOf(fieldValidator), FieldValidateExtensionPoint);
                    PluginValidationResult result;
                    try
                    {
                        result = await fieldValidator
                            .ValidateFieldAsync(value, item.Feature, validationContext, cancellationToken)
                            .ConfigureAwait(false);
                    }
                    catch
                    {
                        measure.MarkFailed();
                        throw;
                    }

                    if (!result.IsValid)
                    {
                        rejections.Add(new PluginEditRejection(
                            item.Kind, item.RequestIndex, item.ObjectId, result.ErrorCode, result.ErrorMessage!));
                        await AuditRejectionAsync(context, item.ObjectId, result.ErrorMessage!, cancellationToken)
                            .ConfigureAwait(false);
                        break;
                    }
                }
            }
        }

        // Batch-level before hooks. A rejection here supersedes per-feature results and rejects
        // every feature in the batch.
        foreach (var hook in _hooks)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EditHookResult hookResult;
            using (var measure = PluginMetrics.Measure(PluginIdOf(hook), BeforeHookExtensionPoint))
            {
                try
                {
                    hookResult = await hook.OnBeforeEditAsync(context, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    measure.MarkFailed();
                    throw;
                }
            }

            if (hookResult.IsRejected)
            {
                await AuditRejectionAsync(context, objectId: null, hookResult.Reason!, cancellationToken)
                    .ConfigureAwait(false);
                var batch = ImmutableArray.CreateBuilder<PluginEditRejection>(context.Features.Length);
                foreach (var item in context.Features)
                {
                    batch.Add(new PluginEditRejection(
                        item.Kind, item.RequestIndex, item.ObjectId, hookResult.ErrorCode, hookResult.Reason!));
                }

                return new PluginEditOutcome(batch.ToImmutable(), BatchRejected: true);
            }
        }

        return rejections.Count == 0
            ? PluginEditOutcome.Allowed
            : new PluginEditOutcome(rejections.ToImmutable(), BatchRejected: false);
    }

    /// <inheritdoc />
    public async ValueTask RunAfterHooksAsync(EditHookContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);
        if (!HasPlugins || _hooks.Length == 0 || !IsEntitled())
        {
            return;
        }

        foreach (var hook in _hooks)
        {
            var pluginId = PluginIdOf(hook);

            // Skip after-hooks whose breaker is tripped open: a repeatedly-faulting after-hook is
            // parked (auto-disabled) until the cooldown allows a half-open retry, so a misbehaving
            // hook stops being invoked on every committed edit.
            if (!_afterHookBreaker.IsAllowed(pluginId))
            {
                continue;
            }

            using var measure = PluginMetrics.Measure(pluginId, AfterHookExtensionPoint);
            try
            {
                await hook.OnAfterEditAsync(context, cancellationToken).ConfigureAwait(false);
                if (_afterHookBreaker.RecordSuccess(pluginId))
                {
                    Log.AfterHookRecovered(_logger, pluginId);
                    await PluginExecutionAudit
                        .RecordRecoveredAsync(_auditLog, pluginId, AfterHookExtensionPoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // After-hooks are best-effort: the edit is already committed. Never propagate.
                measure.MarkFailed();
                Log.AfterHookFailed(_logger, pluginId, context.ServiceId, context.LayerId, ex);
                if (_afterHookBreaker.RecordFailure(pluginId))
                {
                    Log.AfterHookAutoDisabled(_logger, pluginId);
                    await PluginExecutionAudit
                        .RecordAutoDisabledAsync(_auditLog, pluginId, AfterHookExtensionPoint, cancellationToken)
                        .ConfigureAwait(false);
                }
            }
        }
    }

    private bool IsEntitled()
    {
        if (_licensing.CheckEntitlement(FeatureCatalog.PluginSdkKey).IsActive)
        {
            return true;
        }

        if (Interlocked.Exchange(ref _unlicensedLogged, 1) == 0)
        {
            Log.PluginsUnlicensed(_logger, _validators.Length + _fieldValidators.Length + _hooks.Length);
        }

        return false;
    }

    private async Task AuditRejectionAsync(
        EditHookContext context,
        long? objectId,
        string reason,
        CancellationToken cancellationToken)
    {
        var actor = context.Actor;
        var auditEvent = new AuditEvent
        {
            Timestamp = DateTimeOffset.UtcNow,
            EventType = AuditEventType.Authorization,
            Actor = string.IsNullOrWhiteSpace(actor) ? AuditEvent.AnonymousActor : actor,
            ActorType = string.IsNullOrWhiteSpace(actor) ? AuditActorType.Anonymous : AuditActorType.UserId,
            ResourceType = "layer",
            ResourceId = objectId?.ToString(System.Globalization.CultureInfo.InvariantCulture)
                ?? context.LayerId.ToString(System.Globalization.CultureInfo.InvariantCulture),
            Action = "plugin.edit.reject",
            Outcome = AuditOutcome.Denied,
            CorrelationId = string.IsNullOrWhiteSpace(context.CorrelationId) ? "-" : context.CorrelationId,
            Details = reason,
        };

        await _auditLog.RecordAsync(auditEvent, cancellationToken).ConfigureAwait(false);
    }

    private static bool TryGetAttribute(
        Honua.Core.Features.FeatureStore.Domain.Feature feature,
        string fieldName,
        out object? value)
    {
        if (feature.Attributes.TryGetValue(fieldName, out value))
        {
            return true;
        }

        // Fall back to a case-insensitive scan to honor the case-insensitive field-name contract
        // even when the attribute bag is keyed ordinally.
        foreach (var pair in feature.Attributes)
        {
            if (string.Equals(pair.Key, fieldName, StringComparison.OrdinalIgnoreCase))
            {
                value = pair.Value;
                return true;
            }
        }

        value = null;
        return false;
    }

    private static T[] OrderByPluginId<T>(IEnumerable<T> instances)
        where T : class
        => [.. instances.OrderBy(PluginIdOf, StringComparer.Ordinal)];

    private static string PluginIdOf(object instance)
    {
        var type = instance.GetType();
        return type.GetCustomAttribute<PluginAttribute>()?.Id ?? type.FullName ?? type.Name;
    }

    private static partial class Log
    {
        [LoggerMessage(EventId = 9300, Level = LogLevel.Warning,
            Message = "Plugin SDK is not licensed (Enterprise required); {PluginCount} registered plugin extension point(s) are inactive.")]
        public static partial void PluginsUnlicensed(ILogger logger, int pluginCount);

        [LoggerMessage(EventId = 9301, Level = LogLevel.Error,
            Message = "Plugin after-edit hook '{PluginId}' failed for service {ServiceId} layer {LayerId}; edit already committed.")]
        public static partial void AfterHookFailed(ILogger logger, string pluginId, string serviceId, int layerId, Exception exception);

        [LoggerMessage(EventId = 9302, Level = LogLevel.Warning,
            Message = "Plugin after-edit hook '{PluginId}' auto-disabled after repeated failures; it will be retried after a cooldown.")]
        public static partial void AfterHookAutoDisabled(ILogger logger, string pluginId);

        [LoggerMessage(EventId = 9303, Level = LogLevel.Information,
            Message = "Plugin after-edit hook '{PluginId}' recovered and was re-enabled.")]
        public static partial void AfterHookRecovered(ILogger logger, string pluginId);
    }
}
