// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using Microsoft.Extensions.Primitives;

namespace Honua.Infrastructure.Validation;

// The shared input middleware enforces this parameter's byte budget before protocol parsing; other parameters keep their text limits.
internal abstract class GeometryParameterMetadata
{
    public abstract string ParameterName { get; }

    public abstract string? Validate(string value, int maxVertices, CancellationToken cancellationToken = default);

    public abstract Task<(IReadOnlyDictionary<string, StringValues>? Values, string? Error)> ReadBodyParametersAsync(
        HttpRequest request, CancellationToken cancellationToken);
}
