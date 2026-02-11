// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
using Honua.Core.Features.Validation.Abstractions;
using Honua.Server.Features.FeatureServer.Models;
using Honua.Server.Features.Infrastructure.Models;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Primitives;

namespace Honua.Server.Features.FeatureServer;

internal static partial class FeatureServerEndpoints
{
    private static async Task<IResult> HandleApplyEdits(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limitsOptions)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (request, readError) = await TryReadApplyEditsRequestAsync(context.Request, cancellationToken);
        if (request == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid applyEdits request",
                [readError ?? "Invalid request body."]);
        }

        return await ExecuteEditsRequestAsync(
            serviceId,
            layerId,
            context,
            editsHandler,
            limitsOptions.Value.Edits,
            request,
            "applyEdits");
    }

    private static async Task<IResult> HandleAddFeatures(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limitsOptions)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (request, readError) = await TryReadFeatureArrayRequestAsync(
            context.Request,
            primaryKey: "features",
            fallbackKey: "adds",
            assignToAdds: true,
            cancellationToken);
        if (request == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid addFeatures request",
                [readError ?? "Invalid request body."]);
        }

        if (request.Adds == null || request.Adds.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid addFeatures request",
                ["features parameter is required"]);
        }

        return await ExecuteEditsRequestAsync(
            serviceId,
            layerId,
            context,
            editsHandler,
            limitsOptions.Value.Edits,
            request,
            "addFeatures");
    }

    private static async Task<IResult> HandleUpdateFeatures(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limitsOptions)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (request, readError) = await TryReadFeatureArrayRequestAsync(
            context.Request,
            primaryKey: "features",
            fallbackKey: "updates",
            assignToAdds: false,
            cancellationToken);
        if (request == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid updateFeatures request",
                [readError ?? "Invalid request body."]);
        }

        if (request.Updates == null || request.Updates.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid updateFeatures request",
                ["features parameter is required"]);
        }

        return await ExecuteEditsRequestAsync(
            serviceId,
            layerId,
            context,
            editsHandler,
            limitsOptions.Value.Edits,
            request,
            "updateFeatures");
    }

    private static async Task<IResult> HandleDeleteFeatures(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        IOptions<LimitsOptions> limitsOptions)
    {
        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        var (request, readError) = await TryReadDeleteFeaturesRequestAsync(context.Request, cancellationToken);
        if (request == null)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid deleteFeatures request",
                [readError ?? "Invalid request body."]);
        }

        if (request.Deletes == null || request.Deletes.Length == 0)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid deleteFeatures request",
                ["objectIds parameter is required"]);
        }

        return await ExecuteEditsRequestAsync(
            serviceId,
            layerId,
            context,
            editsHandler,
            limitsOptions.Value.Edits,
            request,
            "deleteFeatures");
    }

    private static async Task<IResult> ExecuteEditsRequestAsync(
        string serviceId,
        int layerId,
        HttpContext context,
        FeatureServerEditsHandler editsHandler,
        Honua.Core.Configuration.EditLimits editLimits,
        ApplyEditsRequest request,
        string operationName)
    {
        var queryValidator = context.RequestServices.GetRequiredService<ICommonQueryValidator>();
        if (!TryValidateAllowedParameters(context.Request.Query, queryValidator, AllowedQueryParameters.ApplyEdits, out var parameterError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "Invalid query parameters",
                [parameterError ?? "Invalid query parameter."]);
        }

        if (!TryApplyEditOptionsFromQuery(request, context.Request.Query, out var queryError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid {operationName} request",
                [queryError ?? "Invalid query parameters."]);
        }

        if (!TryValidateOutputFormat(request.F, JsonOnlyFormats, out var normalizedFormat, out var formatError))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                $"Invalid {operationName} request",
                [formatError ?? "Output format is not supported."]);
        }

        request.F = normalizedFormat;

        if (request.UseGlobalIds)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "useGlobalIds is not supported",
                ["Set useGlobalIds to false and supply objectIds in attributes."]);
        }

        if (request.ReturnEditMoment)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "returnEditMoment is not supported");
        }

        if (!string.IsNullOrWhiteSpace(request.GdbVersion))
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "gdbVersion is not supported");
        }

        if (request.Attachments is { Length: > 0 })
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "attachments edits are not supported");
        }

        var cancellationToken = GetTimeoutAwareCancellationToken(context);
        return await editsHandler.HandleApplyEditsAsync(
            serviceId,
            layerId,
            request,
            editLimits,
            cancellationToken);
    }

    private static bool TryApplyEditOptionsFromQuery(
        ApplyEditsRequest request,
        IQueryCollection query,
        out string? error)
    {
        error = null;
        if (query.Count == 0)
        {
            return true;
        }

        var values = ToCaseInsensitiveDictionary(query);

        if (!TryParseBoolValue(values, "rollbackOnFailure", request.RollbackOnFailure, out var rollbackOnFailure, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "useGlobalIds", request.UseGlobalIds, out var useGlobalIds, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnEditMoment", request.ReturnEditMoment, out var returnEditMoment, out error))
        {
            return false;
        }

        request.RollbackOnFailure = rollbackOnFailure;
        request.UseGlobalIds = useGlobalIds;
        request.ReturnEditMoment = returnEditMoment;

        var f = GetValueString(values, "f");
        if (!string.IsNullOrWhiteSpace(f))
        {
            request.F = f;
        }

        var gdbVersion = GetValueString(values, "gdbVersion");
        if (!string.IsNullOrWhiteSpace(gdbVersion))
        {
            request.GdbVersion = gdbVersion;
        }

        return true;
    }

    private static async Task<(ApplyEditsRequest? Request, string? Error)> TryReadApplyEditsRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return TryParseApplyEditsRequest(values);
        }

        if (request.ContentLength is 0)
        {
            return (new ApplyEditsRequest(), null);
        }

        try
        {
            var parsed = await JsonSerializer.DeserializeAsync(
                request.Body,
                FeatureServerJsonContext.Default.ApplyEditsRequest,
                cancellationToken);
            if (parsed == null)
            {
                return (null, "Invalid JSON payload.");
            }

            return (parsed, null);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static async Task<(ApplyEditsRequest? Request, string? Error)> TryReadFeatureArrayRequestAsync(
        HttpRequest request,
        string primaryKey,
        string fallbackKey,
        bool assignToAdds,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return TryParseFeatureArrayRequestFromValues(values, primaryKey, fallbackKey, assignToAdds);
        }

        if (request.ContentLength is 0)
        {
            return (new ApplyEditsRequest(), null);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Invalid JSON payload.");
            }

            return TryParseFeatureArrayRequestFromJson(document.RootElement, primaryKey, fallbackKey, assignToAdds);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static async Task<(ApplyEditsRequest? Request, string? Error)> TryReadDeleteFeaturesRequestAsync(
        HttpRequest request,
        CancellationToken cancellationToken)
    {
        if (request.HasFormContentType)
        {
            var form = await request.ReadFormAsync(cancellationToken);
            var values = form.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            return TryParseDeleteFeaturesRequestFromValues(values);
        }

        if (request.ContentLength is 0)
        {
            return (new ApplyEditsRequest(), null);
        }

        try
        {
            using var document = await JsonDocument.ParseAsync(request.Body, cancellationToken: cancellationToken);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return (null, "Invalid JSON payload.");
            }

            return TryParseDeleteFeaturesRequestFromJson(document.RootElement);
        }
        catch (JsonException)
        {
            return (null, "Invalid JSON payload.");
        }
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseApplyEditsRequest(
        IReadOnlyDictionary<string, StringValues> values)
    {
        var request = new ApplyEditsRequest();

        if (!TryParseGeoServicesFeatures(values, "adds", out var adds, out var error))
        {
            return (null, error);
        }

        if (!TryParseGeoServicesFeatures(values, "updates", out var updates, out error))
        {
            return (null, error);
        }

        if (!TryParseDeletes(values, "deletes", out var deletes, out error))
        {
            return (null, error);
        }

        if (!TryParseRequestOptions(values, request, out error))
        {
            return (null, error);
        }

        request.Adds = adds;
        request.Updates = updates;
        request.Deletes = deletes;

        return (request, null);
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseFeatureArrayRequestFromValues(
        IReadOnlyDictionary<string, StringValues> values,
        string primaryKey,
        string fallbackKey,
        bool assignToAdds)
    {
        var request = new ApplyEditsRequest();
        if (!TryParseRequestOptions(values, request, out var error))
        {
            return (null, error);
        }

        if (!TryParseGeoServicesFeatures(values, primaryKey, out var features, out error))
        {
            return (null, error);
        }

        if (features == null && !string.Equals(primaryKey, fallbackKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseGeoServicesFeatures(values, fallbackKey, out features, out error))
            {
                return (null, error);
            }
        }

        if (assignToAdds)
        {
            request.Adds = features;
        }
        else
        {
            request.Updates = features;
        }

        return (request, null);
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseFeatureArrayRequestFromJson(
        JsonElement root,
        string primaryKey,
        string fallbackKey,
        bool assignToAdds)
    {
        var request = new ApplyEditsRequest();
        if (!TryParseRequestOptions(root, request, out var error))
        {
            return (null, error);
        }

        if (!TryParseGeoServicesFeatures(root, primaryKey, out var features, out error))
        {
            return (null, error);
        }

        if (features == null && !string.Equals(primaryKey, fallbackKey, StringComparison.OrdinalIgnoreCase))
        {
            if (!TryParseGeoServicesFeatures(root, fallbackKey, out features, out error))
            {
                return (null, error);
            }
        }

        if (assignToAdds)
        {
            request.Adds = features;
        }
        else
        {
            request.Updates = features;
        }

        return (request, null);
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseDeleteFeaturesRequestFromValues(
        IReadOnlyDictionary<string, StringValues> values)
    {
        var request = new ApplyEditsRequest();
        if (!TryParseRequestOptions(values, request, out var error))
        {
            return (null, error);
        }

        if (!TryParseDeletes(values, "objectIds", out var deletes, out error))
        {
            return (null, error);
        }

        if (deletes == null)
        {
            if (!TryParseDeletes(values, "deletes", out deletes, out error))
            {
                return (null, error);
            }
        }

        request.Deletes = deletes;
        return (request, null);
    }

    private static (ApplyEditsRequest? Request, string? Error) TryParseDeleteFeaturesRequestFromJson(JsonElement root)
    {
        var request = new ApplyEditsRequest();
        if (!TryParseRequestOptions(root, request, out var error))
        {
            return (null, error);
        }

        if (!TryParseDeletes(root, "objectIds", out var deletes, out error))
        {
            return (null, error);
        }

        if (deletes == null && !TryParseDeletes(root, "deletes", out deletes, out error))
        {
            return (null, error);
        }

        request.Deletes = deletes;
        return (request, null);
    }

    private static bool TryParseRequestOptions(
        IReadOnlyDictionary<string, StringValues> values,
        ApplyEditsRequest request,
        out string? error)
    {
        error = null;

        if (!TryParseBoolValue(values, "rollbackOnFailure", request.RollbackOnFailure, out var rollbackOnFailure, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "useGlobalIds", request.UseGlobalIds, out var useGlobalIds, out error))
        {
            return false;
        }

        if (!TryParseBoolValue(values, "returnEditMoment", request.ReturnEditMoment, out var returnEditMoment, out error))
        {
            return false;
        }

        request.RollbackOnFailure = rollbackOnFailure;
        request.UseGlobalIds = useGlobalIds;
        request.ReturnEditMoment = returnEditMoment;
        request.F = GetValueString(values, "f");
        request.GdbVersion = GetValueString(values, "gdbVersion");

        if (TryGetValue(values, "attachments", out var attachmentsRaw) && !StringValues.IsNullOrEmpty(attachmentsRaw))
        {
            request.Attachments = [attachmentsRaw.ToString()];
        }

        return true;
    }

    private static bool TryParseRequestOptions(
        JsonElement root,
        ApplyEditsRequest request,
        out string? error)
    {
        error = null;

        if (root.TryGetProperty("rollbackOnFailure", out var rollbackElement))
        {
            if (!TryParseBooleanElement(rollbackElement, out var rollbackOnFailure))
            {
                error = "rollbackOnFailure must be a boolean value";
                return false;
            }

            request.RollbackOnFailure = rollbackOnFailure;
        }

        if (root.TryGetProperty("useGlobalIds", out var globalIdsElement))
        {
            if (!TryParseBooleanElement(globalIdsElement, out var useGlobalIds))
            {
                error = "useGlobalIds must be a boolean value";
                return false;
            }

            request.UseGlobalIds = useGlobalIds;
        }

        if (root.TryGetProperty("returnEditMoment", out var editMomentElement))
        {
            if (!TryParseBooleanElement(editMomentElement, out var returnEditMoment))
            {
                error = "returnEditMoment must be a boolean value";
                return false;
            }

            request.ReturnEditMoment = returnEditMoment;
        }

        if (root.TryGetProperty("f", out var formatElement) && formatElement.ValueKind == JsonValueKind.String)
        {
            request.F = formatElement.GetString();
        }

        if (root.TryGetProperty("gdbVersion", out var gdbVersionElement) && gdbVersionElement.ValueKind == JsonValueKind.String)
        {
            request.GdbVersion = gdbVersionElement.GetString();
        }

        if (root.TryGetProperty("attachments", out var attachmentsElement))
        {
            request.Attachments = [attachmentsElement.GetRawText()];
        }

        return true;
    }

    private static bool TryParseGeoServicesFeatures(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out GeoServicesFeature[]? features,
        out string? error)
    {
        features = null;
        error = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var payload = raw.ToString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        try
        {
            features = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
            return true;
        }
        catch (JsonException)
        {
            error = $"{key} must be valid JSON.";
            return false;
        }
    }

    private static bool TryParseGeoServicesFeatures(
        JsonElement root,
        string key,
        out GeoServicesFeature[]? features,
        out string? error)
    {
        features = null;
        error = null;

        if (!root.TryGetProperty(key, out var element))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var payload = element.GetString();
            if (string.IsNullOrWhiteSpace(payload))
            {
                return true;
            }

            try
            {
                features = JsonSerializer.Deserialize(payload, FeatureServerJsonContext.Default.GeoServicesFeatureArray);
                return true;
            }
            catch (JsonException)
            {
                error = $"{key} must be valid JSON.";
                return false;
            }
        }

        if (element.ValueKind != JsonValueKind.Array)
        {
            error = $"{key} must be a JSON array.";
            return false;
        }

        try
        {
            features = JsonSerializer.Deserialize(element.GetRawText(), FeatureServerJsonContext.Default.GeoServicesFeatureArray);
            return true;
        }
        catch (JsonException)
        {
            error = $"{key} must be valid JSON.";
            return false;
        }
    }

    private static bool TryParseDeletes(
        IReadOnlyDictionary<string, StringValues> values,
        string key,
        out object[]? deletes,
        out string? error)
    {
        deletes = null;
        error = null;

        if (!TryGetValue(values, key, out var raw) || StringValues.IsNullOrEmpty(raw))
        {
            return true;
        }

        var payload = raw.ToString();
        if (string.IsNullOrWhiteSpace(payload))
        {
            return true;
        }

        var trimmedPayload = payload.TrimStart();
        if (trimmedPayload.Length > 0 && trimmedPayload[0] == '[')
        {
            try
            {
                using var document = JsonDocument.Parse(payload);
                if (document.RootElement.ValueKind != JsonValueKind.Array)
                {
                    error = $"{key} must be a JSON array.";
                    return false;
                }

                var items = new List<object>();
                foreach (var element in document.RootElement.EnumerateArray())
                {
                    items.Add(ConvertDeleteValue(element));
                }

                deletes = items.ToArray();
                return true;
            }
            catch (JsonException)
            {
                error = $"{key} must be valid JSON.";
                return false;
            }
        }

        deletes = ParseDeleteTokens(payload);
        return true;
    }

    private static bool TryParseDeletes(
        JsonElement root,
        string key,
        out object[]? deletes,
        out string? error)
    {
        deletes = null;
        error = null;

        if (!root.TryGetProperty(key, out var element) || element.ValueKind == JsonValueKind.Null)
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            var items = new List<object>();
            foreach (var item in element.EnumerateArray())
            {
                items.Add(ConvertDeleteValue(item));
            }

            deletes = items.ToArray();
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            deletes = ParseDeleteTokens(element.GetString() ?? string.Empty);
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number)
        {
            deletes = [ConvertDeleteValue(element)];
            return true;
        }

        error = $"{key} must be an array or comma-separated string.";
        return false;
    }

    private static object[] ParseDeleteTokens(string payload)
    {
        var tokens = payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return [];
        }

        var parsed = new object[tokens.Length];
        for (var i = 0; i < tokens.Length; i++)
        {
            if (long.TryParse(tokens[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
            {
                parsed[i] = id;
            }
            else
            {
                parsed[i] = tokens[i];
            }
        }

        return parsed;
    }

    private static bool TryParseBooleanElement(JsonElement element, out bool value)
    {
        value = false;

        if (element.ValueKind == JsonValueKind.True)
        {
            value = true;
            return true;
        }

        if (element.ValueKind == JsonValueKind.False)
        {
            value = false;
            return true;
        }

        if (element.ValueKind == JsonValueKind.Number && element.TryGetInt32(out var numeric))
        {
            value = numeric != 0;
            return true;
        }

        if (element.ValueKind == JsonValueKind.String)
        {
            var raw = element.GetString();
            if (bool.TryParse(raw, out var parsedBool))
            {
                value = parsedBool;
                return true;
            }

            if (int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsedInt))
            {
                value = parsedInt != 0;
                return true;
            }
        }

        return false;
    }

    private static object ConvertDeleteValue(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.Number when element.TryGetInt64(out var id) => id,
            JsonValueKind.Number => element.GetDouble(),
            JsonValueKind.String => element.GetString() ?? string.Empty,
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => element.GetRawText()
        };
    }
}
