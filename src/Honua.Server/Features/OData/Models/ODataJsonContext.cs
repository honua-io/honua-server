// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Server.Features.OData.Models;

/// <summary>
/// JSON serialization context for OData models with AOT support
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ServiceDocument))]
[JsonSerializable(typeof(EntitySet))]
[JsonSerializable(typeof(EntitySet[]))]
[JsonSerializable(typeof(ODataResponse))]
[JsonSerializable(typeof(ODataError))]
[JsonSerializable(typeof(ErrorDetails))]
[JsonSerializable(typeof(ErrorDetail))]
[JsonSerializable(typeof(ErrorDetail[]))]
[JsonSerializable(typeof(ODataFeatureRequest))]
[JsonSerializable(typeof(ODataFeatureResponse))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(decimal))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTimeOffset))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(Dictionary<string, object?>[]))]
[JsonSerializable(typeof(Dictionary<string, string>))]
// Batch operation types
[JsonSerializable(typeof(ODataBatchRequest))]
[JsonSerializable(typeof(ODataBatchRequestItem))]
[JsonSerializable(typeof(ODataBatchRequestItem[]))]
[JsonSerializable(typeof(ODataBatchResponse))]
[JsonSerializable(typeof(ODataBatchResponseItem))]
[JsonSerializable(typeof(ODataBatchResponseItem[]))]
// Aggregation types
[JsonSerializable(typeof(ODataAggregationResult))]
[JsonSerializable(typeof(AggregationGroup))]
[JsonSerializable(typeof(AggregationGroup[]))]
// Expanded/search result types
[JsonSerializable(typeof(ODataExpandedResponse))]
[JsonSerializable(typeof(ODataSearchResult))]
internal partial class ODataJsonContext : JsonSerializerContext
{
}
