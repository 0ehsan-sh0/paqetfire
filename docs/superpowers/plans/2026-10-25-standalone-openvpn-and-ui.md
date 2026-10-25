# Standalone OpenVPN Engine, DCO Driver, UI Health & Password Retention Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Package OpenVPN 2.7.8 x64 and the WHQL-signed `ovpn-dco` driver into the standalone payload, automatically provision the DCO adapter at installation/runtime, fix OpenVPN password retention across toggle and saves, display OpenVPN exit IP and endpoint on the Overview card, and expose OpenVPN health and driver status in the Diagnostics UI.

**Architecture:** 
1. **Engine Payload & Driver:** Bundle `openvpn.exe`, `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, and `libpkcs11-helper-1.dll` in `payload/engines/openvpn/x64/` and `ovpn-dco.inf`, `ovpn-dco.cat`, and `ovpn-dco.sys` in `payload/engines/openvpn/x64/driver/`. Pin SHA-256 hashes in `payload/payload-manifest.json` and verify them via `BundledEngineOptions` and `OpenVpnProcessAdapter`. Provision the DCO driver via elevated `pnputil.exe /add-driver ... /install` in WiX installer deferred custom action and Broker runtime self-heal.
2. **Password Retention:** Encrypt `UpstreamOpenVpn.Password` using DPAPI in `MachineSettingsStore`, merge existing saved passwords in `PaqetFireRuntime.SaveSettingsAsync` when incoming password is empty, and preserve `hasSavedUpstreamOpenVpnPassword` in Desktop UI so toggling OpenVPN off/on preserves credentials seamlessly.
3. **Overview & Diagnostics UI:** In `LiveStatsService`, query the public IP probe without SOCKS proxy when OpenVPN is running so it exits through the OpenVPN TUN/DCO adapter and resolves the OpenVPN exit IP and country flag. Update `EndpointText` and `ProtocolText` to reflect OpenVPN remote endpoint. Add an OpenVPN status card to Diagnostics "Health and activity" and the `ovpn-dco` driver to "Required software".

**Tech Stack:** C# 13, .NET 10, Windows Presentation Foundation (WPF), WiX Toolset v4, OpenVPN 2.7.8 x64, OpenVPN DCO driver 2.8.14, DPAPI (`System.Security.Cryptography.ProtectedData`), `pnputil.exe`.

**Spec:** [docs/adr/0006-standalone-openvpn-bundling-and-dco-driver.md](file:///e:/Developing/Projects/paqetfire/docs/adr/0006-standalone-openvpn-bundling-and-dco-driver.md)

## Global Constraints

- Pinned OpenVPN Version: `2.7.8`
- Pinned DCO Driver Version: `2.8.14`
- Pinned OpenVPN Executable SHA-256: `02d1f147e58de4e12735d9d4614a40d69a87a4874928b962aa235264fa364ea1`
- Preserved Iran Bypass: `geosite:category-ir` and `geoip:ir` bound to `PhysicalAdapterIp`
- Zero Psiphon references in code, UI, or commit messages
- All files and symbol links must use the `file://` scheme

---

### Task 1: Stage OpenVPN Binaries & DCO Driver in Payload Manifest

**Files:**
- Create: `payload/engines/openvpn/x64/openvpn.exe`
- Create: `payload/engines/openvpn/x64/libcrypto-3-x64.dll`
- Create: `payload/engines/openvpn/x64/libssl-3-x64.dll`
- Create: `payload/engines/openvpn/x64/libpkcs11-helper-1.dll`
- Create: `payload/engines/openvpn/x64/driver/ovpn-dco.inf`
- Create: `payload/engines/openvpn/x64/driver/ovpn-dco.cat`
- Create: `payload/engines/openvpn/x64/driver/ovpn-dco.sys`
- Create: `payload/licenses/OpenVPN-GPL-2.0.txt`
- Modify: `payload/payload-manifest.json:80-95`
- Modify: `payload/THIRD_PARTY_NOTICES.md`
- Modify: `src/PaqetFire.Broker/Engines/BundledEngineOptions.cs:45-56`
- Modify: `src/PaqetFire.Broker/Engines/OpenVpnProcessOptions.cs:1-15`
- Modify: `src/PaqetFire.Broker/Engines/OpenVpnProcessAdapter.cs:80-92`
- Test: `tests/PaqetFire.Broker.Tests/BundledEngineOptionsTests.cs:10-36`

**Interfaces:**
- Consumes: `ProductPayloadManifest.Load(manifestPath)`, `OpenVpnLocator.FindExecutable(payloadRoot)`
- Produces: `BundledEngineOptions.CreateOpenVpn(...)` with pinned SHA-256 validation; `OpenVpnProcessAdapter.ValidatePayloadAsync()` verifying binary integrity against payload manifest.

- [ ] **Step 1: Write the failing test for OpenVPN manifest pinning**

