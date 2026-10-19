// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;

namespace Honua.TestKit;

/// <summary>
/// HTTP response returned by a test message handler whose ownership transfers to the caller.
/// </summary>
public sealed class CallerOwnedHttpResponseMessage : HttpResponseMessage
{
    /// <summary>
    /// Initializes a response with the supplied status code.
    /// </summary>
    /// <param name="statusCode">Response status code.</param>
    public CallerOwnedHttpResponseMessage(HttpStatusCode statusCode)
        : base(statusCode)
    {
    }
}

/// <summary>
/// Memory stream returned by a test double whose ownership transfers to the caller.
/// </summary>
public sealed class CallerOwnedMemoryStream : MemoryStream
{
    /// <summary>Initializes an empty caller-owned stream.</summary>
    public CallerOwnedMemoryStream()
    {
    }

    /// <summary>Initializes a caller-owned stream over a byte buffer.</summary>
    /// <param name="buffer">Backing buffer.</param>
    public CallerOwnedMemoryStream(byte[] buffer)
        : base(buffer)
    {
    }

    /// <summary>Initializes a caller-owned stream over a byte buffer.</summary>
    /// <param name="buffer">Backing buffer.</param>
    /// <param name="writable">Whether the stream permits writes.</param>
    public CallerOwnedMemoryStream(byte[] buffer, bool writable)
        : base(buffer, writable)
    {
    }

    /// <summary>Initializes a caller-owned stream over a byte-buffer segment.</summary>
    /// <param name="buffer">Backing buffer.</param>
    /// <param name="index">Starting buffer offset.</param>
    /// <param name="count">Number of accessible bytes.</param>
    public CallerOwnedMemoryStream(byte[] buffer, int index, int count)
        : base(buffer, index, count)
    {
    }

    /// <summary>Initializes an empty caller-owned stream with the specified capacity.</summary>
    /// <param name="capacity">Initial stream capacity.</param>
    public CallerOwnedMemoryStream(int capacity)
        : base(capacity)
    {
    }
}

/// <summary>
/// HTTP client transferred to a test service that owns its lifetime.
/// </summary>
public sealed class CallerOwnedHttpClient : HttpClient
{
    /// <summary>
    /// Initializes a caller-owned client and transfers handler disposal to it.
    /// </summary>
    /// <param name="handler">HTTP message handler.</param>
    public CallerOwnedHttpClient(HttpMessageHandler handler)
        : base(handler)
    {
    }
}
