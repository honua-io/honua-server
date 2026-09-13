// Copyright (c) Honua. All rights reserved.
// Licensed under the Elastic License 2.0. See LICENSE in the project root.

using System.Globalization;
using System.Text.Json;
using System.Xml;
using System.Xml.Linq;
using Honua.Protocols.GeoServices.GPServer.Models;
using Honua.Geoprocessing;

namespace Honua.Protocols.GeoServices.GPServer;

/// <summary>SOAP GPValue wire translation; execution remains in the shared GP handlers.</summary>
internal static class GPServerSoapExecution
{
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    internal static bool IsExecutionOperation(string name)
        => name is "SubmitJob" or "Execute" or "GetJobStatus" or "GetJobMessages"
            or "GetJobResult" or "GetJobToolName" or "CancelJob";

    internal static IReadOnlyDictionary<string, string> ReadSubmission(XElement operation, GPTaskInfoResponse task)
    {
        ValidateChildren(operation, "ToolName", "Values", "Options", "EnvironmentValues");
        RequiredScalar(operation, "ToolName");
        ValidateResultOptions(operation.Element("Options"));
        var values = operation.Element("Values") ?? throw Invalid("Values is required.");
        var inputs = (task.Parameters ?? []).Where(parameter => parameter.Direction == "esriGPParameterDirectionInput").ToArray();
        var supplied = values.Elements().ToArray();
        if (supplied.Length > inputs.Length || supplied.Any(value => value.Name != "GPValue"))
        {
            throw Invalid("Values must contain one positional GPValue for each supplied input.");
        }
        var parameters = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < supplied.Length; index++)
        {
            var value = supplied[index];
            if (IsNil(value))
            {
                continue;
            }
            var input = inputs[index];
            var type = ResolveType(value);
            var expectedType = input.DataType?.StartsWith("GPMultiValue:", StringComparison.Ordinal) == true
                ? "GPMultiValue" : input.DataType;
            if (type != expectedType)
            {
                throw Invalid($"Input '{input.Name}' requires SOAP type '{expectedType}'.");
            }
            if (!value.HasElements)
            {
                continue; // A typed empty GPValue represents an unset optional input.
            }
            parameters.Add(input.Name!, ReadValue(value, type));
        }
        ReadEnvironment(operation.Element("EnvironmentValues"), parameters);
        return parameters;
    }

    internal static string ReadJobId(XElement operation)
    {
        if (operation.Name.LocalName == "GetJobResult")
        {
            ValidateChildren(operation, "JobID", "ParameterNames", "Options");
        }
        else
        {
            ValidateChildren(operation, "JobID");
        }
        return RequiredScalar(operation, "JobID");
    }

    internal static IEnumerable<string> ReadResultNames(XElement operation, IEnumerable<string> defaults)
    {
        var names = operation.Element("ParameterNames");
        if (names is null || IsNil(names) || !names.HasElements)
        {
            return defaults;
        }
        if (names.Elements().Any(value => value.Name != "String" || value.HasElements || string.IsNullOrWhiteSpace(value.Value)))
        {
            throw Invalid("ParameterNames must contain output parameter strings.");
        }
        var result = names.Elements().Select(value => value.Value).ToArray();
        if (result.Distinct(StringComparer.Ordinal).Count() != result.Length)
        {
            throw Invalid("ParameterNames must not contain duplicates.");
        }
        return result;
    }

    internal static void ValidateResultOptions(XElement? options)
    {
        if (options is null || IsNil(options))
        {
            return;
        }
        ValidateChildren(options, "DensifyFeatures", "TransportType", "ReturnData", "UpdateValues");
        foreach (var value in options.Elements())
        {
            var accepted = !value.HasElements && (value.Name.LocalName switch
            {
                "DensifyFeatures" => value.Value is "false" or "0",
                "TransportType" => value.Value == "esriGPTransportTypeUrl",
                "ReturnData" => value.Value is "true" or "1",
                "UpdateValues" => value.Value is "false" or "0",
                _ => false
            });
            if (!accepted)
            {
                throw Invalid($"SOAP result option '{value.Name.LocalName}' is not supported with this value.");
            }
        }
    }

    internal static XElement BuildResult(IEnumerable<GPResultResponse> outputs, GPJobMessage[] messages)
        => new("Result", new XAttribute(Xsi + "type", "tns:GPResult"),
            new XElement("Values", new XAttribute(Xsi + "type", "tns:GPValues"), outputs.Select(BuildOutput)),
            BuildMessages(messages));

    internal static XElement BuildMessages(GPJobMessage[] messages)
        => new("Messages", messages.Select(message => new XElement("JobMessage",
            new XElement("MessageType", message.Type), new XElement("MessageDesc", message.Description))));

    private static XElement BuildOutput(GPResultResponse output)
    {
        if (output.DataType is not ("GPString" or "GPLong" or "GPDouble" or "GPBoolean" or "GPDate"))
        {
            throw Invalid($"SOAP output type '{output.DataType}' is not supported.");
        }
        var value = output.Value switch
        {
            null => null,
            string text => text,
            JsonElement json when json.ValueKind == JsonValueKind.String => json.GetString(),
            JsonElement json => json.GetRawText(),
            bool boolean => XmlConvert.ToString(boolean),
            IFormattable number => number.ToString(null, CultureInfo.InvariantCulture),
            _ => throw Invalid("The result cannot be represented by its advertised SOAP scalar type.")
        };
        return new XElement("GPValue", new XAttribute(Xsi + "type", "tns:" + output.DataType),
            value is null ? new XAttribute(Xsi + "nil", "true") : new XElement("Value", value));
    }

    private static string ReadValue(XElement value, string type)
    {
        if (type is not ("GPString" or "GPLong" or "GPDouble" or "GPBoolean" or "GPDate"))
        {
            throw Invalid($"SOAP input type '{type}' is not supported.");
        }
        ValidateChildren(value, "Value");
        var scalar = value.Element("Value");
        if (scalar is null || scalar.HasElements)
        {
            throw Invalid("A scalar GPValue requires one Value element.");
        }
        return scalar.Value;
    }

    private static void ReadEnvironment(XElement? environment, Dictionary<string, string> parameters)
    {
        if (environment is null || IsNil(environment))
        {
            return;
        }
        ValidateChildren(environment, "PropertyArray");
        var properties = environment.Element("PropertyArray");
        if (properties is null)
        {
            return;
        }
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in properties.Elements())
        {
            if (property.Name != "PropertySetProperty")
            {
                throw Invalid("Invalid environment property.");
            }
            ValidateChildren(property, "Key", "Value");
            var key = RequiredScalar(property, "Key");
            if (!seen.Add(key))
            {
                throw Invalid("Duplicate environment property.");
            }
            var value = property.Element("Value") ?? throw Invalid("Environment value is required.");
            if (IsNil(value) || !value.HasElements)
            {
                continue;
            }
            var text = ReadValue(value, ResolveType(value));
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }
            var restName = key switch
            {
                "outputCoordinateSystem" => "outSR",
                "cartographicCoordinateSystem" => "processSR",
                _ => key
            };
            parameters.Add("env:" + restName, text);
        }
    }

    private static string ResolveType(XElement value)
    {
        var qualified = value.Attribute(Xsi + "type")?.Value ?? throw Invalid("A GPValue requires xsi:type.");
        var parts = qualified.Split(':');
        var ns = parts.Length == 2 ? value.GetNamespaceOfPrefix(parts[0]) : value.GetDefaultNamespace();
        if (parts.Length > 2 || ns is null || !ns.NamespaceName.StartsWith("http://www.esri.com/schemas/ArcGIS/", StringComparison.Ordinal))
        {
            throw Invalid("Invalid GPValue type namespace.");
        }
        return parts[^1];
    }

    private static bool IsNil(XElement value)
    {
        var nil = value.Attribute(Xsi + "nil")?.Value;
        if (nil is not (null or "true" or "1" or "false" or "0"))
        {
            throw Invalid("Invalid xsi:nil value.");
        }
        if (nil is "true" or "1")
        {
            if (value.HasElements || !string.IsNullOrWhiteSpace(value.Value))
            {
                throw Invalid("A nil value cannot contain data.");
            }
            return true;
        }
        return false;
    }

    private static string RequiredScalar(XElement parent, string name)
    {
        var child = parent.Element(name);
        if (child is null || child.HasElements || string.IsNullOrWhiteSpace(child.Value))
        {
            throw Invalid($"{name} requires a non-empty scalar value.");
        }
        return child.Value;
    }

    private static void ValidateChildren(XElement parent, params string[] names)
    {
        if (parent.Nodes().OfType<XText>().Any(text => !string.IsNullOrWhiteSpace(text.Value)) ||
            parent.Elements().Any(child => child.Name.Namespace != XNamespace.None || !names.Contains(child.Name.LocalName, StringComparer.Ordinal)) ||
            parent.Elements().GroupBy(child => child.Name).Any(group => group.Count() != 1))
        {
            throw Invalid($"{parent.Name.LocalName} contains duplicate or unsupported arguments.");
        }
    }

    private static GeoprocessingValidationException Invalid(string message) => new(message);
}
