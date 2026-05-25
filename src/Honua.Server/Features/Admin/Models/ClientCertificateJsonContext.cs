// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json.Serialization;
using Honua.Server.Features.Infrastructure.Models;

namespace Honua.Server.Features.Admin.Models;

/// <summary>
/// JSON source generation context for client-certificate admin API models.
/// </summary>
[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = false)]
[JsonSerializable(typeof(ClientCertificateTrustProfileResponse))]
[JsonSerializable(typeof(ClientCertificateTrustProfileResponse[]))]
[JsonSerializable(typeof(UpsertClientCertificateTrustProfileRequest))]
[JsonSerializable(typeof(ClientCertificatePrincipalMappingResponse))]
[JsonSerializable(typeof(ClientCertificatePrincipalMappingResponse[]))]
[JsonSerializable(typeof(UpsertClientCertificatePrincipalMappingRequest))]
[JsonSerializable(typeof(ClientCertificateRevocationEntryResponse))]
[JsonSerializable(typeof(ClientCertificateRevocationEntryResponse[]))]
[JsonSerializable(typeof(AddClientCertificateRevocationRequest))]
[JsonSerializable(typeof(ValidateClientCertificateRequest))]
[JsonSerializable(typeof(ValidateClientCertificateResponse))]
[JsonSerializable(typeof(ApiResponse<ClientCertificateTrustProfileResponse>))]
[JsonSerializable(typeof(ApiResponse<ClientCertificateTrustProfileResponse[]>))]
[JsonSerializable(typeof(ApiResponse<ClientCertificatePrincipalMappingResponse>))]
[JsonSerializable(typeof(ApiResponse<ClientCertificatePrincipalMappingResponse[]>))]
[JsonSerializable(typeof(ApiResponse<ClientCertificateRevocationEntryResponse>))]
[JsonSerializable(typeof(ApiResponse<ClientCertificateRevocationEntryResponse[]>))]
[JsonSerializable(typeof(ApiResponse<ValidateClientCertificateResponse>))]
[JsonSerializable(typeof(ApiResponse<object>))]
internal sealed partial class ClientCertificateJsonContext : JsonSerializerContext;
