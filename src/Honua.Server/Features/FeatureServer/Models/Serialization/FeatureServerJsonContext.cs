// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.FeatureServer.Models;

/// <summary>
/// JSON serialization context for FeatureServer API models with source generation for AOT compatibility.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    PropertyNameCaseInsensitive = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(FeatureServerResponse))]
[JsonSerializable(typeof(LayerResponse))]
[JsonSerializable(typeof(LayerRelationshipInfo))]
[JsonSerializable(typeof(LayerRelationshipInfo[]))]
[JsonSerializable(typeof(LayerInfo))]
[JsonSerializable(typeof(FeatureServerTimeInfo))]
[JsonSerializable(typeof(SpatialReferenceInfo))]
[JsonSerializable(typeof(GeoServicesSpatialReference))]
[JsonSerializable(typeof(ExtentInfo))]
[JsonSerializable(typeof(GeoServicesFieldInfo))]
[JsonSerializable(typeof(QueryResponse))]
[JsonSerializable(typeof(GeoServicesFeature))]
[JsonSerializable(typeof(GeoServicesFeature[]), TypeInfoPropertyName = "GeoServicesFeatureArray")]
[JsonSerializable(typeof(GeoServicesGeometry))]
[JsonSerializable(typeof(QueryParameters))]
[JsonSerializable(typeof(GeoJsonFeatureSet))]
[JsonSerializable(typeof(GeoJsonFeature), TypeInfoPropertyName = "FeatureServerGeoJsonFeature")]
[JsonSerializable(typeof(GeoJsonFeature[]), TypeInfoPropertyName = "FeatureServerGeoJsonFeatureArray")]
[JsonSerializable(typeof(GeoJsonGeometry))]
[JsonSerializable(typeof(GeoJsonCrs))]
[JsonSerializable(typeof(ApplyEditsRequest))]
[JsonSerializable(typeof(ApplyEditsResponse))]
[JsonSerializable(typeof(EditResult))]
[JsonSerializable(typeof(EditError))]
[JsonSerializable(typeof(QueryRelatedRecordsParameters))]
[JsonSerializable(typeof(QueryRelatedRecordsResponse))]
[JsonSerializable(typeof(RelatedRecordGroup))]
[JsonSerializable(typeof(RelatedRecords))]
[JsonSerializable(typeof(double[]))]
[JsonSerializable(typeof(double[][]))]
[JsonSerializable(typeof(double[][][]))]
[JsonSerializable(typeof(ApiErrorResponse))]
[JsonSerializable(typeof(GeoServicesError))]
[JsonSerializable(typeof(Dictionary<string, object>))]
[JsonSerializable(typeof(Dictionary<string, object?>))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(AttachmentInfo))]
[JsonSerializable(typeof(AttachmentGroup))]
[JsonSerializable(typeof(AttachmentQueryResponse))]
[JsonSerializable(typeof(AddAttachmentResult))]
[JsonSerializable(typeof(AddAttachmentResponse))]
[JsonSerializable(typeof(UpdateAttachmentResult))]
[JsonSerializable(typeof(UpdateAttachmentResponse))]
[JsonSerializable(typeof(DeleteAttachmentResult))]
[JsonSerializable(typeof(DeleteAttachmentsResponse))]
[JsonSerializable(typeof(IReadOnlyList<object>))]
[JsonSerializable(typeof(object))]
[JsonSerializable(typeof(object[]))]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(int))]
[JsonSerializable(typeof(long))]
[JsonSerializable(typeof(double))]
[JsonSerializable(typeof(bool))]
[JsonSerializable(typeof(DateTime))]
[JsonSerializable(typeof(DateTimeOffset))]
// ASP.NET Core types
[JsonSerializable(typeof(Microsoft.AspNetCore.Mvc.ProblemDetails))]
[JsonSerializable(typeof(ImmutableArray<string>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<double>>))]
[JsonSerializable(typeof(ImmutableArray<ImmutableArray<string?>>))]
[JsonSerializable(typeof(ImmutableArray<string>?))]
internal sealed partial class FeatureServerJsonContext : JsonSerializerContext;
