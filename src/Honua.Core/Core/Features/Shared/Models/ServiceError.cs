// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Globalization;

namespace Honua.Core.Features.Shared.Models;

public readonly record struct ServiceError
{
    public required string Code { get; init; }

    public required string Message { get; init; }

    public string? Target { get; init; }

    public IReadOnlyList<string>? Details { get; init; }

    public static ServiceError Create(string code, string message)
        => new() { Code = code, Message = message };

    public static ServiceError Create(string code, string message, string target)
        => new() { Code = code, Message = message, Target = target };

    public static ServiceError Create(string code, string message, string? target, IReadOnlyList<string>? details)
        => new() { Code = code, Message = message, Target = target, Details = details };

    public static ServiceError Create(int code, string message)
        => new() { Code = code.ToString(CultureInfo.InvariantCulture), Message = message };

    public int? GetNumericCode()
        => int.TryParse(Code, out var numericCode) ? numericCode : null;
}
