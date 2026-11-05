// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Ai.AnalysisContent;

internal sealed class AnalysisContentValidationException : Exception
{
    public AnalysisContentValidationException(string message, string code, string? path = null)
        : base(message)
    {
        Code = code;
        Path = path;
    }

    public string Code { get; }

    public string? Path { get; }
}

internal sealed class AnalysisContentNotFoundException(string message) : Exception(message);

internal sealed class AnalysisContentConflictException(string message) : Exception(message);
