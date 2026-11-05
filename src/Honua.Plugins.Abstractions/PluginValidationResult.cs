// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Plugins.Abstractions;

public readonly record struct PluginValidationResult
{
    private PluginValidationResult(bool isValid, int errorCode, string? errorMessage)
    {
        IsValid = isValid;
        ErrorCode = errorCode;
        ErrorMessage = errorMessage;
    }

    public bool IsValid { get; }

    public int ErrorCode { get; }

    public string? ErrorMessage { get; }

    public static PluginValidationResult Success() => new(isValid: true, errorCode: 0, errorMessage: null);

    public static PluginValidationResult Error(string message, int code = 1000)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(message);
        return new PluginValidationResult(isValid: false, errorCode: code, errorMessage: message);
    }
}
