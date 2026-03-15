using System.Text.Json;
using System.Text.Json.Serialization;
using Honua.Validation.Runner.Contracts;

namespace Honua.Validation.Runner;

internal static class Program
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static int Main(string[] args)
    {
        try
        {
            if (args.Length == 0)
            {
                PrintUsage();
                return 2;
            }

            return args[0] switch
            {
                "describe-targets" => DescribeTargets(),
                "print-example-request" => PrintExampleRequest(args[1..]),
                "validate-request" => ValidateRequest(args[1..]),
                "--help" or "-h" or "help" => Help(),
                _ => UnknownCommand(args[0])
            };
        }
        catch (Exception ex)
        {
            var failure = new ValidationRunnerResult
            {
                Target = "unknown",
                Status = ValidationRunnerStatus.Error,
                Errors = [ex.Message]
            };
            WriteJson(failure);
            return 10;
        }
    }

    private static int DescribeTargets()
    {
        WriteJson(ValidationCatalog.All);
        return 0;
    }

    private static int PrintExampleRequest(string[] args)
    {
        var target = ReadRequiredOption(args, "--target");
        if (target is null)
        {
            PrintUsage();
            return 2;
        }

        if (!ValidationCatalog.TryGetByKey(target, out var contract))
        {
            Console.Error.WriteLine($"Unknown target '{target}'.");
            return 2;
        }

        var example = ValidationExampleFactory.Create(contract);
        WriteJson(example);
        return 0;
    }

    private static int ValidateRequest(string[] args)
    {
        var inputPath = ReadRequiredOption(args, "--input");
        if (inputPath is null)
        {
            PrintUsage();
            return 2;
        }

        var payload = File.ReadAllText(inputPath);
        var request = JsonSerializer.Deserialize<ValidationRunnerRequest>(payload, JsonOptions);
        if (request is null)
        {
            Console.Error.WriteLine($"Could not deserialize validation request from '{inputPath}'.");
            return 2;
        }

        var result = ValidationRequestValidator.Validate(request);
        WriteJson(result);
        return result.Status == ValidationRunnerStatus.Valid ? 0 : 1;
    }

    private static string? ReadRequiredOption(string[] args, string optionName)
    {
        for (var i = 0; i < args.Length - 1; i++)
        {
            if (args[i] == optionName)
            {
                return args[i + 1];
            }
        }

        return null;
    }

    private static int Help()
    {
        PrintUsage();
        return 0;
    }

    private static int UnknownCommand(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        PrintUsage();
        return 2;
    }

    private static void PrintUsage()
    {
        Console.WriteLine(
            """
            Honua.Validation.Runner

            Usage:
              dotnet run --project src/Honua.Validation.Runner -- describe-targets
              dotnet run --project src/Honua.Validation.Runner -- print-example-request --target <target-key>
              dotnet run --project src/Honua.Validation.Runner -- validate-request --input <request.json>
            """);
    }

    private static void WriteJson<T>(T value)
    {
        Console.WriteLine(JsonSerializer.Serialize(value, JsonOptions));
    }
}