Modify `tests/PaqetFire.Broker.Tests/BundledEngineOptionsTests.cs`:
```csharp
    [Theory]
    [InlineData(EngineKind.Paqet)]
    [InlineData(EngineKind.Xray)]
    [InlineData(EngineKind.ProxiFyre)]
    [InlineData(EngineKind.OpenVpn)]
    public void ProductionStartupPinsMatchReleaseManifest(EngineKind kind)
    {
        var manifest = ProductPayloadManifest.Load(Path.Combine(AppContext.BaseDirectory, "release-payload-manifest.json"));
        var engine = Assert.Single(manifest.Engines, item => item.Engine == kind);
        var entryPoint = Assert.Single(engine.Files, file => file.Path == engine.EntryPoint);
        var paths = new RuntimePaths("payload", "paqet.exe", "paqet.yaml", "xray.exe", "xray.json",
            "geoip.dat", "geosite.dat", "ProxiFyre.exe", "app-config.json", "settings.json",
            OpenVpnExecutablePath: "openvpn.exe");
        (string? version, string? digest) = kind switch
        {
            EngineKind.Paqet => Pin(BundledEngineOptions.CreatePaqet(paths)),
            EngineKind.Xray => Pin(BundledEngineOptions.CreateXray(paths)),
            EngineKind.ProxiFyre => Pin(BundledEngineOptions.CreateProxiFyre(paths)),
            EngineKind.OpenVpn => Pin(BundledEngineOptions.CreateOpenVpn("openvpn.exe", "runtime.ovpn")),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
        Assert.Equal(engine.Version, version);
        Assert.Equal(entryPoint.Sha256, digest, ignoreCase: true);
    }

    private static (string?, string?) Pin(OpenVpnProcessOptions options) => (options.Version, options.ExpectedExecutableSha256);
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~BundledEngineOptionsTests`
Expected: FAIL because `EngineKind.OpenVpn` is not in `release-payload-manifest.json` and `OpenVpnProcessOptions` lacks `ExpectedExecutableSha256`.

- [ ] **Step 3: Copy binaries, driver, license, and update payload-manifest.json and options**

1. Copy files:
   - Copy `C:\Program Files\OpenVPN\bin\openvpn.exe`, `libcrypto-3-x64.dll`, `libssl-3-x64.dll`, `libpkcs11-helper-1.dll` to `payload/engines/openvpn/x64/`
   - Copy `C:\Windows\System32\DriverStore\FileRepository\ovpn-dco.inf_amd64_89d4b3a14e861a3b\ovpn-dco.inf`, `ovpn-dco.cat`, `ovpn-dco.sys` to `payload/engines/openvpn/x64/driver/`
   - Copy `C:\Program Files\OpenVPN\license.txt` to `payload/licenses/OpenVPN-GPL-2.0.txt`
2. Update `payload/payload-manifest.json` under `"engines"`:
```json
    {
      "engine": "OpenVpn",
      "version": "2.7.8",
      "entryPoint": "engines/openvpn/x64/openvpn.exe",
      "files": [
        {
          "path": "engines/openvpn/x64/openvpn.exe",
          "size": 1077680,
          "sha256": "02d1f147e58de4e12735d9d4614a40d69a87a4874928b962aa235264fa364ea1"
        },
        {
          "path": "engines/openvpn/x64/libcrypto-3-x64.dll",
          "size": 5290504,
          "sha256": "1bbececb5ccb120a2003d105ace388fec5ffa174dd8268ba51a6d4f29d41a94f"
        },
        {
          "path": "engines/openvpn/x64/libssl-3-x64.dll",
          "size": 923656,
          "sha256": "434801bbecc42f2b861c9da535baacb8d8720d3f463f92129a1667edda8aad9c"
        },
        {
          "path": "engines/openvpn/x64/libpkcs11-helper-1.dll",
          "size": 112048,
          "sha256": "6636876cf2682a45b411d516bf592f40c3c811fe1f415fbe17ef363da6a63e35"
        },
        {
          "path": "engines/openvpn/x64/driver/ovpn-dco.inf",
          "size": 3066,
          "sha256": "bcfe1a0c6584306ed072dfbee51cbf838ec53fc71bd580b249b3bfdaa5176742"
        },
        {
          "path": "engines/openvpn/x64/driver/ovpn-dco.cat",
          "size": 11855,
          "sha256": "85351773de9e3af57650b44c0911354613613cad569b3381d429218a1e2bf2e8"
        },
        {
          "path": "engines/openvpn/x64/driver/ovpn-dco.sys",
          "size": 168160,
          "sha256": "80b2b2962dd421e566cacddf3e003455a6aa50764dec76479092b92570330a1b"
        }
      ]
    }
```
3. Update `src/PaqetFire.Broker/Engines/OpenVpnProcessOptions.cs`:
```csharp
public sealed record OpenVpnProcessOptions
{
    public required string ExecutablePath { get; init; }
    public required string ConfigurationPath { get; init; }
    public string? AuthFilePath { get; init; }
    public string? Version { get; init; }
    public string? ExpectedExecutableSha256 { get; init; }
    public TimeSpan ReadinessTimeout { get; init; } = TimeSpan.FromSeconds(30);
}
```
4. Update `src/PaqetFire.Broker/Engines/BundledEngineOptions.cs`:
```csharp
    public const string OpenVpnSha256 = "02d1f147e58de4e12735d9d4614a40d69a87a4874928b962aa235264fa364ea1";

    public static OpenVpnProcessOptions CreateOpenVpn(
        string executablePath,
        string configPath,
        string? authPath = null) => new()
    {
        ExecutablePath = executablePath,
        ConfigurationPath = configPath,
        AuthFilePath = authPath,
        Version = OpenVpnVersion,
        ExpectedExecutableSha256 = OpenVpnSha256,
        ReadinessTimeout = TimeSpan.FromSeconds(30),
    };
```
5. In `src/PaqetFire.Broker/Engines/OpenVpnProcessAdapter.cs`, validate SHA-256 before process execution:
```csharp
            if (!string.IsNullOrEmpty(options.ExpectedExecutableSha256))
            {
                using var stream = File.OpenRead(options.ExecutablePath);
                var actualHash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(stream));
                if (!string.Equals(actualHash, options.ExpectedExecutableSha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new System.Security.Cryptography.CryptographicException(
                        $"OpenVPN executable digest mismatch. Expected {options.ExpectedExecutableSha256}, observed {actualHash}.");
                }
            }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~BundledEngineOptionsTests`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add payload/ src/PaqetFire.Broker/Engines/ tests/PaqetFire.Broker.Tests/
