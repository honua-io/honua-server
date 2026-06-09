// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;

namespace Honua.Migration;

internal static class ImportValidationErrorMessage
{
    public static string FromArgument(ArgumentException exception, string fallback)
    {
        var message = string.IsNullOrWhiteSpace(exception.Message)
            ? fallback
            : exception.Message;

        return RedactUrls(message);
    }

    private static string RedactUrls(string message)
    {
        const string RedactedUrl = "[redacted-url]";

        var builder = new StringBuilder(message.Length);
        var index = 0;
        while (index < message.Length)
        {
            var httpIndex = message.IndexOf("http://", index, StringComparison.OrdinalIgnoreCase);
            var httpsIndex = message.IndexOf("https://", index, StringComparison.OrdinalIgnoreCase);
            var nextUrlIndex = SelectNextUrlIndex(httpIndex, httpsIndex);
            if (nextUrlIndex < 0)
            {
                builder.Append(message, index, message.Length - index);
                break;
            }

            builder.Append(message, index, nextUrlIndex - index);
            builder.Append(RedactedUrl);

            index = nextUrlIndex;
            while (index < message.Length && !IsUrlTerminator(message[index]))
            {
                index++;
            }
        }

        return builder.ToString();
    }

    private static int SelectNextUrlIndex(int httpIndex, int httpsIndex)
        => (httpIndex, httpsIndex) switch
        {
            (< 0, < 0) => -1,
            (< 0, _) => httpsIndex,
            (_, < 0) => httpIndex,
            _ => Math.Min(httpIndex, httpsIndex)
        };

    private static bool IsUrlTerminator(char character)
        => char.IsWhiteSpace(character)
           || character is '"' or '\'' or '<' or '>' or ')' or ']' or '}';
}
