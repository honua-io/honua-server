namespace Honua.Core.Configuration.Validation;

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class ConfigurationPathAttribute : Attribute
{
    public string Path { get; }

    public ConfigurationPathAttribute(string path)
    {
        Path = path;
    }
}

[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class SuggestedFixAttribute : Attribute
{
    public string Fix { get; }

    public SuggestedFixAttribute(string fix)
    {
        Fix = fix;
    }
}
