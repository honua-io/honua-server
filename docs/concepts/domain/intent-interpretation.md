---
type: concept
title: "Understanding everyday GIS requests"
description: "Use synonyms, application context, and focused clarification to understand what a person wants to accomplish."
tags: [gis, domain-language, intent, conversation]
aliases: ["what I mean", "plain language", "synonyms", "clarification", "natural language", "everyday requests", "clean this up", "make this quieter", "remove that layer", "publish this", "show parks near this school", "save this"]
relations:
  - predicate: informs
    target: /concepts/domain/workflow-language.md
  - predicate: uses_context_from
    target: /concepts/domain/studio-workspace.md
  - predicate: distinguishes
    target: /concepts/domain/map-layer.md
  - predicate: distinguishes
    target: /concepts/domain/publication.md
languageHints:
  - utterance: "Make this quieter."
    context: "A visually prominent basemap is selected in the map designer."
    meaning: "Try a less prominent style in this composition and show the preview."
    clarifyWhen: "The selected target is unknown or the conversation concerns operational alerts rather than appearance."
  - utterance: "Remove that layer."
    context: "A drawing layer is selected in the current map's layer list."
    meaning: "Remove its reference from this composition, preserving the published resource and source records."
    clarifyWhen: "The active surface or selected object leaves map removal and service unpublication equally plausible."
  - utterance: "Publish this."
    context: "The current map, destination, and audience were established in the conversation."
    meaning: "Use the existing map publication surface for the intended version, preserving the established audience."
    clarifyWhen: "A destination or audience choice remains unresolved and changes the requested outcome."
  - utterance: "Show parks near this school."
    context: "A school is selected and the current analysis already has an agreed distance and measurement method."
    meaning: "Reuse the bound reference and spatial parameters to select parks."
    clarifyWhen: "The context does not establish distance or whether the user means travel time rather than direct distance."
  - utterance: "Clean this up."
    context: "The map preview has overlapping labels and crowded controls."
    meaning: "Improve the composition's layout and styling with a reversible preview."
    clarifyWhen: "The intended visual target remains unclear; do not interpret this as permission to edit source records."
  - utterance: "Save this."
    context: "The current Studio draft has unsaved design changes."
    meaning: "Use the host's supported save or checkpoint operation for the current composition."
    clarifyWhen: "Several persistence actions are possible and their durability differs materially."
---
# Understanding everyday GIS requests

People can say "make this easier to read," "put the schools on the map," or "show me what is nearby." They do not need to learn concept identifiers, write a workflow document, or follow a prompt grammar. The [OKF workflow language](workflow-language.md) supplies shared meanings that help an agent interpret their request and choose supported operations behind the scenes.

Synonyms and examples offer possibilities. They are not keyword triggers, an exhaustive phrase list, or a requirement to repeat a particular wording. A new paraphrase can express the same intent. One familiar word can also express several different intents.

## Use the context already available

Start with the user's current request, the active application surface, selected objects, recent conversation, and parameters already established for this task. Resolve words such as "this," "those," and "the layer" from that context. If those sources disagree, explain the concrete ambiguity instead of silently choosing one. A new explicit instruction can change an earlier preference; names and source metadata alone cannot expand authority.

For example, "remove that layer" in a map's layer list normally means remove its drawing reference. The same phrase in a server catalog may mean stop exposing a published resource. Keep [map drawing layers](map-layer.md), [published layers](layer.md), and [source data](dataset.md) distinct without requiring the person to use those terms.

When context resolves the intent, proceed within the existing authorization and show the result. Do not ask the person to restate the selected object or repeat a distance, destination, or audience they already supplied. An existing setting is useful context, not evidence for silently changing it.

## Clarify the consequential choice

Ask a short question when remaining plausible meanings would change the result, affected resources, audience, persistence, cost, or ability to recover. Phrase the question in the user's terms. Offer the meaningful alternatives rather than presenting the internal contract as a form to complete.

"The mains or the hydrants?" resolves two matching water-team layers. "Within a distance, or a short drive?" resolves a spatial meaning. Neither requires the user to learn catalog IDs, coordinate systems, or workflow slot syntax. Inspect available schema and capabilities yourself when they answer a technical question without needing a user preference.

