namespace PaqetFire.Desktop.Presentation;

/// <summary>The sign-in launch command: PaqetFire starts hidden in the notification area.</summary>
public static class StartupCommand
{
    public const string BackgroundArgument = "--background";

    public static string Create(string executablePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        return $"\"{executablePath}\" {BackgroundArgument}";
    }

    public static bool IsBackgroundLaunch(IEnumerable<string> arguments) =>
        arguments.Any(argument => string.Equals(argument.Trim(), BackgroundArgument, StringComparison.OrdinalIgnoreCase));

    /// <summary>True when an existing Run value is missing or predates the background flag.</summary>
    public static bool NeedsUpdate(string? currentValue, string executablePath) =>
        !string.Equals(currentValue, Create(executablePath), StringComparison.OrdinalIgnoreCase);
}
