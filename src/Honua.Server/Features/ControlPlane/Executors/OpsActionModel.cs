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

internal static class OpsActionNames
{
    public const string RedriveDeadLetters = "alerts.redrive_dead_letters";

    public const string PauseChannel = "alerts.pause_channel";

    public const string ResumeChannel = "alerts.resume_channel";

    public const string TuneBoundedAdmission = "db.tune_bounded_admission";

    public const string ComposeRestartService = "compose.restart_service";

    public const string ComposeApplyConfig = "compose.apply_config";
    public const string RestartEcs = "workload.restart_ecs";

    public const string RestartLambda = "workload.restart_lambda";

    public const string SetConfig = "workload.set_config";
}

internal static class OpsActionCatalog
{
    // The guardrail tier can tighten the edition floor and can never loosen it, and the auto-safe marker never loosens a guardrail by itself.
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

    public static readonly ImmutableDictionary<string, GuardrailTier> GuardrailTiers =
        Actions.ToImmutableDictionary(
            static action => action.Key,
            static action => action.Value.GuardrailTier,
            StringComparer.Ordinal);

    public static bool IsRegistered(string action) => Actions.ContainsKey(action);
}

internal sealed record OpsActionDescriptor(GuardrailTier GuardrailTier, bool IsAutoSafe);

internal sealed class OpsActionSafetyCatalog : IOpsActionSafetyCatalog
{
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

internal sealed class OpsActionGuardrailCatalog : IOpsActionGuardrailCatalog
{
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

// The applier throws this before any side effect so a bad payload never partially applies.
internal sealed class OpsActionException(string message) : Exception(message);

internal sealed record OpsActionRequest
{
    public required string Name { get; init; }

    public string? Target { get; init; }

    public string? Service { get; init; }

    public int? MaxCount { get; init; }

    public string? Channel { get; init; }

    public int? Limit { get; init; }

    public bool HasParameters { get; init; }
    public string? ConfigField { get; init; }

    public int? ConfigValue { get; init; }

    public IReadOnlyList<string> ParameterNames { get; init; } = [];

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

internal static class OpsActionExecutionPayloads
{
    public static string RedriveDeadLetters(int? maxCount = null)
        => Build(OpsActionNames.RedriveDeadLetters, target: null, maxCount: maxCount, limit: null, channel: null);

    public static string PauseChannel(string channel)
        => Build(OpsActionNames.PauseChannel, target: channel, maxCount: null, limit: null, channel: channel);

    public static string TuneBoundedAdmission(int limit)
        => Build(OpsActionNames.TuneBoundedAdmission, target: null, maxCount: null, limit: limit, channel: null);

    public static string RestartComposeService(string targetId, string service)
        => Build(OpsActionNames.ComposeRestartService, target: targetId, maxCount: null, limit: null, channel: null, service: service);

    private static string Build(string action, string? target, int? maxCount, int? limit, string? channel, string? service = null)
    {
        var parameters = maxCount is null && limit is null && channel is null && service is null
            ? null
            : new OpsActionExecutionParams { MaxCount = maxCount, Limit = limit, Channel = channel, Service = service };

        var payload = new OpsActionExecutionPayload
        {
            Action = action,
            Target = target,
            Params = parameters,
        };

        return JsonSerializer.Serialize(payload, OpsActionExecutionPayloadJsonContext.Default.OpsActionExecutionPayload);
    }
}

internal sealed record OpsActionExecutionPayload
{
    public required string Action { get; init; }

    public string? Target { get; init; }

    public OpsActionExecutionParams? Params { get; init; }
}

internal sealed record OpsActionExecutionParams
{
    public string? Service { get; init; }

    public int? MaxCount { get; init; }

    public int? Limit { get; init; }

    public string? Channel { get; init; }
}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(OpsActionExecutionPayload))]
internal sealed partial class OpsActionExecutionPayloadJsonContext : JsonSerializerContext
{
}
