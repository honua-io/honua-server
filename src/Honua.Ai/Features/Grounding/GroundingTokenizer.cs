// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace Honua.Ai.Grounding;

/// <summary>
/// Deterministic, dependency-free tokenizer for grounding input text.
/// Lowercases, strips punctuation, drops a built-in English stopword list,
/// applies a tiny set of hand-coded stem rules, and de-duplicates tokens.
///
/// Kept intentionally small — the eval harness (honua-server-734) pins the
/// token set, so any change here needs paired fixture updates. A bag-of-lemma
/// representation is enough for the 20-process catalog and a few-hundred
/// layers; when catalog size grows beyond that, swap in an embeddings
/// reranker behind <c>IGroundingEngine</c>.
/// </summary>
internal static partial class GroundingTokenizer
{
    private static readonly FrozenSet<string> Stopwords =
        new HashSet<string>(StringComparer.Ordinal)
        {
            "a", "an", "the", "and", "or", "but", "if", "of", "to", "in",
            "on", "at", "by", "for", "with", "from", "into", "over", "under",
            "as", "that", "this", "these", "those", "is", "are", "was", "were",
            "be", "been", "being", "it", "its", "my", "our", "your", "their",
            "do", "does", "did", "has", "have", "had", "i", "we", "you", "they",
            "me", "us", "he", "she", "them", "so", "than", "then", "such", "via",
            "per", "up", "down", "out", "about", "any", "all", "each", "some",
            "no", "not", "only", "own", "same", "very", "can", "will", "just",
            "let", "lets", "me", "please",
        }.ToFrozenSet(StringComparer.Ordinal);

    [GeneratedRegex(@"[a-z0-9]+", RegexOptions.CultureInvariant)]
    private static partial Regex TokenPattern();

    /// <summary>
    /// Returns the deterministic bag of lemma tokens for <paramref name="text"/>.
    /// Duplicates are collapsed, stopwords are dropped, and the order follows
    /// first-occurrence in the original text.
    /// </summary>
    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        var lowered = text.ToLowerInvariant();
        var matches = TokenPattern().Matches(lowered);
        if (matches.Count == 0)
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<string>(matches.Count);

        foreach (Match match in matches)
        {
            var raw = match.Value;
            if (raw.Length == 1)
            {
                // Single letters rarely carry meaning; drop them so noise
                // words like "a"/"i"/"s" do not leak past the stopword list.
                continue;
            }

            if (Stopwords.Contains(raw))
            {
                continue;
            }

            var stem = Stem(raw);
            if (seen.Add(stem))
            {
                result.Add(stem);
            }
        }

        return result;
    }

    /// <summary>
    /// Tokenizes text and returns the set of tokens (unordered). Convenience
    /// for join-style overlap counts where order is irrelevant.
    /// </summary>
    public static FrozenSet<string> TokenizeToSet(string? text)
    {
        var tokens = Tokenize(text);
        return tokens.Count == 0
            ? FrozenSet<string>.Empty
            : tokens.ToFrozenSet(StringComparer.Ordinal);
    }

    private static string Stem(string token)
    {
        // Hand-coded stem rules deliberately narrower than a full Porter
        // stemmer: only the suffixes that collapse common geospatial verbs
        // ("buffering" → "buffer") without over-aggressive rewrites.
        if (token.Length <= 4)
        {
            return token;
        }

        if (token.EndsWith("ies", StringComparison.Ordinal))
        {
            return token[..^3] + "y";
        }

        if (token.EndsWith("ing", StringComparison.Ordinal))
        {
            return token[..^3];
        }

        if (token.EndsWith("ed", StringComparison.Ordinal))
        {
            return token[..^2];
        }

        if (token.EndsWith("es", StringComparison.Ordinal))
        {
            return token[..^2];
        }

        if (token.EndsWith('s'))
        {
            return token[..^1];
        }

        return token;
    }
}
