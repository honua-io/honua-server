// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

public sealed class EvalScenarioException : Exception
{
    public EvalScenarioException(string message) : base(message) { }

    public EvalScenarioException(string message, Exception innerException) : base(message, innerException) { }
}
