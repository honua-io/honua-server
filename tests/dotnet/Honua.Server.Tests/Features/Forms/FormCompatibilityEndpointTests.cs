// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Net;
using System.Text.Json;
using FluentAssertions;
using Honua.Core.Features.Forms.Packages;
using Honua.TestKit;
using Honua.TestKit.Attributes;
using Microsoft.Extensions.DependencyInjection;

namespace Honua.Server.Tests.Features.Forms;

[Collection("Database")]
[Protocol(TestProtocols.Admin)]
public sealed class FormCompatibilityEndpointTests : IAsyncLifetime
{
    private readonly WebAppFixture _fixture = new();

    public async Task InitializeAsync() => await _fixture.InitializeAsync();

    public Task DisposeAsync() => _fixture.DisposeAsync();

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/packages/{formId}/compatibility")]
    public async Task Compatibility_CurrentVersion_IsCurrentAndSubmittable()
    {
        var published = await PublishPackageAsync(CreatePackage("Compatibility current"));

        var manifest = await GetJsonAsync(
            $"/api/v1/forms/packages/{published.FormId}/compatibility?clientVersion={published.Version}",
            FormPackageJsonContext.Default.FormCompatibilityManifest);

        manifest.Compatibility.Should().Be(FormCompatibilityLevel.Current);
        manifest.OfflineEditsSubmittable.Should().BeTrue();
        manifest.MigrationRequired.Should().BeFalse();
        manifest.CurrentPublishedVersion.Should().Be(published.Version);
    }

    [IntegrationTest]
    [Operation(Operations.Configuration)]
    [Endpoint("GET /api/v1/forms/packages/{formId}/compatibility")]
    public async Task Compatibility_UnknownClientVersion_RequiresMigration()
    {
        var published = await PublishPackageAsync(CreatePackage("Compatibility unknown"));

        var manifest = await GetJsonAsync(
            $"/api/v1/forms/packages/{published.FormId}/compatibility?clientVersion=999",
            FormPackageJsonContext.Default.FormCompatibilityManifest);

        manifest.Compatibility.Should().Be(FormCompatibilityLevel.Unknown);
        manifest.MigrationRequired.Should().BeTrue();
        manifest.OfflineEditsSubmittable.Should().BeFalse();
        manifest.MigrationSignals.Should().Contain(s => s.Code == "versionUnknown");
    }

    /// <summary>
    /// Publishes a form package directly through the shared store so the test is
    /// independent of the target service advertising edit capabilities.
    /// </summary>
    private async Task<FormPackageVersion> PublishPackageAsync(FormPackageDocument package)
    {
        using var scope = _fixture.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<IFormPackageStore>();
        var draft = await store.SaveDraftAsync(package, "test-admin");
        return await store.PublishAsync(
            draft.FormId,
            draft.Version,
            new FormPackageValidationResult { IsValid = true },
            "test-admin")
            ?? throw new InvalidOperationException("Failed to publish seeded form package.");
    }

    private async Task<T> GetJsonAsync<T>(string path, System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var response = await _fixture.Client.GetAsync(path);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        return await ReadJsonAsync(response, jsonTypeInfo);
    }

    private static async Task<T> ReadJsonAsync<T>(
        HttpResponseMessage response,
        System.Text.Json.Serialization.Metadata.JsonTypeInfo<T> jsonTypeInfo)
    {
        var payload = await response.Content.ReadAsStringAsync();
        var value = JsonSerializer.Deserialize(payload, jsonTypeInfo);
        value.Should().NotBeNull(payload);
        return value!;
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
            SubmitPolicy = new FormSubmitPolicy { AllowedOperations = [FormSubmissionOperations.Create], RequiresGeometry = true },
            OfflinePolicy = new FormOfflinePolicy { Enabled = true }
        };
}
