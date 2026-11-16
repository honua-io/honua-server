// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Net;

namespace Honua.TestKit;

public sealed class CallerOwnedHttpResponseMessage : HttpResponseMessage
{
    public CallerOwnedHttpResponseMessage(HttpStatusCode statusCode)
        : base(statusCode)
    {
    }
}

public sealed class CallerOwnedMemoryStream : MemoryStream
{
    public CallerOwnedMemoryStream()
    {
    }

    public CallerOwnedMemoryStream(byte[] buffer)
        : base(buffer)
    {
    }

    public CallerOwnedMemoryStream(byte[] buffer, bool writable)
        : base(buffer, writable)
    {
    }

    public CallerOwnedMemoryStream(byte[] buffer, int index, int count)
        : base(buffer, index, count)
    {
    }

    public CallerOwnedMemoryStream(int capacity)
        : base(capacity)
    {
    }
}

public sealed class CallerOwnedHttpClient : HttpClient
{
    public CallerOwnedHttpClient(HttpMessageHandler handler)
        : base(handler)
    {
    }
}
