// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.Protocols.Scene;

// Bounds throw InvalidOperationException, because data-annotation validation throws OptionsValidationException and the endpoint catch blocks would miss it.
internal sealed class SceneAccessSigningOptions
{
    public const string SectionName = "Honua:SceneAccessSigning";

    public const int DefaultTokenTtlMinutes = 15;

    public const int MinTokenTtlMinutes = 1;

    public const int MaxTokenTtlMinutes = 1440;

    public const double DefaultRefreshAfterFraction = 0.5;

    public string SigningKey { get; set; } = string.Empty;

    public int TokenTtlMinutes { get; set; } = DefaultTokenTtlMinutes;

    public double RefreshAfterFractionOfTtl { get; set; } = DefaultRefreshAfterFraction;
}
