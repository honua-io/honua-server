---
type: guide
title: "How to approve an agent-proposed change"
description: "Review an exact proposal in a human Console session and follow it to completion."
---
# How to approve an agent-proposed change

The rc.2 approval model requires **one human decision** for an agent-initiated
change in every edition. The requesting person may make that decision; a
second person is not required by this model. The agent cannot approve on your
behalf, and typing “yes” in a conversation is not a recorded server approval.

**Availability:** the human-session enforcement and requester-approval changes
are awaiting integration and a matching candidate. Older deployments can still
reject approval by the requester or accept credential types that the new model
rejects. Upgrade the server and Console together before relying on the new
boundary. This guide describes the target behavior, not certification of an
older deployment.

## Review and approve in Console

You need an interactive sign-in to the correct Console environment and the
approval grant for the proposed action. Read or edit access alone does not
grant approval authority.

1. Keep the operation and proposal references returned by the agent. A
   submitted or awaiting-approval result means the change has not completed.
2. Sign in to Console as yourself and open **Approvals** (`/inbox`), or follow
   the proposal's **Open approval** link when one is supplied.
3. Select that proposal. Review its target environment, resource, requested
   change, warnings, and exact saved version or revision. Check that it is the
   action you intended, rather than a newer edit or another user's request.
4. Choose **Approve** only if the displayed change is acceptable. Choose
   **Reject** and give a reason if it is not. If the human verification has
   expired, sign in again before making the decision.
5. Follow the same operation to its terminal result and read back the changed
   resource. Approval authorizes execution; it does not prove that execution
   succeeded. Keep a failed or cancelled result with the original reference.

For publication, also check the returned viewer link and the saved version it
serves. A public map document does not automatically make its data dependencies
public. See [publish and share a saved map](../workflows/publish-share-map.md).

## When the request starts in MCP

Have the MCP agent submit the supported proposal and report its returned
identity and status. Make the human decision in Console, then let the agent
observe that same proposal. Do not give it an approval API key or ask it to
impersonate your Console session.

In-client approval through MCP Multi Round-Trip Requests (MRTR) is **deferred
to rc.3**. An MCP client's local tool-consent dialog is not proof of a Honua
approval. For rc.2, use the interactive Console approval channel.

## Migrate an automated approval setup

API keys and agent bearer credentials cannot approve under the new model,
even if they have an approval-related scope. Keep automation responsible for
submission and observation, and route the pending proposal to a person signed
in to Console. Existing administrative grants still apply to that person.

Do not replace a refused approval with a direct mutation or a new proposal to
evade policy. If the resource changed, review a fresh proposal for the new
revision rather than transferring the old decision.

## Troubleshooting

- **Approval denied:** verify the environment, interactive sign-in, and approval
  grant. Adding scopes to an API key does not create a human session.
- **Requester approval refused:** check that the installation includes the
  one-human model. Older separation-of-duties behavior needs the coordinated
  upgrade; do not create a second identity as a workaround.
- **The proposal is stale, rejected or expired:** retain its outcome. Submit a
  fresh request only after reviewing the current resource and intended action.
- **Approved but not completed:** inspect the operation's failure or pending
  state before retrying. Do not report success from the decision alone.

Related: [authentication](authentication.md), [access control](access-control.md),
and [AI agents over MCP](../connect/ai-agents-mcp.md).
