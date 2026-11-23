// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json.Serialization;

namespace Honua.Protocols.Scene.Models;

internal sealed record SceneAccessCredentialTransport(
    [property: JsonPropertyName("kind")] string Kind,
    [property: JsonPropertyName("name")] string Name,
    [property: JsonPropertyName("valueTemplate")] string ValueTemplate,
    [property: JsonPropertyName("preferred")] bool Preferred);

internal sealed record SceneAccessAssetScope(
    [property: JsonPropertyName("scheme")] string Scheme,
    [property: JsonPropertyName("host")] string Host,
    [property: JsonPropertyName("port")] int Port,
    [property: JsonPropertyName("pathPrefix")] string PathPrefix);

// The token is accepted on the query string because browser clients cannot attach a bearer header to nested 3D Tiles requests, and it must not be logged.
internal sealed record SceneAccessEnvelope(
    [property: JsonPropertyName("sceneId")] string SceneId,
    [property: JsonPropertyName("token")] string Token,
    [property: JsonPropertyName("expiresAt")] DateTimeOffset ExpiresAt,
    [property: JsonPropertyName("refreshAfter")] DateTimeOffset RefreshAfter,
    [property: JsonPropertyName("allowedMethods")] IReadOnlyList<string> AllowedMethods,
    [property: JsonPropertyName("contractVersion")] string ContractVersion = SceneAccessEnvelope.CurrentContractVersion,
    [property: JsonPropertyName("transports")] IReadOnlyList<SceneAccessCredentialTransport>? Transports = null,
    [property: JsonPropertyName("assetScopes")] IReadOnlyList<SceneAccessAssetScope>? AssetScopes = null)
{
    public const string CurrentContractVersion = "2026-08-v1";

    public override string ToString()
        => $"SceneAccessEnvelope {{ SceneId = {SceneId}, Token = [redacted], ExpiresAt = {ExpiresAt:O}, RefreshAfter = {RefreshAfter:O}, ContractVersion = {ContractVersion} }}";
}
