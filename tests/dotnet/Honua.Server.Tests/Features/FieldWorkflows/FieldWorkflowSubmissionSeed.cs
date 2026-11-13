// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.FieldWorkflows;

// Seeds a published form package and a completed (accepted) submission row
// directly through the shared IFormPackageStore. This exercises
// the same durable schema the runtime submission path writes to, without
// depending on the target service advertising edit capabilities.
internal sealed class FieldWorkflowSubmissionSeed(IServiceProvider services, string titlePrefix)
{
    public async Task<Guid> SeedSubmissionAsync(string suffix, string name)
    {
        using var scope = services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();

        var draft = await store.SaveDraftAsync(CreatePackage(titlePrefix + " " + suffix), "test-admin");
        var published = await store.PublishAsync(
            draft.FormId,
            draft.Version,
            new FormPackageValidationResult { IsValid = true },
            "test-admin")
            ?? throw new InvalidOperationException("Failed to publish seeded form package.");

        var submission = new FormSubmissionRequest
        {
            IdempotencyKey = suffix,
            Operation = FormSubmissionOperations.Create,
            ClientId = "field-client-1",
            Values = new Dictionary<string, JsonElement>
            {
                ["name"] = JsonElement.Parse(JsonSerializer.Serialize(name))
            }
        };

        var submissionId = Guid.NewGuid();
        var claimed = await store.CreateSubmissionAsync(
            submissionId,
            suffix,
            "actor-hash-" + suffix,
            "request-hash-" + suffix,
            published,
            submission,
            "pending");
        claimed.Should().BeTrue();

        await store.CompleteSubmissionAsync(
            submissionId,
            new FormSubmissionResponse
            {
                SubmissionId = submissionId,
                Status = "accepted",
                FormId = published.FormId,
                FormVersion = published.Version,
                Operation = FormSubmissionOperations.Create,
                TargetFeatureId = 1
            },
            "accepted");

        return submissionId;
    }

    private static FormPackageDocument CreatePackage(string title)
        => new()
        {
            Title = title,
            Target = new FormTargetDefinition
            {
                ServiceId = WebAppFixture.TestServiceId,
                LayerId = WebAppFixture.TestLayerId
            },
            Sections =
            [
                new FormSectionDefinition { SectionId = "main", Label = "Main", FieldIds = ["name"] }
            ],
            Fields =
            [
                new FormFieldDefinition
                {
                    FieldId = "name",
                    Label = "Name",
                    Type = "text",
                    TargetField = "name",
                    Required = true,
                    SectionId = "main"
                }
            ],
            SubmitPolicy = new FormSubmitPolicy
            {
                AllowedOperations = [FormSubmissionOperations.Create],
                RequiresGeometry = true
            },
            OfflinePolicy = new FormOfflinePolicy { Enabled = true }
        };
}
