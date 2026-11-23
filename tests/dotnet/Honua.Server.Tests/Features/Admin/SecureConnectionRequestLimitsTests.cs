// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.ComponentModel.DataAnnotations;
using System.Text.Json;
using System.Text.Json.Nodes;
using FluentAssertions;
using Honua.Server.Features.Admin.Models;
using Honua.TestKit.Attributes;

namespace Honua.Server.Tests.Features.Admin;

// The connection create and update handlers validate these annotations before any registry,
// driver or network call, so removing one would let oversized input reach the database probe.
public sealed class SecureConnectionRequestLimitsTests
{
    public static TheoryData<string, object> OversizedCreateFields => new()
    {
        { "Name", new string('n', 65) },
        { "Name", string.Empty },
        { "Description", new string('d', 1001) },
        { "Host", new string('h', 256) },
        { "Port", 0 },
        { "Port", 65536 },
        { "DatabaseName", new string('b', 65) },
        { "Username", new string('u', 65) },
        { "Password", new string('p', 256) },
        { "SecretReference", new string('s', 256) },
        { "Provider", new string('v', 33) },
    };

    public static TheoryData<string, object> OversizedUpdateFields => new()
    {
        { "Name", new string('n', 65) },
        { "Description", new string('d', 1001) },
        { "Host", string.Empty },
        { "Host", new string('h', 256) },
        { "Port", 65536 },
        { "DatabaseName", new string('b', 65) },
        { "Username", new string('u', 65) },
        { "Password", new string('p', 256) },
    };

    [UnitTheory]
    [MemberData(nameof(OversizedCreateFields))]
    public void CreateRequest_FieldOutsideLimit_FailsValidationNamingTheField(string field, object value)
        => InvalidMembers(With<CreateSecureConnectionRequest>(ValidCreate(), field, value)).Should().Contain(field);

    [UnitTheory]
    [MemberData(nameof(OversizedUpdateFields))]
    public void UpdateRequest_FieldOutsideLimit_FailsValidationNamingTheField(string field, object value)
        => InvalidMembers(With<UpdateSecureConnectionRequest>(new JsonObject(), field, value)).Should().Contain(field);

    [UnitTest]
    public void Requests_AtEveryLimit_PassValidation()
    {
        var create = new CreateSecureConnectionRequest
        {
            Name = new string('n', 64),
            Description = new string('d', 1000),
            Host = new string('h', 255),
            Port = 65535,
            DatabaseName = new string('b', 64),
            Username = new string('u', 64),
            Password = new string('p', 255),
            SecretReference = new string('s', 255),
            Provider = new string('v', 32),
            SslMode = "Require",
        };
        var update = new UpdateSecureConnectionRequest
        {
            Name = new string('n', 64),
            Description = new string('d', 1000),
            Host = new string('h', 255),
            Port = 1,
            DatabaseName = new string('b', 64),
            Username = new string('u', 64),
            Password = new string('p', 255),
        };

        InvalidMembers(create).Should().BeEmpty();
        InvalidMembers(update).Should().BeEmpty();
    }

    private static JsonObject ValidCreate() => new()
    {
        ["Name"] = "limits",
        ["Host"] = "db.example.com",
        ["Port"] = 5432,
        ["DatabaseName"] = "gis",
        ["Username"] = "reader",
        ["Password"] = "secret",
        ["SslMode"] = "Require",
    };

    private static T With<T>(JsonObject request, string field, object value)
    {
        request[field] = JsonSerializer.SerializeToNode(value);
        return request.Deserialize<T>()!;
    }

    private static string[] InvalidMembers(object request)
    {
        var results = new List<ValidationResult>();
        _ = Validator.TryValidateObject(request, new ValidationContext(request), results, validateAllProperties: true);
        return [.. results.SelectMany(result => result.MemberNames)];
    }
}
