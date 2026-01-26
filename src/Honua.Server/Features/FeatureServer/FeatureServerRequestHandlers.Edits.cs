// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using Honua.Core.Configuration;
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

        if (request.UseGlobalIds)
        {
            return StandardErrorHelpers.CreateBadRequest(context,
                "useGlobalIds is not supported",
                ["Set useGlobalIds to false and supply objectIds in attributes."]);
        }

        return await editsHandler.HandleApplyEditsAsync(
            serviceId,
            layerId,
            request,
            limitsOptions.Value.Edits,
            cancellationToken);
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

        if (!TryParseDeletes(values, out var deletes, out error))
        {
            return (null, error);
        }

        if (!TryParseBoolValue(values, "rollbackOnFailure", false, out var rollbackOnFailure, out error))
        {
            return (null, error);
        }

        if (!TryParseBoolValue(values, "useGlobalIds", false, out var useGlobalIds, out error))
        {
            return (null, error);
        }

        request.Adds = adds;
        request.Updates = updates;
        request.Deletes = deletes;
        request.RollbackOnFailure = rollbackOnFailure;
        request.UseGlobalIds = useGlobalIds;

        return (request, null);
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

    private static bool TryParseDeletes(
        IReadOnlyDictionary<string, StringValues> values,
        out object[]? deletes,
        out string? error)
    {
        deletes = null;
        error = null;

        if (!TryGetValue(values, "deletes", out var raw) || StringValues.IsNullOrEmpty(raw))
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
                    error = "deletes must be a JSON array.";
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
                error = "deletes must be valid JSON.";
                return false;
            }
        }

        var tokens = payload.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (tokens.Length == 0)
        {
            return true;
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

        deletes = parsed;
        return true;
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