git commit -m "feat(payload): bundle openvpn 2.7.8 x64 and ovpn-dco driver into engine manifest"
```

---

### Task 2: Automated Driver Provisioning and Diagnostics Prerequisite Detection

**Files:**
- Create: `src/PaqetFire.Broker/Deployment/OpenVpnDriverInstaller.cs`
- Modify: `src/PaqetFire.Broker/Deployment/PrerequisiteInspector.cs:20-60`
- Modify: `src/PaqetFire.Broker/Deployment/PrerequisiteCompatibility.cs:1-30`
- Modify: `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs:1580-1610`
- Modify: `installer/PaqetFire.Installer/Package.wxs:20-40,210-230`
- Test: `tests/PaqetFire.Broker.Tests/PrerequisiteInspectorTests.cs`

**Interfaces:**
- Consumes: `paths.PayloadRoot`, `prerequisiteInspector.Inspect()`
- Produces: `OpenVpnDriverInstaller.EnsureDriverInstalledAsync(payloadRoot)`: invokes `pnputil.exe /add-driver ... /install` if `ovpn-dco.sys` is missing; `PrerequisiteInspector.InspectOpenVpnDco()` returning `PrerequisiteStatus("ovpn-dco", ...)`.

- [ ] **Step 1: Write the failing test for OpenVPN DCO driver inspection**

Create test in `tests/PaqetFire.Broker.Tests/PrerequisiteInspectorTests.cs` (or add to existing test file):
```csharp
[Fact]
public void PrerequisiteInspector_DetectsOpenVpnDcoDriver()
{
    var inspector = new PrerequisiteInspector();
    var prerequisites = inspector.Inspect();
    var dco = Assert.Single(prerequisites, p => p.Id == "ovpn-dco");
    Assert.Equal("OpenVPN Data Channel Offload driver", dco.DisplayName);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~PrerequisiteInspector_DetectsOpenVpnDcoDriver`
Expected: FAIL because `"ovpn-dco"` prerequisite is not implemented yet.

- [ ] **Step 3: Implement driver inspection and installer**

1. In `src/PaqetFire.Broker/Deployment/PrerequisiteCompatibility.cs`, add:
```csharp
public static bool IsSupportedOpenVpnDcoVersion(string? version) =>
    version is not null && Version.TryParse(version, out var parsed) && parsed >= new Version(2, 8, 0);
```

2. In `src/PaqetFire.Broker/Deployment/PrerequisiteInspector.cs`, add:
```csharp
    private static readonly Uri OpenVpnDcoHelp = new("https://github.com/OpenVPN/ovpn-dco-win");

    private static PrerequisiteStatus InspectOpenVpnDco()
    {
        var driverPath = Path.Combine(Environment.SystemDirectory, "drivers", "ovpn-dco.sys");
        var version = TryReadFileVersion(driverPath);
        var installed = PrerequisiteCompatibility.IsSupportedOpenVpnDcoVersion(version);
        return new PrerequisiteStatus(
            "ovpn-dco",
            "OpenVPN Data Channel Offload driver",
            installed,
            installed
                ? $"Compatible DCO driver detected (version {version})."
                : version is null
                    ? "Required for Upstream OpenVPN. The driver is bundled and installed automatically."
                    : $"Driver version {version} is incompatible. OpenVPN requires DCO 2.8.0 or newer.",
            OpenVpnDcoHelp);
    }
```
Include `InspectOpenVpnDco()` in `InspectMachine()`.

3. Create `src/PaqetFire.Broker/Deployment/OpenVpnDriverInstaller.cs`:
```csharp
using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace PaqetFire.Broker.Deployment;

public static class OpenVpnDriverInstaller
{
    public static async ValueTask<bool> EnsureDriverInstalledAsync(string payloadRoot, ILogger? logger = null)
    {
        var driverSys = Path.Combine(Environment.SystemDirectory, "drivers", "ovpn-dco.sys");
        if (File.Exists(driverSys))
        {
            return true;
        }

        var infPath = Path.Combine(payloadRoot, "engines", "openvpn", "x64", "driver", "ovpn-dco.inf");
        if (!File.Exists(infPath))
        {
            logger?.LogWarning("Bundled ovpn-dco.inf not found at {Path}", infPath);
            return false;
        }

        try
        {
            logger?.LogInformation("Installing bundled OpenVPN DCO driver from {InfPath}...", infPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(Environment.SystemDirectory, "pnputil.exe"),
                Arguments = $"/add-driver \"{infPath}\" /install",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            using var process = Process.Start(startInfo);
            if (process is not null)
            {
                await process.WaitForExitAsync().ConfigureAwait(false);
                logger?.LogInformation("pnputil exited with code {ExitCode}", process.ExitCode);
                return process.ExitCode == 0 || process.ExitCode == 259 || process.ExitCode == 3010;
            }
        }
        catch (Exception ex)
        {
            logger?.LogError(ex, "Failed to provision OpenVPN DCO driver.");
        }

        return File.Exists(driverSys);
    }
}
```

4. In `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs`:
In `WriteEngineConfigurationsAsync` or before launching OpenVPN when `isOpenVpnActive` is true:
```csharp
        if (isOpenVpnActive)
        {
            await OpenVpnDriverInstaller.EnsureDriverInstalledAsync(paths.PayloadRoot, logger).ConfigureAwait(false);
        }
```

5. In `installer/PaqetFire.Installer/Package.wxs`:
Add restart resource for `openvpn.exe`:
```xml
    <util:RestartResource
        Id="OpenVpnEngineRestartResource"
        Path="[BrokerFolder]payload\engines\openvpn\x64\openvpn.exe" />
```
Add deferred execution custom action to install the bundled driver during MSI installation:
```xml
    <CustomAction
        Id="InstallOpenVpnDcoDriver"
        Directory="SystemFolder"
        ExeCommand="&quot;[SystemFolder]pnputil.exe&quot; /add-driver &quot;[BrokerFolder]payload\engines\openvpn\x64\driver\ovpn-dco.inf&quot; /install"
        Execute="deferred"
        Impersonate="no"
        Return="ignore" />
    <InstallExecuteSequence>
      <Custom Action="InstallOpenVpnDcoDriver" After="InstallFiles" Condition="NOT Installed OR REINSTALL" />
    </InstallExecuteSequence>
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~PrerequisiteInspector_DetectsOpenVpnDcoDriver`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PaqetFire.Broker/ installer/ tests/
git commit -m "feat(driver): automate ovpn-dco driver provisioning and prerequisite inspection"
```

---

### Task 3: Fix OpenVPN Password Retention on Toggle and Save

**Files:**
- Modify: `src/PaqetFire.Broker/Configuration/MachineSettingsStore.cs:11-90,154-158`
- Modify: `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs:208-225`
- Modify: `src/PaqetFire.Core/Profiles/ProfileCatalogMessages.cs:25-43`
- Modify: `src/PaqetFire.Desktop/MainWindow.Form.cs:385-415,433-455,485-495`
- Modify: `src/PaqetFire.Desktop/MainWindow.UpstreamOpenVpn.cs:10-35`
- Test: `tests/PaqetFire.Broker.Tests/MachineSettingsStoreTests.cs`
- Test: `tests/PaqetFire.Core.Tests/UpstreamOpenVpnValidatorTests.cs`

**Interfaces:**
- Consumes: `PaqetFireSettings.UpstreamOpenVpn.Password`, `hasSavedUpstreamOpenVpnPassword`
- Produces: `MachineSettingsStore` persists DPAPI-protected OpenVPN password; `PaqetFireRuntime.SaveSettingsAsync` merges `existing.UpstreamOpenVpn.Password` when new password is blank; UI placeholder displays `"Saved securely · leave blank to keep it"`.

- [ ] **Step 1: Write the failing test for password persistence in MachineSettingsStore**

Add test in `tests/PaqetFire.Broker.Tests/MachineSettingsStoreTests.cs`:
```csharp
[Fact]
public async Task SaveAsync_PreservesUpstreamOpenVpnPassword()
{
    var tempPath = Path.Combine(Path.GetTempPath(), $"settings_{Guid.NewGuid():N}.json");
    try
    {
        var store = new MachineSettingsStore(tempPath);
        var original = new PaqetFireSettings
        {
            ServerEndpoint = "51.254.208.73:8443",
            TransportKey = "test-key",
            UpstreamOpenVpn = new UpstreamOpenVpnOptions
            {
                Enabled = true,
                Username = "proton-user",
                Password = "secret-vpn-password",
                OvpnConfigText = "client\ndev tun\nremote 1.2.3.4 1194 udp"
            }
        };

        await store.SaveAsync(original, CancellationToken.None);
        var loaded = await store.LoadAsync(CancellationToken.None);

        Assert.NotNull(loaded);
        Assert.Equal("secret-vpn-password", loaded.UpstreamOpenVpn.Password);
    }
    finally
    {
        if (File.Exists(tempPath)) File.Delete(tempPath);
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~SaveAsync_PreservesUpstreamOpenVpnPassword`
Expected: FAIL because `MachineSettingsStore` does not protect or restore `UpstreamOpenVpn.Password`.

- [ ] **Step 3: Implement DPAPI storage and runtime secret merging**

1. In `src/PaqetFire.Broker/Configuration/MachineSettingsStore.cs`:
Add entropy:
```csharp
private static readonly byte[] OpenVpnPasswordEntropy = Encoding.UTF8.GetBytes("PaqetFire.OpenVpnPassword.v1");
```
Update `StoredSettings`:
```csharp
    private sealed record StoredSettings(
        PaqetFireSettings Settings,
        string ProtectedTransportKey,
        string? ProtectedLanSocksPassword = null,
        string? ProtectedOpenVpnPassword = null);
```
In `SaveAsync`:
```csharp
        var stored = new StoredSettings(
            settings with
            {
                TransportKey = string.Empty,
                LanSocksPassword = string.Empty,
                UpstreamOpenVpn = settings.UpstreamOpenVpn with { Password = null },
            },
            Protect(settings.TransportKey, Entropy),
            string.IsNullOrEmpty(settings.LanSocksPassword)
                ? null
                : Protect(settings.LanSocksPassword, LanPasswordEntropy),
            string.IsNullOrEmpty(settings.UpstreamOpenVpn.Password)
                ? null
                : Protect(settings.UpstreamOpenVpn.Password, OpenVpnPasswordEntropy));
```
In `LoadAsync`:
```csharp
        var key = Unprotect(stored.ProtectedTransportKey, Entropy);
        var lanPassword = string.IsNullOrEmpty(stored.ProtectedLanSocksPassword)
            ? string.Empty
            : Unprotect(stored.ProtectedLanSocksPassword, LanPasswordEntropy);
        var openVpnPassword = string.IsNullOrEmpty(stored.ProtectedOpenVpnPassword)
            ? string.Empty
            : Unprotect(stored.ProtectedOpenVpnPassword, OpenVpnPasswordEntropy);

        return stored.Settings with
        {
            TransportKey = key,
            LanSocksPassword = lanPassword,
            UpstreamOpenVpn = stored.Settings.UpstreamOpenVpn with { Password = openVpnPassword },
        };
```

2. In `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs` line 210:
Merge existing OpenVPN password when incoming password is empty:
```csharp
            if (string.IsNullOrEmpty(settings.TransportKey) && existing is not null)
            {
                settings = settings with { TransportKey = existing.TransportKey };
            }

            if (string.IsNullOrEmpty(settings.LanSocksPassword) && existing is not null)
            {
                settings = settings with { LanSocksPassword = existing.LanSocksPassword };
            }

            if (string.IsNullOrEmpty(settings.UpstreamOpenVpn.Password) && existing is not null)
            {
                settings = settings with
                {
                    UpstreamOpenVpn = settings.UpstreamOpenVpn with
                    {
                        Password = existing.UpstreamOpenVpn.Password
                    }
                };
            }
```

3. In `src/PaqetFire.Core/Profiles/ProfileCatalogMessages.cs`:
Update `ConnectionProfileSummary` and `ProfileCatalogView`:
```csharp
public sealed record ConnectionProfileSummary(
    Guid Id,
    string Name,
    bool IsActive,
    bool IsDefault,
    bool HasTransportKey,
    bool HasSharingPassword,
    bool HasUpstreamOpenVpnPassword = false);

public sealed record ProfileCatalogView(IReadOnlyList<ConnectionProfileSummary> Profiles)
{
    public static ProfileCatalogView FromCatalog(ProfileCatalog catalog) => new(
        catalog.Profiles.Select(profile => new ConnectionProfileSummary(
            profile.Id,
            profile.Settings.ProfileName,
            profile.Id == catalog.ActiveProfileId,
            profile.Id == catalog.DefaultProfileId,
            !string.IsNullOrEmpty(profile.Settings.TransportKey),
            !string.IsNullOrEmpty(profile.Settings.LanSocksPassword),
            !string.IsNullOrEmpty(profile.Settings.UpstreamOpenVpn.Password))).ToArray());
}
```

4. In `src/PaqetFire.Desktop/MainWindow.Form.cs`:
In `PopulateFormFromBroker`:
```csharp
        hasSavedUpstreamOpenVpnPassword = settings.HasUpstreamOpenVpnPassword;
        UpstreamOpenVpnPasswordBox.Password = string.Empty;
        UpstreamOpenVpnPasswordBox.PlaceholderText = hasSavedUpstreamOpenVpnPassword
            ? "Saved securely · leave blank to keep it"
            : "Enter OpenVPN password";
```
In `MarkSecretsSaved`:
```csharp
        if (settings.UpstreamOpenVpn.Enabled)
        {
            if (!string.IsNullOrEmpty(settings.UpstreamOpenVpn.Password))
            {
                hasSavedUpstreamOpenVpnPassword = true;
            }
            UpstreamOpenVpnPasswordBox.Password = string.Empty;
            UpstreamOpenVpnPasswordBox.PlaceholderText = hasSavedUpstreamOpenVpnPassword
                ? "Saved securely · leave blank to keep it"
                : "Enter OpenVPN password";
        }
```
In `MainWindow.UpstreamOpenVpn.cs`:
Ensure toggling `UpstreamOpenVpnToggle` does NOT clear `hasSavedUpstreamOpenVpnPassword`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests --filter FullyQualifiedName~SaveAsync_PreservesUpstreamOpenVpnPassword`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PaqetFire.Broker/Configuration/ src/PaqetFire.Broker/Runtime/ src/PaqetFire.Core/ src/PaqetFire.Desktop/ tests/
git commit -m "fix(security): retain and persist upstream openvpn password across toggle and profile saves"
```

---

### Task 4: Display OpenVPN Exit IP & Remote Endpoint on Overview Page

**Files:**
- Modify: `src/PaqetFire.Broker/Diagnostics/TunnelProbe.cs:20-60`
- Modify: `src/PaqetFire.Broker/Diagnostics/LiveStatsService.cs:140-170`
- Modify: `src/PaqetFire.Desktop/ViewModels/LiveStatsViewModel.cs:95-105`
- Test: `tests/PaqetFire.Broker.Tests/TunnelProbeTests.cs`

**Interfaces:**
- Consumes: `status.OpenVpn?.State`, `settings.UpstreamOpenVpn`
- Produces: `LiveConnectionStats.PublicAddress` reflecting the OpenVPN exit IP; `LiveStatsViewModel.EndpointText` displaying the OpenVPN remote host and port; `LiveStatsViewModel.ProtocolText` displaying OpenVPN transport.

- [ ] **Step 1: Write test for LiveStatsViewModel endpoint and protocol when OpenVPN is active**

Add test in `tests/PaqetFire.Desktop.Tests/LiveStatsViewModelTests.cs` (or `LiveStatsTextTests.cs`):
```csharp
[Fact]
public void ApplyProfile_ShowsOpenVpnEndpointAndProtocol_WhenUpstreamOpenVpnEnabled()
{
    var vm = new LiveStatsViewModel();
    var settings = new PaqetFireSettingsView
    {
        ServerEndpoint = "51.254.208.73:8443",
        KcpMode = "fast2",
        UpstreamOpenVpn = new UpstreamOpenVpnOptions
        {
            Enabled = true,
            OvpnConfigText = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443"
        }
    };

    vm.ApplyProfile(settings);

    Assert.Equal("149.88.97.110:443 (TCP)", vm.EndpointText);
    Assert.Equal("OpenVPN (TCP)", vm.ProtocolText);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Desktop.Tests --filter FullyQualifiedName~ApplyProfile_ShowsOpenVpnEndpointAndProtocol`
Expected: FAIL because `ApplyProfile` currently uses `ServerEndpoint` and `KcpMode`.

- [ ] **Step 3: Implement OpenVPN exit IP probing and ViewModel display**

1. In `src/PaqetFire.Broker/Diagnostics/TunnelProbe.cs`:
Add a direct client (`HttpClient` without proxy) for probing public IP through the machine's active default route (OpenVPN TUN):
```csharp
    private readonly HttpClient directClient = new(new SocketsHttpHandler
    {
        UseProxy = false,
        ConnectTimeout = ProbeTimeout,
        PooledConnectionIdleTimeout = WarmConnectionLifetime,
        MaxConnectionsPerServer = 1,
    })
    {
        Timeout = ProbeTimeout,
    };

    public async ValueTask<IPAddress?> GetPublicAddressAsync(bool useDirectRoute, CancellationToken cancellationToken)
    {
        var targetClient = useDirectRoute ? directClient : client;
        try
        {
            var response = await targetClient.GetStringAsync(PublicAddressUri, cancellationToken).ConfigureAwait(false);
            if (IPAddress.TryParse(response.Trim(), out var address))
            {
                return address;
            }
        }
        catch (Exception)
        {
            // Transient probe failure
        }
        return null;
    }
```
Update `ITunnelProbe` interface to accept `bool useDirectRoute = false`.

2. In `src/PaqetFire.Broker/Diagnostics/LiveStatsService.cs`:
In `ScheduleProbes`, pass `useDirectRoute: owner.IsOpenVpnConnected`:
```csharp
    var isOpenVpn = status.OpenVpn?.State == EngineState.Running;
    current.ScheduleProbes(now, isOpenVpn);
```
So when OpenVPN is running, `PublicAddress` queries `directClient`, hitting `https://api4.ipify.org` over the OpenVPN tunnel adapter. The returned IP is the OpenVPN server's exit IP, and `TryFindCountryAsync` resolves its country code!

3. In `src/PaqetFire.Desktop/ViewModels/LiveStatsViewModel.cs`:
Update `ApplyProfile`:
```csharp
    public void ApplyProfile(PaqetFireSettingsView? settings)
    {
        if (settings?.UpstreamOpenVpn?.Enabled == true &&
            !string.IsNullOrWhiteSpace(settings.UpstreamOpenVpn.OvpnConfigText))
        {
            var (remoteHost, remotePort, proto) = UpstreamOpenVpnValidator.ParseServerInfo(settings.UpstreamOpenVpn.OvpnConfigText);
            var port = remotePort ?? 1194;
            var protocol = (proto ?? "tcp").ToUpperInvariant();
            EndpointText = !string.IsNullOrWhiteSpace(remoteHost) ? $"{remoteHost}:{port} ({protocol})" : settings.ServerEndpoint;
            ProtocolText = $"OpenVPN ({protocol})";
            return;
        }

        ProtocolText = LiveStatsText.FormatProtocol(settings?.KcpMode, settings?.Advanced.KcpBlock);
        EndpointText = string.IsNullOrWhiteSpace(settings?.ServerEndpoint) ? "—" : settings.ServerEndpoint;
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Desktop.Tests --filter FullyQualifiedName~ApplyProfile_ShowsOpenVpnEndpointAndProtocol`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PaqetFire.Broker/Diagnostics/ src/PaqetFire.Desktop/ViewModels/ tests/
git commit -m "feat(ui): display upstream openvpn exit ip, country, and remote endpoint on overview card"
```

---

### Task 5: Expose OpenVPN Engine and DCO Driver in Diagnostics UI

**Files:**
- Modify: `src/PaqetFire.Desktop/MainWindow.xaml:515-535,920-940,980-1045`
- Modify: `src/PaqetFire.Desktop/MainWindow.xaml.cs:920-950`
- Modify: `src/PaqetFire.Desktop/MainWindow.Setup.cs:20-50`
- Modify: `src/PaqetFire.Desktop/ViewModels/ConnectionViewModel.cs:140-160,1385-1440`
- Test: `tests/PaqetFire.Desktop.Tests/ConnectionViewModelTests.cs`

**Interfaces:**
- Consumes: `snapshot.Engines`, `snapshot.Prerequisites`
- Produces: `ViewModel.OpenVpnStatusText` bound to the OpenVPN Diagnostics card; OpenVPN DCO Driver displayed under "Required software" with status and action button.

- [ ] **Step 1: Write test for ViewModel.OpenVpnStatusText**

Add test in `tests/PaqetFire.Desktop.Tests/ConnectionViewModelTests.cs`:
```csharp
[Fact]
public void SnapshotWithOpenVpn_UpdatesOpenVpnStatusText()
{
    var vm = new ConnectionViewModel(...);
    var openVpnStatus = new EngineStatus(EngineKind.OpenVpn, EngineState.Running, "2.7.8", Detail: "Initialization Sequence Completed");
    var snapshot = new BrokerSnapshot(
        Engines: [openVpnStatus],
        ...
    );

    vm.ApplySnapshot(snapshot);

    Assert.Equal("Running", vm.OpenVpnStatusText);
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Desktop.Tests --filter FullyQualifiedName~SnapshotWithOpenVpn_UpdatesOpenVpnStatusText`
Expected: FAIL because `OpenVpnStatusText` does not exist on `ConnectionViewModel`.

- [ ] **Step 3: Implement OpenVPN status in ConnectionViewModel and Diagnostics UI**

1. In `src/PaqetFire.Desktop/ViewModels/ConnectionViewModel.cs`:
Add property:
```csharp
    private string openVpnStatusText = "Stopped";
    public string OpenVpnStatusText
    {
        get => openVpnStatusText;
        private set => SetProperty(ref openVpnStatusText, value);
    }
```
In `ApplySnapshot`:
```csharp
        var openVpn = snapshot.Engines.FirstOrDefault(engine => engine.Engine == EngineKind.OpenVpn);
        OpenVpnStatusText = openVpn switch
        {
            not null => EngineStateText.GetDisplayName(openVpn.State),
            _ when snapshot.Settings?.UpstreamOpenVpn.Enabled != true => "Disabled",
            _ => "Stopped",
        };
```

2. In `src/PaqetFire.Desktop/MainWindow.xaml`:
Under "Health and activity" (lines 921-935):
Update grid columns to 5:
```xml
            <Grid ColumnSpacing="12" HorizontalAlignment="Stretch">
              <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
                <ColumnDefinition Width="*" />
              </Grid.ColumnDefinitions>
              <Border Padding="16" CornerRadius="16" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="5"><TextBlock Text="Broker service" Opacity="0.65"/><TextBlock Text="{x:Bind ViewModel.BrokerStatusText, Mode=OneWay}" FontSize="18" FontWeight="SemiBold"/></StackPanel>
              </Border>
              <Border Grid.Column="1" Padding="16" CornerRadius="16" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="5"><TextBlock Text="Paqet" Opacity="0.65"/><TextBlock Text="{x:Bind ViewModel.PaqetStatusText, Mode=OneWay}" FontSize="18" FontWeight="SemiBold"/></StackPanel>
              </Border>
              <Border Grid.Column="2" Padding="16" CornerRadius="16" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="5"><TextBlock Text="Xray" Opacity="0.65"/><TextBlock Text="{x:Bind ViewModel.XrayStatusText, Mode=OneWay}" FontSize="18" FontWeight="SemiBold"/></StackPanel>
              </Border>
              <Border Grid.Column="3" Padding="16" CornerRadius="16" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="5"><TextBlock Text="ProxiFyre" Opacity="0.65"/><TextBlock Text="{x:Bind ViewModel.RoutingStatusText, Mode=OneWay}" FontSize="18" FontWeight="SemiBold" TextWrapping="Wrap"/></StackPanel>
              </Border>
              <Border Grid.Column="4" Padding="16" CornerRadius="16" Background="{ThemeResource CardBackgroundFillColorDefaultBrush}">
                <StackPanel Spacing="5"><TextBlock Text="OpenVPN" Opacity="0.65"/><TextBlock Text="{x:Bind ViewModel.OpenVpnStatusText, Mode=OneWay}" FontSize="18" FontWeight="SemiBold" TextWrapping="Wrap"/></StackPanel>
              </Border>
            </Grid>
```
3. In `MainWindow.xaml`:
Update `ProtonVpnHelpBar.Message`:
```xml
Message="Requires a free Proton account. In the Proton dashboard, select Windows as the platform and TCP as the protocol when downloading the configuration file (TCP is required; UDP will not work through the intermediate proxy). Enter your OpenVPN / IKEv2 credentials, not your Proton login password."
```
Under "Required software" (lines 983-1045):
Add Row 2 for OpenVPN DCO Driver:
```xml
                  <Border Grid.Row="2" Padding="16" CornerRadius="14" Background="{ThemeResource SubtleFillColorSecondaryBrush}">
                    <Grid RowSpacing="8">
                      <Grid.RowDefinitions><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/><RowDefinition Height="Auto"/></Grid.RowDefinitions>
                      <Grid ColumnSpacing="10"><Grid.ColumnDefinitions><ColumnDefinition Width="*"/><ColumnDefinition Width="Auto"/></Grid.ColumnDefinitions>
                        <TextBlock Text="OpenVPN DCO Driver" FontWeight="SemiBold" />
                        <TextBlock x:Name="OpenVpnDcoPrerequisiteStatusText" Grid.Column="1" Text="Checking…" Opacity="0.7" />
                      </Grid>
                      <TextBlock x:Name="OpenVpnDcoPrerequisiteDetailText" Grid.Row="1" Text="OpenVPN Data Channel Offload kernel driver." FontSize="12" Opacity="0.65" TextWrapping="Wrap" />
                      <Button x:Name="OpenVpnDcoPrerequisiteButton" Grid.Row="2" Tag="ovpn-dco" Content="Install bundled driver" HorizontalAlignment="Left"
                              IsEnabled="False" Click="PrerequisiteActionButton_Click" />
                    </Grid>
                  </Border>
```
4. In `src/PaqetFire.Desktop/MainWindow.Setup.cs`:
Bind `OpenVpnDcoPrerequisiteStatusText`, `OpenVpnDcoPrerequisiteDetailText`, and `OpenVpnDcoPrerequisiteButton` to the `"ovpn-dco"` prerequisite status from snapshot.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Desktop.Tests --filter FullyQualifiedName~SnapshotWithOpenVpn_UpdatesOpenVpnStatusText`
Expected: PASS

- [ ] **Step 5: Commit**

```bash
git add src/PaqetFire.Desktop/ tests/
git commit -m "feat(diagnostics): add openvpn health card and dco driver check to diagnostics ui"
```

---

### Task 6: Version Bump, Full Test Suite Verification & MSI Build

**Files:**
- Modify: `Directory.Build.props` / `PaqetFire.Version.props` (bump from `0.6.34` to `0.6.35`)
- Output: `artifacts/installer/PaqetFire-0.6.35-x64.msi`

**Interfaces:**
- Consumes: All updated projects and staged payloads
- Produces: Signed/built MSI installer ready for deployment

- [ ] **Step 1: Bump version to 0.6.35**

Update version in `PaqetFire.Version.props`:
```xml
<ProductVersion>0.6.35</ProductVersion>
```

- [ ] **Step 2: Run all solution tests**

Run: `dotnet test PaqetFire.slnx`
Expected: All 200+ unit and integration tests PASS.

- [ ] **Step 3: Publish Desktop and Broker, then build WiX MSI**

Run:
```powershell
dotnet publish src/PaqetFire.Desktop -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o artifacts/publish/desktop
dotnet publish src/PaqetFire.Broker -c Release -r win-x64 --self-contained -p:PublishSingleFile=false -o artifacts/publish/broker
dotnet build installer/PaqetFire.Installer/PaqetFire.Installer.wixproj -c Release -p:DesktopPublishDir=artifacts/publish/desktop -p:BrokerPublishDir=artifacts/publish/broker -p:ProductVersion=0.6.35
```
Expected: `artifacts/installer/PaqetFire-0.6.35-x64.msi` successfully generated.

- [ ] **Step 4: Commit and tag release**

```bash
git add PaqetFire.Version.props
git commit -m "chore(release): bump version to 0.6.35 with standalone openvpn and diagnostics"
```
