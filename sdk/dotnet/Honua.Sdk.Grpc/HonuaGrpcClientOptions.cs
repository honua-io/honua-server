// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Sdk.Grpc;

/// <summary>
/// Configuration options for the Honua gRPC client.
/// </summary>
public sealed class HonuaGrpcClientOptions
{
    /// <summary>
    /// Address of the Honua gRPC server.
    /// </summary>
    public string Address { get; set; } = "https://localhost:5001";

    /// <summary>
    /// API key for authentication (sent as grpc-metadata header).
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Bearer token for authentication.
    /// </summary>
    public string? BearerToken { get; set; }

    /// <summary>
    /// Enables gRPC compression negotiation for responses.
    /// </summary>
    public bool EnableCompressionNegotiation { get; set; } = true;

    /// <summary>
    /// Accepted gRPC compression algorithms advertised to the server.
    /// </summary>
    public string AcceptedCompressionEncodings { get; set; } = "gzip,identity";
}
