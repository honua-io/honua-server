// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Core.Features.Console.Domain;

namespace Honua.Core.Features.OpenData.Domain;

/// <summary>
/// Source-generated JSON metadata for persisted open-data domain records.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(OpenDataPageRecord))]
[JsonSerializable(typeof(OpenDataStacPublicationRecord))]
[JsonSerializable(typeof(OpenDataOrganization))]
[JsonSerializable(typeof(OpenDataContact))]
[JsonSerializable(typeof(OpenDataDistribution))]
[JsonSerializable(typeof(OpenDataSpatialExtent))]
[JsonSerializable(typeof(OpenDataTemporalExtent))]
[JsonSerializable(typeof(ConsoleProvenanceRef))]
public sealed partial class OpenDataDomainJsonContext : JsonSerializerContext;
