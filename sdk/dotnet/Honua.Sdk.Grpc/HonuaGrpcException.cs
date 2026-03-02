// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using Grpc.Core;

namespace Honua.Sdk.Grpc;

/// <summary>
/// Exception thrown when a gRPC call to the Honua server fails.
/// </summary>
public sealed class HonuaGrpcException : Exception
{
    /// <summary>
    /// The gRPC status code.
    /// </summary>
    public StatusCode StatusCode { get; }

    /// <summary>
    /// Creates a new gRPC exception.
    /// </summary>
    /// <param name="statusCode">The gRPC status code.</param>
    /// <param name="message">The error detail message.</param>
    /// <param name="innerException">The original exception, if any.</param>
    public HonuaGrpcException(StatusCode statusCode, string message, Exception? innerException = null)
        : base($"gRPC {statusCode}: {message}", innerException)
    {
        StatusCode = statusCode;
    }
}
