// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Infrastructure.Rendering;

/// <summary>
/// Thrown when a MapLibre style expression is well-formed enough to parse but cannot be
/// evaluated to a usable value — for example an <c>interpolate</c> whose stop outputs mix
/// types, or whose outputs are neither numbers nor colors.
/// </summary>
/// <remarks>
/// MapLibre GL JS rejects these expressions at style-validation time; because this
/// evaluator parses styles lazily at render time, the equivalent failure surfaces here.
/// The evaluator raises this rather than substituting a default so an unevaluatable style
/// cannot render as a confident, plausible-looking image — the silent <c>0f</c> coercion it
/// replaces turned every color <c>interpolate</c> into black with no throw, warning, or log
/// (honua-server#2867). The message describes the offending stop types only; it carries no
/// server internals and is safe to log, though protocol adapters map it through the shared
/// problem helpers rather than returning it verbatim.
/// </remarks>
public sealed class StyleExpressionEvaluationException : Exception
{
    /// <summary>
    /// Initializes a new instance with a description of the unevaluatable expression.
    /// </summary>
    /// <param name="message">A description of why the expression could not be evaluated.</param>
    public StyleExpressionEvaluationException(string message)
        : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance with a description of the unevaluatable expression
    /// and the underlying cause.
    /// </summary>
    /// <param name="message">A description of why the expression could not be evaluated.</param>
    /// <param name="innerException">The underlying cause.</param>
    public StyleExpressionEvaluationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
