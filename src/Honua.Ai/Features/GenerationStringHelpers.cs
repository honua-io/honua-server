namespace Honua.Ai;

/// <summary>
/// Shared string helpers for the AI generation services. Consolidates logic that was
/// previously duplicated across the per-feature generation services (#2403).
/// </summary>
internal static class GenerationStringHelpers
{
    /// <summary>
    /// Truncates <paramref name="value"/> to at most <paramref name="maxLength"/> characters,
    /// appending an ellipsis (<c>"..."</c>) when the value is longer. Intended for bounding
    /// provider error/response text before it is logged.
    /// </summary>
    /// <param name="value">The string to truncate.</param>
    /// <param name="maxLength">The maximum number of characters to retain before the ellipsis.</param>
    /// <returns>The original value when within the limit; otherwise the truncated value with an ellipsis appended.</returns>
    internal static string Truncate(string value, int maxLength = 500) =>
        value.Length <= maxLength ? value : string.Concat(value.AsSpan(0, maxLength), "...");
}
