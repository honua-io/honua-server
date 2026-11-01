// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Core.Features.ControlPlane.Domain;
using Honua.Core.Features.Guardrails.Abstractions;
using Honua.Core.Features.Guardrails.Domain;

namespace Honua.ControlPlane.Executors;

/// <summary>
/// Stable identifiers for the control-plane self-healing ops actions. The action
/// vocabulary is dotted <c>domain.verb_noun</c>, aligned with the operational-workflow
/// catalog naming (honua-io/honua-devops#132).
/// </summary>
internal static class OpsActionNames
{
    /// <summary>Re-enqueues dead-lettered alert dispatch rows for redelivery.</summary>
    public const string RedriveDeadLetters = "alerts.redrive_dead_letters";

    /// <summary>Pauses delivery on an alert channel.</summary>
    public const string PauseChannel = "alerts.pause_channel";

    /// <summary>Resumes delivery on a paused alert channel.</summary>
    public const string ResumeChannel = "alerts.resume_channel";

    /// <summary>Transiently tunes the bounded database admission target.</summary>
    public const string TuneBoundedAdmission = "db.tune_bounded_admission";

    /// <summary>Restarts the registered Compose service and requires a completed readback.</summary>
    public const string ComposeRestartService = "compose.restart_service";

    /// <summary>
    /// Reserved Compose config change. Always refused: the executor does not apply arbitrary config.
    /// </summary>
    public const string ComposeApplyConfig = "compose.apply_config";
    /// <summary>Forces a new deployment of the registered ECS service. Does not change its task definition.</summary>
    public const string RestartEcs = "workload.restart_ecs";

    /// <summary>Republishes the registered Lambda alias's current image and moves the alias to that version.</summary>
    public const string RestartLambda = "workload.restart_lambda";

    /// <summary>Changes one allowlisted field on the registered deployment. Currently <c>ecs.desired_count</c>.</summary>
    public const string SetConfig = "workload.set_config";
}

/// <summary>
/// Single source of truth for the registered ops actions and their per-action
/// guardrail tiers. Consumed by <see cref="OpsActionGuardrailCatalog"/> (guardrail
/// resolution) and by the applier (dispatch and validation).
/// </summary>
internal static class OpsActionCatalog
{
    /// <summary>
    /// Declared metadata per action. The guardrail tier is applied as a floor on top of
    /// the edition guardrail policy (it can tighten but never loosen the edition tier);
    /// the auto-safe marker is consumed by the autonomy evaluator and never loosens a
    /// guardrail by itself.
    /// </summary>
    public static readonly ImmutableDictionary<string, OpsActionDescriptor> Actions =
        ImmutableDictionary.CreateRange(
            StringComparer.Ordinal,
            new KeyValuePair<string, OpsActionDescriptor>[]
            {
                new(OpsActionNames.RedriveDeadLetters, new OpsActionDescriptor(GuardrailTier.RequiresApproval, true)),
                new(OpsActionNames.PauseChannel, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.ResumeChannel, new OpsActionDescriptor(GuardrailTier.DirectExecute, false)),
                new(OpsActionNames.TuneBoundedAdmission, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.ComposeRestartService, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.ComposeApplyConfig, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.RestartEcs, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.RestartLambda, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
                new(OpsActionNames.SetConfig, new OpsActionDescriptor(GuardrailTier.RequiresApproval, false)),
            });

    /// <summary>Compatibility projection of registered ops-action guardrail tiers.</summary>
    public static readonly ImmutableDictionary<string, GuardrailTier> GuardrailTiers =
        Actions.ToImmutableDictionary(
            static action => action.Key,
            static action => action.Value.GuardrailTier,
            StringComparer.Ordinal);

    /// <summary>Returns true when the action name is a registered ops action.</summary>
    public static bool IsRegistered(string action) => Actions.ContainsKey(action);
}

/// <summary>
/// Registered ops-action metadata.
/// </summary>
internal sealed record OpsActionDescriptor(GuardrailTier GuardrailTier, bool IsAutoSafe);

/// <summary>
/// Per-action auto-safe metadata source backed by <see cref="OpsActionCatalog"/>.
/// </summary>
internal sealed class OpsActionSafetyCatalog : IOpsActionSafetyCatalog
{
    /// <inheritdoc />
    public bool IsAutoSafe(OperationClass operationClass, string? actionDiscriminator)
    {
        if (operationClass != OperationClass.AdminConfigChange ||
            string.IsNullOrWhiteSpace(actionDiscriminator))
        {
            return false;
        }

        return OpsActionCatalog.Actions.TryGetValue(actionDiscriminator, out var descriptor)
            && descriptor.IsAutoSafe;
    }
}

/// <summary>
/// Per-action guardrail tier source backed by <see cref="OpsActionCatalog"/>. Registered
/// so the guardrail ladder can resolve a per-action tier for an ops-action discriminator.
/// </summary>
internal sealed class OpsActionGuardrailCatalog : IOpsActionGuardrailCatalog
{
    /// <inheritdoc />
    public bool TryGetTier(string action, out GuardrailTier tier)
    {
        if (!string.IsNullOrWhiteSpace(action) &&
            OpsActionCatalog.Actions.TryGetValue(action, out var descriptor))
        {
            tier = descriptor.GuardrailTier;
            return true;
        }

        tier = GuardrailTier.Blocked;
        return false;
    }
}

/// <summary>
/// Thrown when an ops-action payload is malformed, names an unknown action, or carries
/// invalid parameters. The applier throws this <em>before</em> any side effect so the
/// gateway fails closed with a clean error and never partially applies an action.
/// </summary>
internal sealed class OpsActionException(string message) : Exception(message);

/// <summary>
/// Parsed <c>{action, target, params}</c> ops-action payload. Parsing extracts every
/// typed parameter the registered actions consume so no <see cref="JsonElement"/> outlives
/// the parsed document.
/// </summary>
internal sealed record OpsActionRequest
{
    /// <summary>Action discriminator (a registered <see cref="OpsActionNames"/> value).</summary>
    public required string Name { get; init; }

