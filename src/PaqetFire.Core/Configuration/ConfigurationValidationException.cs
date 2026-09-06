namespace PaqetFire.Core.Configuration;

public sealed class ConfigurationValidationException : ArgumentException
{
    public ConfigurationValidationException(IReadOnlyList<string> errors)
        : base(CreateMessage(errors))
    {
        ArgumentNullException.ThrowIfNull(errors);
        Errors = errors.ToArray();
    }

    public IReadOnlyList<string> Errors { get; }

    private static string CreateMessage(IReadOnlyList<string> errors)
    {
        ArgumentNullException.ThrowIfNull(errors);

        return errors.Count == 0
            ? "The engine configuration is invalid."
            : $"The engine configuration is invalid: {string.Join(" ", errors)}";
    }
}
