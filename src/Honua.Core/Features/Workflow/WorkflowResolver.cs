// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Honua.Core.Features.Workflow;

/// <summary>A vocabulary resolution request. Allowed terms must come from server authorization.</summary>
public sealed record WorkflowResolveRequest(string Text, string[] AuthorizedTermIds, string? Context = null, string? ClarificationId = null, string? Choice = null);
/// <summary>Candidate meanings are descriptive, never permission to execute.</summary>
public sealed record WorkflowCandidate(string TermId, string Phrase);
/// <summary>A bounded choice of meanings for this turn.</summary>
public sealed record WorkflowClarification(string Id, string Question, string[] Choices);
/// <summary>Status is candidate, clarification, or no-match; never authorized.</summary>
public sealed record WorkflowResolution(string Status, WorkflowCandidate[] Candidates, WorkflowClarification? Clarification);
/// <summary>A suggested next operation with prerequisite and executor blockers.</summary>
public sealed record WorkflowPathway(string Operation, string[] MissingPrerequisites, string? Blocker);
/// <summary>Structured alias declaration.</summary>
public sealed record WorkflowAlias(string TermId, string Text, bool Ambiguous, string? Clarification, string[] Contexts);
/// <summary>Portable, generated vocabulary and grammar bundle.</summary>
public sealed class WorkflowBundle
{
    /// <summary>Exact version used to pin reviews.</summary>
    public string BundleVersion { get; }
    /// <summary>Immutable source JSON for transport to clients.</summary>
    public string Json { get; }
    internal Dictionary<string, WorkflowAlias[]> Aliases { get; }
    internal Dictionary<string, WorkflowClarification> Clarifications { get; }

    private WorkflowBundle(string json)
    {
        Json = json;
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        BundleVersion = root.GetProperty("bundleVersion").GetString()!;
        Aliases = root.GetProperty("aliasIndex").Deserialize(WorkflowJsonContext.Default.DictionaryStringWorkflowAliasArray)!;
        Clarifications = root.GetProperty("vocabulary").GetProperty("clarifications")
            .Deserialize(WorkflowJsonContext.Default.WorkflowClarificationArray)!.ToDictionary(c => c.Id, StringComparer.Ordinal);
    }

    /// <summary>Load the build-validated embedded bundle.</summary>
    public static WorkflowBundle LoadDefault()
    {
        using var stream = typeof(WorkflowBundle).Assembly.GetManifestResourceStream("Honua.Workflow.bundle.v0.1.json")
            ?? throw new InvalidOperationException("Workflow bundle is missing.");
        using var reader = new StreamReader(stream);
        return new WorkflowBundle(reader.ReadToEnd());
    }

    /// <summary>Filter templates by server-derived capability/edition grants, permission, and object state.</summary>
    public WorkflowPathway[] SuggestPathways(string[] availableObjects, string[] authorizedOperations, string[] entitledCapabilities)
    {
        using var doc = JsonDocument.Parse(Json);
        var objects = availableObjects.ToHashSet(StringComparer.Ordinal);
        var permissions = authorizedOperations.ToHashSet(StringComparer.Ordinal);
        var capabilities = entitledCapabilities.ToHashSet(StringComparer.Ordinal);
        var results = new List<WorkflowPathway>();
        foreach (var pathway in doc.RootElement.GetProperty("pathways").EnumerateArray())
        {
            var operation = pathway.GetProperty("operation").GetString()!;
            var capability = pathway.GetProperty("capability").GetString();
            if (!permissions.Contains(operation) || (capability != null && !capabilities.Contains(capability))) continue;
            var missing = pathway.GetProperty("requires").EnumerateArray().Select(p => p.GetString()!).Where(p => !objects.Contains(p)).ToArray();
            if (missing.Length > 1) continue;
            results.Add(new(operation, missing, pathway.GetProperty("executor").GetString() == "unavailable" ? "executor-unavailable" : null));
        }
        return results.ToArray();
    }

    /// <summary>Classify by actual target; undeclared target combinations fail closed.</summary>
    public string GetEffectTier(string operation, string target)
    {
        using var doc = JsonDocument.Parse(Json);
        foreach (var op in doc.RootElement.GetProperty("grammar").GetProperty("operations").EnumerateArray())
        {
            if (op.GetProperty("id").GetString() != operation) continue;
            var effects = op.GetProperty("effectByTarget");
            if (effects.TryGetProperty(target, out var tier) || effects.TryGetProperty("default", out tier)) return tier.GetString()!;
            throw new ArgumentException("Target has no declared effect tier.", nameof(target));
        }
        throw new ArgumentException("Unknown workflow operation.", nameof(operation));
    }
}

/// <summary>Find longest phrase candidates without granting authority or choosing an executor.</summary>
public sealed partial class WorkflowResolver(WorkflowBundle bundle)
{
    /// <summary>Resolve using authorized context and an optional explicit turn clarification.</summary>
    public WorkflowResolution Resolve(WorkflowResolveRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var text = Normalize(request.Text);
        var allowed = request.AuthorizedTermIds.ToHashSet(StringComparer.Ordinal);
        var matches = bundle.Aliases.Where(pair => (" " + text + " ").Contains(" " + pair.Key + " ", StringComparison.Ordinal))
            .OrderByDescending(pair => pair.Key.Split(' ').Length).ThenByDescending(pair => pair.Key.Length).ToArray();
        if (matches.Length == 0) return new("no-match", [], null);
        // Select the lexical span before filtering grants: a shorter authorized phrase cannot replace it.
        var longest = matches[0];
        var meanings = longest.Value.Where(a => allowed.Contains(a.TermId) && (a.Contexts.Length == 0 || a.Contexts.Contains(request.Context, StringComparer.Ordinal))).ToArray();
        if (meanings.Length == 0) return new("no-match", [], null);
        var candidates = meanings.Select(a => new WorkflowCandidate(a.TermId, longest.Key)).ToArray();
        var clarificationId = meanings.FirstOrDefault(a => a.Ambiguous)?.Clarification;
        if (clarificationId != null)
        {
            var clarification = bundle.Clarifications[clarificationId];
            if (request.ClarificationId == clarificationId && request.Choice != null && meanings.Any(a => a.TermId == request.Choice))
                return new("candidate", [new(request.Choice, longest.Key)], null);
            return new("clarification", candidates, clarification with { Choices = clarification.Choices.Where(allowed.Contains).ToArray() });
        }
        return new("candidate", candidates, null);
    }

    private static string Normalize(string text) => WordSeparator().Replace(text.ToLowerInvariant(), " ").Trim();
    [GeneratedRegex(@"[^\p{L}\p{N}._-]+", RegexOptions.CultureInvariant)]
    private static partial Regex WordSeparator();
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(Dictionary<string, WorkflowAlias[]>))]
[JsonSerializable(typeof(WorkflowClarification[]))]
internal sealed partial class WorkflowJsonContext : JsonSerializerContext;