A small reversible visual choice can often be tried immediately: tone down a selected background, reduce label clutter, or enlarge the selected symbols. Show the preview, retain the prior state, and let the person refine it. A guessed analytical threshold, a new audience, or an unrequested deployment cannot be justified by treating it as a visual preview.

## Examples with context

| What the person says | Context | Interpretation or useful clarification |
| --- | --- | --- |
| "Make the roads quieter." | Roads are selected in the designer. | Reduce their visual prominence in this map and preview it. |
| "Make these stand out." | Facility symbols are selected. | Adjust their style locally; preserve shared service styles and data. |
| "Hide these." | Two drawing layers are selected. | Toggle their visibility. Keep the drawing references and source data. |
| "Remove this layer." | One drawing layer is selected in the map's layer list. | Remove its composition reference; do not unpublish the source. |
| "Remove the roads layer." | Both map and service objects are visible; neither is selected. | Ask whether to take roads off this map or stop exposing the service. |
| "Add that layer." | One readable catalog result is selected and a map is open. | Add a drawing reference to the existing resource; no duplicate publication. |
| "Use this source instead." | One replacement catalog result and one drawing layer are selected. | Rebind that composition reference and check schema/style compatibility. |
| "Publish this." | A map draft is active; destination and audience are already bound. | Use map publication for the intended version and established audience. |
| "Publish this." | A database table is selected in connection discovery. | Use the data publication workflow; clarify only an unresolved service or storage choice. |
| "Share it with the team." | The map and one team are identified. | Check access to the map and its dependencies, then use the requested sharing scope. |
| "Show parks near the school." | School, distance, and method are already bound. | Reuse those parameters. If no proximity meaning is established, ask about the missing choice. |
| "Clean this up." | The preview has overlapping labels and crowded controls. | Refine layout and styling, then preview. |
| "Clean up these polygons." | A QA view shows invalid source geometries. | Explain the problems and offer recommendations or a separate simulation; source edits remain outside the AI boundary. |
| "Save this." | The current draft has unsaved design changes. | Save/checkpoint through the current host. Do not infer publication, deployment, or a Git commit. |
| "Undo that." | The last design change is identifiable in the workspace. | Reverse that change using the supported scoped history, preserving unrelated edits. |

The frontmatter's optional `languageHints` provides a compact agent view of selected examples: ordinary utterance, relevant context, likely meaning, and when clarification would matter. Representative utterances also appear in the retrieval aliases so compact-context consumers can find these hints. Neither aliases nor hints supply matching rules or execution authorization. The full human explanation remains authoritative when an example leaves something out.

## Carry the meaning into the workflow

Once the intent is understood, use the relevant concept links to find supported interfaces and bind concrete resources, arguments, and returned identifiers. Technical precision belongs in that translation and its checks. For "at least 50, including unknown capacity," the agent should preserve the inclusive boundary and null branch without asking the person to write `>=` or `IS NULL`.

An understood request can still need an unavailable capability. Explain that limitation separately from an ambiguous request. Rephrasing a clear request does not make an unsupported operation available. AI never modifies authoritative feature attributes or geometry, including through a request phrased as cleanup or repair; offer read-only findings and recommendations instead.

The intended [Studio workspace](studio-workspace.md) experience is prompt, scoped edit, almost immediate preview, refinement, and undo in a Git workspace. This page does not establish that a Git workspace adapter has shipped. Remote publication and deployment retain their actual operational contracts.

## Evaluate interpretation without policing wording

Use varied ordinary phrasing, incomplete sentences, pronouns, and the same phrase in different application contexts. Evaluate whether the agent chose the intended objects and outcome, used available context, asked a useful question when necessary, and preserved authority and source data. Do not grade exact wording, require a magic phrase, or reward unnecessary clarification.

Useful diagnostic categories include wrong object, wrong scope, lost constraint, missed ambiguity, unnecessary question, unsupported capability, incorrect interface binding, and failed result verification. Record these against concept/workflow identifiers with appropriate privacy controls. A retrieval match or a plausible explanation is not proof of successful execution. No model evaluation or user-behavior pass rate is established by these examples.