    /// <summary>Optional free-form action target.</summary>
    public string? Target { get; init; }

    /// <summary>Configured Compose service to restart.</summary>
    public string? Service { get; init; }

    /// <summary>Optional <c>maxCount</c> parameter (redrive batch bound).</summary>
    public int? MaxCount { get; init; }

    /// <summary>Optional <c>channel</c> parameter (pause/resume), falling back to <see cref="Target"/>.</summary>
    public string? Channel { get; init; }

    /// <summary>Optional <c>limit</c> parameter (bounded-admission tune target).</summary>
    public int? Limit { get; init; }

    /// <summary>Whether the payload carried any <c>params</c> value.</summary>
    public bool HasParameters { get; init; }
    /// <summary>Optional <c>field</c> parameter for <see cref="OpsActionNames.SetConfig"/>.</summary>
    public string? ConfigField { get; init; }

    /// <summary>Optional <c>value</c> parameter for <see cref="OpsActionNames.SetConfig"/>.</summary>
    public int? ConfigValue { get; init; }

    /// <summary>Parameter names present on the payload. Empty when <c>params</c> was omitted.</summary>
    public IReadOnlyList<string> ParameterNames { get; init; } = [];

    /// <summary>
    /// Parses and validates the payload structure. Throws <see cref="OpsActionException"/>
    /// for an empty, non-object, non-JSON, or action-less payload — the fail-closed path.
    /// </summary>
    public static OpsActionRequest Parse(string? payload)
    {
        if (string.IsNullOrWhiteSpace(payload))
        {
            throw new OpsActionException("The ops-action payload is empty.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException)
        {
            throw new OpsActionException("The ops-action payload is not valid JSON.");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                throw new OpsActionException("The ops-action payload must be a JSON object.");
            }

            if (!root.TryGetProperty("action", out var actionElement) ||
                actionElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(actionElement.GetString()))
            {
                throw new OpsActionException("The ops-action payload requires a non-empty 'action'.");
            }

            var name = actionElement.GetString()!.Trim();
            var target = root.TryGetProperty("target", out var targetElement) && targetElement.ValueKind == JsonValueKind.String
                ? targetElement.GetString()
                : null;

            int? maxCount = null;
            int? limit = null;
            string? channel = null;
            string? service = null;
            var hasParameters = false;
            string? configField = null;
            int? configValue = null;
            var parameterNames = new List<string>();
            if (root.TryGetProperty("params", out var paramsElement))
            {
                if (paramsElement.ValueKind == JsonValueKind.Object)
                {
                    foreach (var property in paramsElement.EnumerateObject())
                    {
                        parameterNames.Add(property.Name);
                    }
                    hasParameters = parameterNames.Count > 0;
                    maxCount = ReadInt(paramsElement, "maxCount");
                    limit = ReadInt(paramsElement, "limit");
                    channel = ReadString(paramsElement, "channel");
                    service = ReadString(paramsElement, "service");
                    configField = ReadString(paramsElement, "field");
                    configValue = ReadInt(paramsElement, "value");
                }
                else if (paramsElement.ValueKind is not JsonValueKind.Null and not JsonValueKind.Undefined)
                {
                    hasParameters = true;
                }
            }

            return new OpsActionRequest
            {
                Name = name,
                Service = service,
                Target = string.IsNullOrWhiteSpace(target) ? null : target,
                MaxCount = maxCount,
                Limit = limit,
                Channel = string.IsNullOrWhiteSpace(channel) ? (string.IsNullOrWhiteSpace(target) ? null : target) : channel,
                HasParameters = hasParameters,
                ConfigField = string.IsNullOrWhiteSpace(configField) ? null : configField,
                ConfigValue = configValue,
                ParameterNames = parameterNames,
            };
        }
    }

    private static int? ReadInt(JsonElement parent, string property)
    {
        if (!parent.TryGetProperty(property, out var element))
        {
            return null;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var number))
        {
            return number;
        }

        if (element.ValueKind == JsonValueKind.String &&
            int.TryParse(element.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
        {
            return parsed;
        }

        return null;
    }

    private static string? ReadString(JsonElement parent, string property)
    {
        return parent.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
    }
}

/// <summary>
/// Builds serialized <c>{action, target, params}</c> execution payloads for the registered
/// <see cref="OpsActionNames"/> vocabulary, mirroring exactly what <see cref="OpsActionRequest.Parse"/>
/// expects. Single-sourcing the wire shape here (next to the parser) keeps producers — for example the
/// deterministic ops-findings engine — from hand-rolling JSON that could drift from the parser's contract.
/// </summary>
internal static class OpsActionExecutionPayloads
{
    /// <summary>Builds the payload for <see cref="OpsActionNames.RedriveDeadLetters"/>.</summary>
    /// <param name="maxCount">Optional redrive batch bound.</param>
    public static string RedriveDeadLetters(int? maxCount = null)
        => Build(OpsActionNames.RedriveDeadLetters, target: null, maxCount: maxCount, limit: null, channel: null);

    /// <summary>Builds the approval-gated payload for <see cref="OpsActionNames.PauseChannel"/>.</summary>
    /// <param name="channel">Canonical external channel name.</param>
    public static string PauseChannel(string channel)
        => Build(OpsActionNames.PauseChannel, target: channel, maxCount: null, limit: null, channel: channel);

    /// <summary>Builds the payload for <see cref="OpsActionNames.TuneBoundedAdmission"/>.</summary>
    /// <param name="limit">The requested admission target.</param>
    public static string TuneBoundedAdmission(int limit)
        => Build(OpsActionNames.TuneBoundedAdmission, target: null, maxCount: null, limit: limit, channel: null);

    /// <summary>Builds a target-bound, approval-required managed Compose restart.</summary>
    public static string RestartComposeService(string targetId, string service)
        => JsonSerializer.Serialize(new OpsActionExecutionPayload
        {
            Action = OpsActionNames.ComposeRestartService,
            Target = targetId,
            Params = new OpsActionExecutionParams { Service = service }
        }, OpsActionExecutionPayloadJsonContext.Default.OpsActionExecutionPayload);

    private static string Build(string action, string? target, int? maxCount, int? limit, string? channel)
    {
        var parameters = maxCount is null && limit is null && channel is null
            ? null
            : new OpsActionExecutionParams { MaxCount = maxCount, Limit = limit, Channel = channel };

        var payload = new OpsActionExecutionPayload
        {
            Action = action,
            Target = target,
            Params = parameters,
        };

        return JsonSerializer.Serialize(payload, OpsActionExecutionPayloadJsonContext.Default.OpsActionExecutionPayload);
    }
}

/// <summary>Wire shape for a serialized ops-action execution payload (see <see cref="OpsActionRequest.Parse"/>).</summary>
internal sealed record OpsActionExecutionPayload
{
    /// <summary>Action discriminator (a registered <see cref="OpsActionNames"/> value).</summary>
    public required string Action { get; init; }

    /// <summary>Optional free-form action target.</summary>
    public string? Target { get; init; }

    /// <summary>Optional action parameters.</summary>
    public OpsActionExecutionParams? Params { get; init; }
}

/// <summary>Optional parameters carried by an ops-action execution payload.</summary>
internal sealed record OpsActionExecutionParams
{
    /// <summary>Configured managed Compose service.</summary>
    public string? Service { get; init; }

    /// <summary>Redrive batch bound.</summary>
    public int? MaxCount { get; init; }

    /// <summary>Bounded-admission tune target.</summary>
    public int? Limit { get; init; }

    /// <summary>Pause/resume channel name.</summary>
    public string? Channel { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpsActionExecutionPayload))]
internal sealed partial class OpsActionExecutionPayloadJsonContext : JsonSerializerContext
{
}
