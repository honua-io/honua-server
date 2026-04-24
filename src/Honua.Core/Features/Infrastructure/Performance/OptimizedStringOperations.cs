// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Buffers;
using System.Runtime.CompilerServices;
using System.Text;
using Microsoft.Extensions.ObjectPool;

namespace Honua.Core.Features.Infrastructure.Performance;

/// <summary>
/// High-performance string operations optimized for hot paths in query building and response formatting.
/// Reduces memory allocations and improves performance through string pooling and efficient formatting.
/// </summary>
public static class OptimizedStringOperations
{
    private static readonly ObjectPool<StringBuilder> _stringBuilderPool =
        new DefaultObjectPool<StringBuilder>(new StringBuilderPooledObjectPolicy());

    private static readonly ArrayPool<char> _charPool = ArrayPool<char>.Shared;

    /// <summary>
    /// Efficiently concatenates strings using object pooling to minimize allocations.
    /// </summary>
    /// <param name="values">Values to concatenate</param>
    /// <returns>Concatenated string</returns>
    public static string ConcatOptimized(params ReadOnlySpan<string?> values)
    {
        if (values.IsEmpty) return string.Empty;
        if (values.Length == 1) return values[0] ?? string.Empty;

        // Calculate total length to minimize StringBuilder reallocations
        var totalLength = 0;
        for (int i = 0; i < values.Length; i++)
        {
            totalLength += values[i]?.Length ?? 0;
        }

        if (totalLength == 0) return string.Empty;

        var sb = _stringBuilderPool.Get();
        try
        {
            sb.EnsureCapacity(totalLength);

            for (int i = 0; i < values.Length; i++)
            {
                if (values[i] != null)
                {
                    sb.Append(values[i]);
                }
            }

            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Efficiently joins strings with a separator using minimal allocations.
    /// </summary>
    /// <param name="separator">Separator string</param>
    /// <param name="values">Values to join</param>
    /// <returns>Joined string</returns>
    public static string JoinOptimized(string? separator, IEnumerable<string?> values)
    {
        if (values == null) return string.Empty;

        var sep = separator ?? string.Empty;
        var sb = _stringBuilderPool.Get();
        try
        {
            bool first = true;
            foreach (var value in values)
            {
                if (value != null)
                {
                    if (!first && sep.Length > 0)
                    {
                        sb.Append(sep);
                    }
                    sb.Append(value);
                    first = false;
                }
            }

            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Optimized string replacement avoiding intermediate string allocations.
    /// </summary>
    /// <param name="input">Input string</param>
    /// <param name="oldValue">Value to replace</param>
    /// <param name="newValue">Replacement value</param>
    /// <returns>String with replacements</returns>
    public static string ReplaceOptimized(string input, string oldValue, string newValue)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldValue))
            return input;

        var index = input.IndexOf(oldValue, StringComparison.Ordinal);
        if (index == -1)
            return input;

        var sb = _stringBuilderPool.Get();
        try
        {
            var currentIndex = 0;
            sb.EnsureCapacity(input.Length + (newValue?.Length ?? 0) - oldValue.Length);

            while (index != -1)
            {
                sb.Append(input, currentIndex, index - currentIndex);
                if (newValue != null)
                {
                    sb.Append(newValue);
                }

                currentIndex = index + oldValue.Length;
                index = input.IndexOf(oldValue, currentIndex, StringComparison.Ordinal);
            }

            sb.Append(input, currentIndex, input.Length - currentIndex);
            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Format strings with minimal allocations using StringBuilder pooling.
    /// </summary>
    /// <param name="format">Format string</param>
    /// <param name="args">Arguments</param>
    /// <returns>Formatted string</returns>
    public static string FormatOptimized(string format, params object?[] args)
    {
        if (string.IsNullOrEmpty(format)) return string.Empty;
        if (args?.Length == 0) return format;

        var sb = _stringBuilderPool.Get();
        try
        {
            sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, format, args ?? Array.Empty<object>());
            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Escapes SQL identifiers efficiently without intermediate string allocations.
    /// </summary>
    /// <param name="identifier">SQL identifier to escape</param>
    /// <returns>Escaped identifier</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string EscapeSqlIdentifier(string identifier)
    {
        if (string.IsNullOrEmpty(identifier))
            return string.Empty;

        // Fast path: if no quotes needed, return as-is
        if (!identifier.Contains('"'))
        {
            return $"\"{identifier}\"";
        }

        // Slow path: escape quotes
        var sb = _stringBuilderPool.Get();
        try
        {
            sb.Append('"');
            foreach (var c in identifier)
            {
                if (c == '"')
                {
                    sb.Append("\"\""); // Escape quotes by doubling
                }
                else
                {
                    sb.Append(c);
                }
            }
            sb.Append('"');
            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Builds parameterized SQL clauses efficiently with proper parameter indexing.
    /// </summary>
    /// <param name="baseIndex">Starting parameter index</param>
    /// <param name="clauses">SQL clauses to combine</param>
    /// <returns>Combined SQL and next parameter index</returns>
    public static (string Sql, int NextParamIndex) BuildParameterizedSql(
        int baseIndex,
        IEnumerable<(string Clause, int ParamCount)> clauses)
    {
        var sb = _stringBuilderPool.Get();
        try
        {
            var paramIndex = baseIndex;

            foreach (var (clause, paramCount) in clauses)
            {
                if (!string.IsNullOrEmpty(clause))
                {
                    // Replace parameter placeholders with actual indices
                    var processedClause = clause;
                    for (int i = 0; i < paramCount; i++)
                    {
                        processedClause = processedClause.Replace($"${i + 1}", $"${paramIndex++}");
                    }

                    if (sb.Length > 0)
                    {
                        sb.Append(' ');
                    }
                    sb.Append(processedClause);
                }
            }

            return (sb.ToString(), paramIndex);
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Efficiently trims and normalizes whitespace in SQL queries.
    /// </summary>
    /// <param name="sql">SQL string to normalize</param>
    /// <returns>Normalized SQL</returns>
    public static string NormalizeSql(string sql)
    {
        if (string.IsNullOrEmpty(sql))
            return string.Empty;

        var chars = _charPool.Rent(sql.Length);
        try
        {
            var writeIndex = 0;
            var lastWasSpace = false;
            var inStringLiteral = false;

            for (int i = 0; i < sql.Length; i++)
            {
                var c = sql[i];

                // Handle string literals
                if (c == '\'' && (i == 0 || sql[i - 1] != '\\'))
                {
                    inStringLiteral = !inStringLiteral;
                    chars[writeIndex++] = c;
                    lastWasSpace = false;
                    continue;
                }

                if (inStringLiteral)
                {
                    chars[writeIndex++] = c;
                    lastWasSpace = false;
                    continue;
                }

                // Normalize whitespace outside string literals
                if (char.IsWhiteSpace(c))
                {
                    if (!lastWasSpace && writeIndex > 0)
                    {
                        chars[writeIndex++] = ' ';
                        lastWasSpace = true;
                    }
                }
                else
                {
                    chars[writeIndex++] = c;
                    lastWasSpace = false;
                }
            }

            // Remove trailing space
            if (writeIndex > 0 && chars[writeIndex - 1] == ' ')
            {
                writeIndex--;
            }

            return new string(chars, 0, writeIndex);
        }
        finally
        {
            _charPool.Return(chars);
        }
    }

    /// <summary>
    /// Efficiently builds comma-separated lists for SQL IN clauses.
    /// </summary>
    /// <param name="values">Values to include in the list</param>
    /// <param name="formatter">Function to format each value</param>
    /// <returns>Comma-separated string</returns>
    public static string BuildCommaSeparatedList<T>(
        IEnumerable<T> values,
        Func<T, string> formatter)
    {
        if (values == null) return string.Empty;

        var sb = _stringBuilderPool.Get();
        try
        {
            bool first = true;
            foreach (var value in values)
            {
                if (!first)
                {
                    sb.Append(", ");
                }
                sb.Append(formatter(value));
                first = false;
            }

            return sb.ToString();
        }
        finally
        {
            _stringBuilderPool.Return(sb);
        }
    }

    /// <summary>
    /// Optimally formats numeric values for SQL queries without culture-specific formatting.
    /// </summary>
    /// <param name="value">Numeric value</param>
    /// <returns>SQL-safe numeric string</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatSqlNumeric(double value)
    {
        // Use invariant culture for consistent SQL formatting
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Optimally formats numeric values for SQL queries without culture-specific formatting.
    /// </summary>
    /// <param name="value">Numeric value</param>
    /// <returns>SQL-safe numeric string</returns>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string FormatSqlNumeric(decimal value)
    {
        // Use invariant culture for consistent SQL formatting
        return value.ToString(System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// Creates a reusable string formatter for hot path operations.
    /// </summary>
    /// <param name="template">String template with {0}, {1}, etc. placeholders</param>
    /// <returns>Optimized formatter function</returns>
    public static Func<object?[], string> CreateOptimizedFormatter(string template)
    {
        if (string.IsNullOrEmpty(template))
            return _ => string.Empty;

        return args =>
        {
            var sb = _stringBuilderPool.Get();
            try
            {
                sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, template, args);
                return sb.ToString();
            }
            finally
            {
                _stringBuilderPool.Return(sb);
            }
        };
    }

    /// <summary>
    /// High-performance string interpolation helper for hot paths.
    /// </summary>
    public ref struct OptimizedStringBuilder
    {
        private StringBuilder _sb;
        private bool _disposed;

        public OptimizedStringBuilder()
        {
            _sb = _stringBuilderPool.Get();
            _disposed = false;
        }

        public OptimizedStringBuilder Append(string? value)
        {
            _sb.Append(value);
            return this;
        }

        public OptimizedStringBuilder Append(char value)
        {
            _sb.Append(value);
            return this;
        }

        public OptimizedStringBuilder Append(int value)
        {
            _sb.Append(value);
            return this;
        }

        public OptimizedStringBuilder Append(double value)
        {
            _sb.Append(value.ToString(System.Globalization.CultureInfo.InvariantCulture));
            return this;
        }

        public OptimizedStringBuilder AppendLine()
        {
            _sb.AppendLine();
            return this;
        }

        public OptimizedStringBuilder AppendLine(string? value)
        {
            _sb.AppendLine(value);
            return this;
        }

        public OptimizedStringBuilder AppendFormat(string format, params object?[] args)
        {
            _sb.AppendFormat(System.Globalization.CultureInfo.InvariantCulture, format, args);
            return this;
        }

        public readonly int Length => _sb.Length;

        public readonly override string ToString()
        {
            return _sb.ToString();
        }

        public void Dispose()
        {
            if (!_disposed)
            {
                _stringBuilderPool.Return(_sb);
                _disposed = true;
            }
        }
    }
}

/// <summary>
/// Cache for commonly used string constants to avoid repeated allocations.
/// </summary>
internal static class StringConstants
{
    // SQL keywords and operators
    internal const string SELECT = "SELECT";
    internal const string FROM = "FROM";
    internal const string WHERE = "WHERE";
    internal const string AND = " AND ";
    internal const string OR = " OR ";
    internal const string ORDER_BY = "ORDER BY";
    internal const string GROUP_BY = "GROUP BY";
    internal const string HAVING = "HAVING";
    internal const string LIMIT = "LIMIT";
    internal const string OFFSET = "OFFSET";

    // Common SQL functions
    internal const string ST_INTERSECTS = "ST_Intersects";
    internal const string ST_WITHIN = "ST_Within";
    internal const string ST_CONTAINS = "ST_Contains";
    internal const string ST_TRANSFORM = "ST_Transform";
    internal const string ST_GEOMFROMTEXT = "ST_GeomFromText";

    // Database column names (commonly used)
    internal const string OBJECTID_COLUMN = "objectid";
    internal const string SHAPE_COLUMN = "shape";
    internal const string LAYERID_COLUMN = "layerid";

    // HTTP headers
    internal const string CONTENT_TYPE = "Content-Type";
    internal const string CORRELATION_ID = "X-Correlation-ID";
    internal const string CACHE_CONTROL = "Cache-Control";

    // Content types
    internal const string APPLICATION_JSON = "application/json";
    internal const string APPLICATION_XML = "application/xml";
    internal const string TEXT_PLAIN = "text/plain";

    // Common format strings for hot paths
    internal static readonly string[] PARAMETER_PLACEHOLDERS =
        Enumerable.Range(1, 100).Select(i => $"${i}").ToArray();

    /// <summary>
    /// Gets a parameter placeholder for the given index (1-based).
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal static string GetParameterPlaceholder(int index)
    {
        return index > 0 && index <= PARAMETER_PLACEHOLDERS.Length
            ? PARAMETER_PLACEHOLDERS[index - 1]
            : $"${index}";
    }
}

/// <summary>
/// Optimized StringBuilder pooled object policy with enhanced configuration.
/// </summary>
internal sealed class StringBuilderPooledObjectPolicy : PooledObjectPolicy<StringBuilder>
{
    private const int DefaultInitialCapacity = 512;
    private const int DefaultMaximumRetainedCapacity = 4096;

    public override StringBuilder Create() => new(DefaultInitialCapacity);

    public override bool Return(StringBuilder obj)
    {
        if (obj.Capacity > DefaultMaximumRetainedCapacity)
        {
            // StringBuilder is too large to pool
            return false;
        }

        obj.Clear();
        return true;
    }
}
