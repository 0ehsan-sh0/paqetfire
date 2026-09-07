using PaqetFire.Core.Configuration;
using PaqetFire.Core.Deployment;

namespace PaqetFire.Broker.Runtime;

// Snapshot polling retains only the public settings projection, never decrypted secrets.
public sealed class SnapshotMetadataCache(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider clock = timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim settingsGate = new(1, 1);
    private readonly object prerequisiteGate = new();
    private bool settingsLoaded;
    private PaqetFireSettingsView? settings;
    private IReadOnlyList<PrerequisiteStatus>? prerequisites;
    private DateTimeOffset prerequisitesExpireAt;

    public async ValueTask<PaqetFireSettingsView?> GetSettingsAsync(
        Func<CancellationToken, ValueTask<PaqetFireSettings?>> load,
        CancellationToken cancellationToken)
    {
        await settingsGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!settingsLoaded)
            {
                var value = await load(cancellationToken).ConfigureAwait(false);
                settings = value is null ? null : PaqetFireSettingsView.FromSettings(value);
                settingsLoaded = true;
            }
            return settings;
        }
        finally { settingsGate.Release(); }
    }

    public async ValueTask UpdateSettingsAsync(PaqetFireSettings? value)
    {
        await settingsGate.WaitAsync().ConfigureAwait(false);
        try
        {
            settings = value is null ? null : PaqetFireSettingsView.FromSettings(value);
            settingsLoaded = true;
        }
        finally { settingsGate.Release(); }
    }

    public IReadOnlyList<PrerequisiteStatus> GetPrerequisites(
        Func<IReadOnlyList<PrerequisiteStatus>> inspect, bool forceRefresh = false)
    {
        lock (prerequisiteGate)
        {
            if (forceRefresh || prerequisites is null || clock.GetUtcNow() >= prerequisitesExpireAt)
            {
                prerequisites = inspect();
                prerequisitesExpireAt = clock.GetUtcNow().AddSeconds(30);
            }
            return prerequisites;
        }
    }
}
