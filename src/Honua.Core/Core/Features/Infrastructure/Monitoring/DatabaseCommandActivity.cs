// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Diagnostics;
using System.Globalization;
using QueryExecutionData = Honua.Core.Features.Infrastructure.Monitoring.DatabaseQueryPerformanceMonitor.QueryExecutionData;

namespace Honua.Core.Features.Infrastructure.Monitoring;

// Reads one completed database command from the OpenTelemetry database client
// attributes that Npgsql and MySqlConnector put on their command activities.
internal static class DatabaseCommandActivity
{
    private const string FallbackQueryType = "COMMAND";
    private const int MaxQueryTypeLength = 32;

    private static readonly string[] OperationTags = ["db.operation.name", "db.operation"];
    private static readonly string[] QueryTextTags = ["db.query.text", "db.statement"];
    private static readonly string[] RowCountTags = ["db.npgsql.rows", "db.response.returned_rows"];

    // Null when the activity is not a command, for example a connection open, which
    // carries no operation or query text.
    public static QueryExecutionData? TryRead(Activity activity)
    {
        var operation = FirstTag(activity, OperationTags);
        var queryText = FirstTag(activity, QueryTextTags);
        var storedProcedure = activity.GetTagItem("db.stored_procedure.name") as string;
        if (operation is null && queryText is null && storedProcedure is null)
            return null;

        return new QueryExecutionData
        {
            QueryType = QueryType(operation, queryText, storedProcedure),
            CorrelationId = activity.TraceId.ToHexString(),
            ExecutionTimeMs = activity.Duration.TotalMilliseconds,
            Success = activity.Status != ActivityStatusCode.Error && activity.GetTagItem("error.type") is null,
            RowCount = RowCount(activity),
            ExecutedAt = activity.StartTimeUtc,
            QueryText = queryText,
        };
    }

    private static string QueryType(string? operation, string? queryText, string? storedProcedure)
    {
        if (!string.IsNullOrWhiteSpace(operation))
            return Normalize(operation);
        if (storedProcedure is not null)
            return "CALL";
        return queryText is null ? FallbackQueryType : Normalize(LeadingKeyword(queryText));
    }

    private static string LeadingKeyword(string queryText)
    {
        var text = queryText.AsSpan().TrimStart();
        var end = 0;
        while (end < text.Length && char.IsAsciiLetter(text[end]))
            end++;
        return end == 0 ? FallbackQueryType : text[..end].ToString();
    }

    private static string Normalize(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.Length > MaxQueryTypeLength)
            trimmed = trimmed[..MaxQueryTypeLength];
        return trimmed.Length == 0 ? FallbackQueryType : trimmed.ToUpperInvariant();
    }

    private static string? FirstTag(Activity activity, string[] names)
    {
        foreach (var name in names)
        {
            if (activity.GetTagItem(name) is string { Length: > 0 } value)
                return value;
        }

        return null;
    }

    private static long? RowCount(Activity activity)
    {
        foreach (var name in RowCountTags)
        {
            switch (activity.GetTagItem(name))
            {
                case long rows:
                    return rows;
                case int rows:
                    return rows;
                case string text when long.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var rows):
                    return rows;
            }
        }

        return null;
    }
}
