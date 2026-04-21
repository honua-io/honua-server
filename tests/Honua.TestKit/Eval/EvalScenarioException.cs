// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.TestKit.Eval;

/// <summary>
/// Raised only at the <see cref="EvalRunner"/> boundary when a fatal configuration
/// or loader error prevents scenario execution. Per-stage failures surface through
/// <see cref="EvalStageOutcome"/> rather than exceptions.
/// </summary>
public sealed class EvalScenarioException : Exception
{
    /// <summary>Initializes the exception with the given message.</summary>
    public EvalScenarioException(string message) : base(message) { }

    /// <summary>Initializes the exception with the given message and inner exception.</summary>
    public EvalScenarioException(string message, Exception innerException) : base(message, innerException) { }
}
