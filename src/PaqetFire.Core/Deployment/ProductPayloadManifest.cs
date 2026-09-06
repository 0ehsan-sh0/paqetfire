using System.Text.Json;
using System.Text.Json.Serialization;

namespace PaqetFire.Core.Deployment;

public sealed record ProductPayloadManifest(
    string SchemaVersion,
    string Architecture,
    IReadOnlyList<BundledEngine> Engines,
    IReadOnlyList<InstallerPrerequisite> Prerequisites)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static ProductPayloadManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);

        using var stream = File.OpenRead(manifestPath);
        return JsonSerializer.Deserialize<ProductPayloadManifest>(stream, SerializerOptions)
            ?? throw new InvalidDataException("The bundled payload manifest is empty.");
    }
}
