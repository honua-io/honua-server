namespace Honua.Core.Configuration.Validation;

/// <summary>
/// Validation attribute to specify the configuration path for validation errors.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class ConfigurationPathAttribute : Attribute
{
    /// <summary>
    /// Gets the configuration path associated with the validated member.
    /// </summary>
    public string Path { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="ConfigurationPathAttribute"/> class.
    /// </summary>
    /// <param name="path">The configuration path associated with the validated member.</param>
    public ConfigurationPathAttribute(string path)
    {
        Path = path;
    }
}

/// <summary>
/// Validation attribute to suggest a fix for validation errors.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Property)]
public class SuggestedFixAttribute : Attribute
{
    /// <summary>
    /// Gets the suggested remediation text for the validation error.
    /// </summary>
    public string Fix { get; }

    /// <summary>
    /// Initializes a new instance of the <see cref="SuggestedFixAttribute"/> class.
    /// </summary>
    /// <param name="fix">The suggested remediation text for the validation error.</param>
    public SuggestedFixAttribute(string fix)
    {
        Fix = fix;
    }
}
