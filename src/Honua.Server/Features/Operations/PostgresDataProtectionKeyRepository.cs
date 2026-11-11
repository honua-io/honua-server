// Copyright (c) Honua Geo Inc. All rights reserved.
// Licensed under the Fair Core License, Version 1.0, ALv2 Future License. See LICENSE in the project root.

using System.Xml.Linq;
using Honua.ControlPlane;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;
using NpgsqlTypes;

namespace Honua.Server.Features.Operations;

// IXmlRepository is synchronous, so the commands are too.
internal sealed class PostgresDataProtectionKeyRepository(
    NpgsqlDataSource dataSource,
    JobStoreDatabase database) : IXmlRepository
{
    private readonly string _keys = database.Table("data_protection_keys");

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var command = dataSource.CreateCommand($"SELECT element FROM {_keys} ORDER BY key_sequence");
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read())
        {
            elements.Add(XElement.Parse(reader.GetString(0), LoadOptions.PreserveWhitespace));
        }

        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);

        using var command = dataSource.CreateCommand(
            $"INSERT INTO {_keys} (friendly_name, element) VALUES (@friendly_name, @element)");
        command.Parameters.Add(new NpgsqlParameter("friendly_name", NpgsqlDbType.Text) { Value = (object?)friendlyName ?? DBNull.Value });
        command.Parameters.Add(new NpgsqlParameter("element", NpgsqlDbType.Text) { Value = element.ToString(SaveOptions.DisableFormatting) });
        command.ExecuteNonQuery();
    }

    public void EnsureAllElementsAreProtected()
        => DataProtectionKeyElementProtection.EnsureAllProtected(GetAllElements());
}
