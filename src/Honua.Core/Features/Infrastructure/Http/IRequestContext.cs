// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Core.Features.Infrastructure.Http;

public interface IRequestContext
{
    string BaseUrl { get; }

    string RequestUrl { get; }

    string Scheme { get; }

    string Host { get; }

    string Path { get; }

    IReadOnlyDictionary<string, string> QueryParameters { get; }

    IReadOnlyDictionary<string, string> Headers { get; }

    string? UserAgent { get; }

    string? ClientIP { get; }

    string? GetQueryParameter(string name);

    string? GetHeader(string name);
}
