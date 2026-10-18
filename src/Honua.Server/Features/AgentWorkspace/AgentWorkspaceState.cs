// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

namespace Honua.Server.Features.AgentWorkspace;

/// <summary>Versioned workflow transitions independent of transport and persistence.</summary>
internal static class AgentWorkspaceState
{
    private static readonly HashSet<string> PublishFields = new(StringComparer.Ordinal)
    {
        "connectionId", "schema", "table", "serviceName", "layerName",
    };

    public static bool IsIdentifier(string? value)
        => value is { Length: > 0 and <= 128 } && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');

    public static bool CanRead(AgentWorkspaceDocument document, string actorId, string? tenantId, string targetId)
        => document.TargetId == targetId && document.TenantId == tenantId && document.Members.Contains(actorId, StringComparer.Ordinal);

    public static AgentWorkspaceContext Current(AgentWorkspaceDocument document, string viewId)
        => document.Views.TryGetValue(viewId, out var history)
            ? history[^1]
            : new(document.WorkspaceId, document.TargetId, viewId, 0, [], document.Task, document.TenantId);

    public static AgentWorkspaceContext Capture(AgentWorkspaceDocument document, string viewId, long revision)
    {
        // Navigation after submission cannot retarget a prompt. Retain recent immutable view
        // revisions, rather than requiring the mutable current selection to still match.
        var captured = document.Views.TryGetValue(viewId, out var history)
            ? history.SingleOrDefault(context => context.Revision == revision)
            : null;
        return captured ?? throw new AgentWorkspaceConflictException("The submitted context revision is no longer available.");
    }

    public static AgentWorkspaceDocument Update(AgentWorkspaceDocument document, UpdateAgentContext request)
    {
        if (!IsIdentifier(request.ViewId) || request.TargetId != document.TargetId ||
            request.SelectedResources is null || request.SelectedResources.Length > 32 ||
            request.SelectedResources.Any(r => r is null || r.Kind != "layer" || !IsIdentifier(r.Id) ||
                (r.ServiceId is not null && (string.IsNullOrWhiteSpace(r.ServiceId) || r.ServiceId.Length > 256 || r.ServiceId.Any(char.IsControl)))))
        {
            throw new ArgumentException("Invalid typed workspace context.");
        }
        var current = Current(document, request.ViewId);
        if (current.Revision != request.ExpectedRevision)
        {
            throw new AgentWorkspaceConflictException("The view changed. Read its current revision before editing.");
        }
        if (request.Task is not null && (!IsValidTaskReference(request.Task) ||
                request.Task.Kind == "publish-layer" && (!document.Drafts.TryGetValue(request.Task.Id, out var draft) || draft.Revision != request.Task.Revision)))
        {
            throw new AgentWorkspaceConflictException("The shared task changed.");
        }
        if (!document.Views.ContainsKey(request.ViewId) && document.Views.Count >= 32)
        {
            throw new ArgumentException("Workspace view limit reached.");
        }
        var next = current with { Revision = current.Revision + 1, SelectedResources = request.SelectedResources.ToArray(), Task = request.Task };
        var views = new Dictionary<string, AgentWorkspaceContext[]>(document.Views, StringComparer.Ordinal);
        var history = views.GetValueOrDefault(request.ViewId) ?? [current];
        views[request.ViewId] = [.. history.TakeLast(127), next];
        if (request.Task != document.Task)
        {
            foreach (var (viewId, otherHistory) in document.Views)
            {
                if (viewId == request.ViewId) continue;
                var previous = otherHistory[^1];
                views[viewId] = [.. otherHistory.TakeLast(127), previous with { Revision = previous.Revision + 1, Task = request.Task }];
            }
        }
        return document with { Views = views, Task = request.Task };
    }

