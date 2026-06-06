// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Text;
using Honua.Core.Features.Forms.Packages;

namespace Honua.Ai.FormGeneration;

/// <summary>
/// Builds the grounded system/user prompts for form generation. Mirrors
/// <c>WorkflowGenerationPrompt</c>: a lean, deterministic prompt that enumerates the supported
/// field-type vocabulary and the form-document contract so a local model proposes only valid forms.
/// </summary>
internal static class FormGenerationPrompt
{
    /// <summary>Supported form field types (mirrors FormPackageValidator.IsCompatibleFieldType).</summary>
    internal static readonly string[] FieldTypes =
    [
        "text", "email", "barcode", "choice", "integer", "number", "boolean",
        "date", "datetime", "uuid", "json", "attachment"
    ];

    public static string BuildSystem(FormGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are Honua's form author. You convert a natural-language request into a validated survey-style form.package document for a geospatial feature layer.");
        sb.AppendLine();
        sb.AppendLine("Rules:");
        sb.AppendLine("- ALWAYS set a concise form title and a target. The target has serviceId (a short descriptive service name; use \"placeholder\" if the request names no concrete service) and layerId (an integer; use 0 if unknown). Both title and target are required.");
        sb.AppendLine("- Produce a form that captures attributes for a single target feature layer (target.serviceId + target.layerId).");
        sb.AppendLine("- Each field has a unique fieldId (a short handle YOU invent, e.g. \"f_name\"), a label, and a type from the allowed list below. Never invent a field type.");
        sb.AppendLine("- Bind each non-attachment field to a layer attribute via targetField (the column name). Attachment fields have no targetField.");
        sb.AppendLine("- Group fields into sections; each section lists the fieldIds it contains. Every field's sectionId must reference an existing section.");
        sb.AppendLine("- Use domain { type: \"codedValue\", choices: [{ code, label }] } for pick-lists, or { type: \"range\", min, max } for bounded numbers.");
        sb.AppendLine("- Use validation rules (types: minLength, maxLength, min, max, regex) where the request implies constraints.");
        sb.AppendLine("- Use visibility { dependsOnFieldId, operator, value } (operators: equals, notEquals, gt, gte, lt, lte, isEmpty, isNotEmpty, in) for conditional fields.");
        sb.AppendLine("- The target service/layer is bound by the operator at publish time; if the request does not name a concrete layer, still propose the fields and use a descriptive placeholder serviceId/layerId.");
        sb.AppendLine("- If the request is ambiguous (which layer, which fields), return status \"needs-clarification\" with clarifications instead of guessing.");
        sb.AppendLine("- If the request is not about building a data-capture form, return status \"refused\" with a short rationale.");
        sb.AppendLine("- Otherwise return status \"generated\" with the form and a one-paragraph rationale.");
        sb.AppendLine("- Do not set submitPolicy/attachmentPolicy/privacyPolicy/offlinePolicy; the server applies safe defaults.");
        sb.AppendLine();

        if (request.RepairFailures is { Count: > 0 })
        {
            sb.AppendLine("Your previous form failed server validation. Fix these failures and return a corrected form:");
            foreach (var failure in request.RepairFailures)
            {
                sb.Append("- [").Append(failure.Code).Append("] ").Append(failure.Message);
                if (!string.IsNullOrWhiteSpace(failure.Path))
                {
                    sb.Append(" (at ").Append(failure.Path).Append(')');
                }

                sb.AppendLine();
            }

            sb.AppendLine();
        }

        sb.Append("Allowed field types: ").AppendLine(string.Join(", ", FieldTypes));
        return sb.ToString();
    }

    public static string BuildUser(FormGenerationProviderRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine(request.Prompt);

        if (request.Answers is { Count: > 0 })
        {
            sb.AppendLine();
            sb.AppendLine("Answers to prior clarifications:");
            foreach (var answer in request.Answers)
            {
                sb.Append("- ").Append(answer.QuestionId).Append(" = ").AppendLine(answer.OptionId);
            }
        }

        if (request.CurrentForm?.Fields is { Length: > 0 })
        {
            sb.AppendLine();
            sb.Append("Refine the existing form, which currently has ")
              .Append(request.CurrentForm.Fields.Length)
              .AppendLine(" field(s). Preserve existing fields unless the request asks to change them.");
        }

        return sb.ToString();
    }
}

/// <summary>
/// Provider-facing request for a single form-generation turn: the prompt, prior turns, answered
/// clarifications, the current form (refine), and validator failures fed back during repair.
/// </summary>
internal sealed record FormGenerationProviderRequest
{
    public required string Prompt { get; init; }
    public string? ModelOverride { get; init; }
    public IReadOnlyList<FormGenerationConversationTurn> Conversation { get; init; } = [];
    public IReadOnlyList<FormGenerationAnswer> Answers { get; init; } = [];
    public FormPackageDocument? CurrentForm { get; init; }
    public IReadOnlyList<FormValidationIssue> RepairFailures { get; init; } = [];
}
