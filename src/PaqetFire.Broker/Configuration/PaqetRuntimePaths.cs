namespace PaqetFire.Broker.Configuration;

public sealed record PaqetRuntimePaths(
    string PayloadRoot,
    string ExecutablePath,
    string ConfigurationRoot,
    string ConfigurationPath)
{
    public static PaqetRuntimePaths CreateDefault()
    {
        var payloadRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "payload"));
        var configurationRoot = Path.GetFullPath(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "PaqetFire",
            "config"));

        return new PaqetRuntimePaths(
            payloadRoot,
            Path.Combine(payloadRoot, "engines", "paqet", "x64", "paqet_windows_amd64.exe"),
            configurationRoot,
            Path.Combine(configurationRoot, "paqet", "client.yaml"));
    }
}