    public static AgentWorkspaceDocument AddTurn(AgentWorkspaceDocument document, string actorId, CreateAgentTurn request)
    {
        if (!IsIdentifier(request.TurnId) || !IsIdentifier(request.ViewId) || string.IsNullOrWhiteSpace(request.Prompt) || request.Prompt.Length > 16000)
        {
            throw new ArgumentException("A bounded prompt, turn id and view id are required.");
        }
        if (document.Turns.TryGetValue(request.TurnId, out var existing))
        {
            if (existing.ActorId != actorId || existing.Prompt != request.Prompt ||
                existing.Context.ViewId != request.ViewId || existing.Context.Revision != request.ContextRevision)
            {
                throw new AgentWorkspaceConflictException("The turn id is already bound to different input.");
            }
            return document;
        }
        if (document.Turns.Count >= 256)
        {
            throw new ArgumentException("Workspace turn limit reached. Start another workspace.");
        }
        var context = Capture(document, request.ViewId, request.ContextRevision);
        AgentWorkspaceDraft? draft = null;
        if (context.Task is { Kind: "publish-layer" } && (!document.Drafts.TryGetValue(context.Task.Id, out draft) || draft.Revision != context.Task.Revision))
        {
            throw new AgentWorkspaceConflictException("The submitted task revision changed. Review the current draft before sending.");
        }
        var turns = new Dictionary<string, AgentWorkspaceTurn>(document.Turns, StringComparer.Ordinal)
        {
            [request.TurnId] = new(request.TurnId, actorId, request.Prompt, context, draft),
        };
        return document with
        {
            Turns = turns,
            Messages = [.. document.Messages, new($"{request.TurnId}-user", request.TurnId, "user", request.Prompt)],
        };
    }

    public static bool IsValidTaskReference(AgentTaskReference task)
        => task.Revision >= 0 && (task.Kind switch
        {
            "publish-layer" => IsIdentifier(task.Id) && task.PackageKey is null && task.ContentHash is null,
            "studio-composition" => Guid.TryParse(task.Id, out _) && task.PackageKey is { Length: > 0 and <= 256 } && !task.PackageKey.Any(char.IsControl) && task.ContentHash is null,
            "local-process-workspace" => task.Id is { Length: 32 } && task.Id.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f') &&
                task.Revision > 0 && task.PackageKey is null && task.ContentHash is { Length: 64 } &&
                task.ContentHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            // Package ids are opaque store keys; generated slugs can contain Unicode.
            "workflow-package" => task.Id is { Length: > 0 and <= 256 } && task.Id == task.Id.Trim() && !task.Id.Any(char.IsControl) &&
                task.Revision is > 0 and <= int.MaxValue && task.PackageKey is null &&
                task.ContentHash is { Length: 64 } && task.ContentHash.All(c => c is >= '0' and <= '9' or >= 'a' and <= 'f'),
            _ => false,
        });

    public static AgentWorkspaceDocument PatchDraft(AgentWorkspaceDocument document, string draftId, PatchAgentDraft request)
    {
        if (!IsIdentifier(request.ViewId)) throw new ArgumentException("A valid view id is required.");
        if (!document.Drafts.TryGetValue(draftId, out var draft)) throw new AgentWorkspaceNotFoundException();
        if (draft.Revision != request.ExpectedRevision) throw new AgentWorkspaceConflictException("The shared draft changed.");
        var fields = new Dictionary<string, string>(draft.Fields, StringComparer.Ordinal);
        foreach (var (field, value) in ValidateFields(request.Changes)) fields[field] = value;
        draft = draft with { Revision = draft.Revision + 1, Fields = fields };
        var drafts = new Dictionary<string, AgentWorkspaceDraft>(document.Drafts, StringComparer.Ordinal) { [draft.Id] = draft };
        var next = document with { Drafts = drafts };
        // Completing an older turn may edit its own draft, but cannot reclaim the
        // user's focus after they have selected another task (or cleared it).
        if (document.Task?.Id != draft.Id) return next;
        var current = Current(document, request.ViewId);
        return Update(next, new(request.ViewId, current.Revision, document.TargetId,
            current.SelectedResources, new(draft.Id, draft.Revision), "workflow"));
    }

    public static Dictionary<string, string> ValidateFields(Dictionary<string, string>? fields)
    {
        if (fields is null || fields.Count > PublishFields.Count || fields.Any(pair =>
                !PublishFields.Contains(pair.Key) || pair.Value is null || pair.Value.Length > 256 || pair.Value.Any(char.IsControl)))
        {
            throw new ArgumentException("Only bounded publish-layer fields may be shared. Supply credentials through the secret channel.");
        }
        return new(fields, StringComparer.Ordinal);
    }
}
internal sealed class AgentWorkspaceConflictException(string message) : Exception(message);
