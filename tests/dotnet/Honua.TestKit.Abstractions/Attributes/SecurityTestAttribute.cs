// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Attributes;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method)]
public sealed class SecurityTestAttribute : Attribute
{
    public SecurityTestAttribute()
    {
    }

    public SecurityTestAttribute(string description)
    {
        Description = description;
    }

    public string? Description { get; }
}
