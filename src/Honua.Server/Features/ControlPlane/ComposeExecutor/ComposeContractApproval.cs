// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

namespace Honua.ControlPlane;

// The contract migrations a reviewed release proposal lists, with the one-shot nonce the migration
// runner derives from exactly those scripts. Approving the proposal authorizes these scripts and no
// others: the executor applies them only when they are exactly the candidate's pending contract
// scripts and the nonce matches, so any other plan still needs its own proposal and approval.
internal sealed record ComposeContractApproval
{
    internal const string NonceParameterKey = "deployment.contract_approval.nonce";
    internal const string ScriptsParameterKey = "deployment.contract_approval.scripts";
    internal const int MaxScripts = 64;
    internal const int MaxNonceLength = 256;

    public string? Nonce { get; init; }

    public IReadOnlyList<string>? Scripts { get; init; }

    internal static bool IsParameterKey(string key)
        => key is NonceParameterKey or ScriptsParameterKey;

    // Null when the parameters carry no contract approval at all; otherwise the approval as given,
    // which the caller must check with IsWellFormed.
    internal static ComposeContractApproval? FromParameters(IReadOnlyDictionary<string, string> parameters)
    {
        var hasNonce = parameters.TryGetValue(NonceParameterKey, out var nonce);
        var hasScripts = parameters.TryGetValue(ScriptsParameterKey, out var scripts);
        if (!hasNonce && !hasScripts)
        {
            return null;
        }

        return new ComposeContractApproval
        {
            Nonce = nonce,
            Scripts = scripts?.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        };
    }

    internal static Dictionary<string, string> ToParameters(string nonce, IEnumerable<string> scripts)
        => new(StringComparer.Ordinal)
        {
            [NonceParameterKey] = nonce,
            [ScriptsParameterKey] = string.Join(',', scripts)
        };

    internal static bool IsWellFormedNonce(string? nonce)
        => nonce is { Length: > 0 and <= MaxNonceLength } && nonce.All(char.IsAsciiLetterOrDigit);

    internal static bool AreWellFormedScripts(IReadOnlyList<string>? scripts)
        => scripts is { Count: > 0 and <= MaxScripts }
            && scripts.All(script => ComposeExecutorRules.ScriptName().IsMatch(script))
            && scripts.Distinct(StringComparer.Ordinal).Count() == scripts.Count;

    internal bool IsWellFormed()
        => IsWellFormedNonce(Nonce) && AreWellFormedScripts(Scripts);

    // The runner's nonce is hex and compared case-insensitively; the script set must match exactly.
    internal bool Covers(IReadOnlyList<string> pendingContractScripts, string? pendingNonce)
        => IsWellFormed()
            && pendingNonce is not null
            && string.Equals(pendingNonce, Nonce, StringComparison.OrdinalIgnoreCase)
            && pendingContractScripts.ToHashSet(StringComparer.Ordinal).SetEquals(Scripts!);
}
