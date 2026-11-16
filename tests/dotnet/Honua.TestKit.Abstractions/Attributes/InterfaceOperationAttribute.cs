// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public sealed class InterfaceOperationAttribute(string protocol, string operation) : Attribute
{
    public string Protocol { get; } = protocol;
    public string Operation { get; } = operation;
}
