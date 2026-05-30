// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Globalization;
using Honua.Protocols.OData.Models;

namespace Honua.Protocols.OData.Services;

/// <summary>
/// Dependency ordering and Content-ID reference resolution for the OData $batch handler.
/// </summary>
internal sealed partial class ODataBatchHandler
{
    private static bool HasDependencies(ImmutableArray<ODataBatchRequestItem> requests)
        => requests.Any(r => r.DependsOn is { Length: > 0 });

    private static List<ODataBatchResponseItem> CreateAtomicDependencyErrors(
        ImmutableArray<ODataBatchRequestItem> requests)
    {
        const string message = "dependsOn is not supported for atomicity groups.";
        return requests.Select(r => CreateErrorResponse(r.Id, 400, "InvalidRequest", message)).ToList();
    }

    private static DependencyOrderResult OrderRequestsByDependencies(ImmutableArray<ODataBatchRequestItem> requests)
    {
        var errorsById = new Dictionary<string, ODataBatchResponseItem>(StringComparer.Ordinal);
        var requestById = new Dictionary<string, ODataBatchRequestItem>(StringComparer.Ordinal);
        var orderedIds = new List<string>(requests.Length);
        var invalid = new HashSet<string>(StringComparer.Ordinal);

        foreach (var request in requests)
        {
            if (!requestById.TryAdd(request.Id, request))
            {
                errorsById[request.Id] = CreateErrorResponse(
                    request.Id,
                    400,
                    "InvalidRequest",
                    $"Duplicate request id '{request.Id}'.");
                invalid.Add(request.Id);
                continue;
            }

            orderedIds.Add(request.Id);
        }

        bool changed;
        do
        {
            changed = false;
            foreach (var request in requests)
            {
                if (invalid.Contains(request.Id) || errorsById.ContainsKey(request.Id))
                {
                    continue;
                }

                if (request.DependsOn == null || request.DependsOn.Length == 0)
                {
                    continue;
                }

                foreach (var dependencyId in request.DependsOn)
                {
                    if (string.IsNullOrWhiteSpace(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            "dependsOn contains an empty request id.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (string.Equals(dependencyId, request.Id, StringComparison.Ordinal))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            "dependsOn cannot reference the same request id.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (!requestById.ContainsKey(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            $"dependsOn references unknown request id '{dependencyId}'.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }

                    if (invalid.Contains(dependencyId))
                    {
                        errorsById[request.Id] = CreateErrorResponse(
                            request.Id,
                            400,
                            "InvalidRequest",
                            $"dependsOn references invalid request id '{dependencyId}'.");
                        invalid.Add(request.Id);
                        changed = true;
                        break;
                    }
                }
            }
        }
        while (changed);

        var validIds = orderedIds
            .Where(id => !invalid.Contains(id) && !errorsById.ContainsKey(id))
            .ToList();
        var validSet = new HashSet<string>(validIds, StringComparer.Ordinal);

        var indegree = new Dictionary<string, int>(StringComparer.Ordinal);
        var dependents = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var id in validIds)
        {
            indegree[id] = 0;
            dependents[id] = [];
        }

        foreach (var request in requests)
        {
            if (!validSet.Contains(request.Id))
            {
                continue;
            }

            if (request.DependsOn == null || request.DependsOn.Length == 0)
            {
                continue;
            }

            foreach (var dependencyId in request.DependsOn)
            {
                if (!validSet.Contains(dependencyId))
                {
                    continue;
                }

                indegree[request.Id]++;
                dependents[dependencyId].Add(request.Id);
            }
        }

        var queue = new Queue<string>(validIds.Where(id => indegree[id] == 0));
        var orderedRequests = new List<ODataBatchRequestItem>(validIds.Count);
        var processed = new HashSet<string>(StringComparer.Ordinal);

        while (queue.Count > 0)
        {
            var id = queue.Dequeue();
            processed.Add(id);
            orderedRequests.Add(requestById[id]);

            foreach (var dependent in dependents[id])
            {
                indegree[dependent]--;
                if (indegree[dependent] == 0)
                {
                    queue.Enqueue(dependent);
                }
            }
        }

        if (processed.Count != validSet.Count)
        {
            foreach (var id in validSet.Where(id => !processed.Contains(id)))
            {
                errorsById[id] = CreateErrorResponse(
                    id,
                    400,
                    "InvalidRequest",
                    "dependsOn contains a cyclic dependency.");
            }
        }

        return new DependencyOrderResult(orderedRequests.ToImmutableArray(), errorsById);
    }

    private sealed record DependencyOrderResult(
        ImmutableArray<ODataBatchRequestItem> OrderedRequests,
        Dictionary<string, ODataBatchResponseItem> ErrorsById);

    private static bool TryResolveContentIdReferences(
        ODataBatchRequestItem request,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out ODataBatchRequestItem resolvedRequest,
        out string? errorMessage)
    {
        errorMessage = null;
        if (!TryResolveRequestUrl(request.Url, responsesById, out var resolvedUrl, out errorMessage))
        {
            resolvedRequest = request;
            return false;
        }

        if (!TryResolveBodyReferences(request.Body, responsesById, out var resolvedBody, out errorMessage))
        {
            resolvedRequest = request;
            return false;
        }

        resolvedRequest = new ODataBatchRequestItem
        {
            Id = request.Id,
            Method = request.Method,
            Url = resolvedUrl,
            Headers = request.Headers,
            Body = resolvedBody,
            AtomicityGroup = request.AtomicityGroup,
            DependsOn = request.DependsOn
        };

        return true;
    }

    private static bool TryResolveRequestUrl(
        string url,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out string resolvedUrl,
        out string? errorMessage)
    {
        resolvedUrl = url;
        errorMessage = null;

        var trimmed = url.Trim();
        if (!trimmed.StartsWith('$'))
        {
            return TryNormalizeClientBatchTargetUrl(trimmed, out resolvedUrl, out errorMessage);
        }

        var delimiterIndex = trimmed.IndexOfAny(['/', '?']);
        var referenceId = delimiterIndex >= 0 ? trimmed[1..delimiterIndex] : trimmed[1..];
        var suffix = delimiterIndex >= 0 ? trimmed[delimiterIndex..] : string.Empty;

        if (!TryResolveContentIdTarget(referenceId, responsesById, out var targetUrl, out errorMessage))
        {
            return false;
        }

        resolvedUrl = $"{NormalizeBatchTargetUrl(targetUrl)}{suffix}";
        return true;
    }

    private static bool TryResolveBodyReferences(
        Dictionary<string, object?>? body,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out Dictionary<string, object?>? resolvedBody,
        out string? errorMessage)
    {
        errorMessage = null;
        if (body == null)
        {
            resolvedBody = null;
            return true;
        }

        resolvedBody = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (key, value) in body)
        {
            if (!TryResolveBodyValue(value, responsesById, out var resolvedValue, out errorMessage))
            {
                return false;
            }

            resolvedBody[key] = resolvedValue;
        }

        return true;
    }

    private static bool TryResolveBodyValue(
        object? value,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out object? resolvedValue,
        out string? errorMessage)
    {
        errorMessage = null;
        resolvedValue = value;

        if (value is string stringValue &&
            stringValue.StartsWith('$') &&
            !stringValue.Contains('/', StringComparison.Ordinal) &&
            !stringValue.Contains('?', StringComparison.Ordinal))
        {
            var referenceId = stringValue[1..];
            if (!TryResolveContentIdTarget(referenceId, responsesById, out var targetUrl, out errorMessage))
            {
                resolvedValue = null;
                return false;
            }

            resolvedValue = targetUrl;
            return true;
        }

        if (value is IDictionary<string, object?> dictionary)
        {
            var result = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var (key, nestedValue) in dictionary)
            {
                if (!TryResolveBodyValue(nestedValue, responsesById, out var resolvedNestedValue, out errorMessage))
                {
                    resolvedValue = null;
                    return false;
                }

                result[key] = resolvedNestedValue;
            }

            resolvedValue = result;
            return true;
        }

        if (value is IEnumerable<object?> listValue)
        {
            var resolvedItems = new List<object?>();
            foreach (var item in listValue)
            {
                if (!TryResolveBodyValue(item, responsesById, out var resolvedItem, out errorMessage))
                {
                    resolvedValue = null;
                    return false;
                }

                resolvedItems.Add(resolvedItem);
            }

            resolvedValue = resolvedItems.ToArray();
        }

        return true;
    }

    private static bool TryResolveContentIdTarget(
        string referenceId,
        IReadOnlyDictionary<string, ODataBatchResponseItem> responsesById,
        out string targetUrl,
        out string? errorMessage)
    {
        targetUrl = string.Empty;
        errorMessage = null;

        if (!responsesById.TryGetValue(referenceId, out var referencedResponse))
        {
            errorMessage = $"Unknown Content-ID reference '${referenceId}'.";
            return false;
        }

        if (referencedResponse.Status >= 400)
        {
            errorMessage = $"Content-ID reference '${referenceId}' points to a failed request.";
            return false;
        }

        if (referencedResponse.Headers != null &&
            (referencedResponse.Headers.TryGetValue("EntityId", out var entityId) ||
             referencedResponse.Headers.TryGetValue("OData-EntityId", out entityId)) &&
            !string.IsNullOrWhiteSpace(entityId))
        {
            targetUrl = NormalizeBatchTargetUrl(entityId);
            return true;
        }

        if (referencedResponse.Headers != null &&
            referencedResponse.Headers.TryGetValue("Location", out var location) &&
            !string.IsNullOrWhiteSpace(location))
        {
            targetUrl = NormalizeBatchTargetUrl(location);
            return true;
        }

        if (referencedResponse.Body is Dictionary<string, object?> dictionary &&
            TryGetInt(dictionary, "LayerId", out var layerId) &&
            TryGetLong(dictionary, "ObjectId", out var objectId))
        {
            targetUrl = $"/odata/Features(LayerId={layerId},ObjectId={objectId})";
            return true;
        }

        errorMessage = $"Content-ID reference '${referenceId}' does not identify an entity resource.";
        return false;
    }

    private static bool TryGetInt(Dictionary<string, object?> values, string key, out int result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) && value switch
        {
            int i => (result = i) >= 0,
            long l when l is >= int.MinValue and <= int.MaxValue => (result = (int)l) >= 0,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) >= 0,
            _ => false
        };
    }

    private static bool TryGetLong(Dictionary<string, object?> values, string key, out long result)
    {
        result = 0;
        return values.TryGetValue(key, out var value) && value switch
        {
            long l => (result = l) >= 0,
            int i => (result = i) >= 0,
            string s when long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => (result = parsed) >= 0,
            _ => false
        };
    }
}
