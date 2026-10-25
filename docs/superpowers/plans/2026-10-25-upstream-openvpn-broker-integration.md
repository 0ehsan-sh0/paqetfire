# Upstream OpenVPN Broker Integration & Multi-hop Routing Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Connect PaqetFire's Upstream OpenVPN engine into the background Broker runtime and Xray configuration so that enabling "Upstream OpenVPN" routes all foreign traffic through Proton VPN over Paqet transport while keeping domestic Iranian traffic direct.

**Architecture:** 
1. The Broker locates `openvpn.exe` (from bundled directory, standard `C:\Program Files\OpenVPN\bin\openvpn.exe`, or PATH) via `OpenVpnLocator`.
2. When `settings.UpstreamOpenVpn.Enabled` is true, the broker generates OpenVPN runtime configs pointing to Paqet's local authenticated SOCKS proxy (`127.0.0.1:1080 <socks_auth_file>`) and Proton credentials (`auth-user-pass <proton_auth_file>`), and supervises `OpenVpnProcessAdapter`.
3. `XrayJsonConfigurationWriter` emits a split outbound policy: Iranian destinations (`geosite:category-ir`, `geoip:ir`) route to `direct` with `sendThrough: <physical_lan_ip>`, while foreign destinations route to `openvpn-out` (freedom outbound without `sendThrough`), allowing the Windows routing table to pass foreign traffic through the OpenVPN TUN adapter.

**Tech Stack:** .NET 10, C# 13, OpenVPN 2.7.x, Xray-core, Windows DPAPI, xUnit.v3.

**Spec:** [`docs/adr/0005-upstream-openvpn-integration.md`](file:///E:/Developing/Projects/paqetfire/docs/adr/0005-upstream-openvpn-integration.md)

## Global Constraints
- Target Framework: `net10.0-windows10.0.26100.0`, Min: `10.0.19041.0`.
- OpenVPN credentials and local SOCKS auth tokens must be stored in protected files under `%ProgramData%\PaqetFire\openvpn\`.
- Zero attribution to Psiphon.
- When `UpstreamOpenVpn.Enabled` is false, the system executes the classic 3-engine Paqet -> Xray -> ProxiFyre pipeline with foreign traffic exiting France VPS directly.
- All code changes must compile with 0 warnings and pass all existing test suites.

---

### File Structure Map

| File | Responsibility |
|---|---|
| `src/PaqetFire.Broker/Engines/OpenVpnLocator.cs` | Finds `openvpn.exe` across bundled payload, Program Files, and PATH |
| `src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs` | Injects `socks-proxy 127.0.0.1 <port> <socksAuthFile>` and `auth-user-pass <protonAuthFile>` |
| `src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs` | Emits `openvpn-out` freedom outbound when `IsOpenVpnActive` is true |
| `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs` | Writes OpenVPN runtime files and manages 4-engine connect/disconnect lifecycle |
| `src/PaqetFire.Broker/Program.cs` | Registers `OpenVpnProcessAdapter` and wires it to `ConnectionController` and `PaqetFireRuntime` |
| `tests/PaqetFire.Broker.Tests/OpenVpnLocatorTests.cs` | Unit tests for OpenVPN executable location logic |
| `tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs` | Unit tests for SOCKS auth and Proton credentials injection |
| `tests/PaqetFire.Core.Tests/XrayOpenVpnRoutingTests.cs` | Unit tests for Xray outbound selection when OpenVPN is active |

---

### Task 1: OpenVPN Executable & Driver Discovery (`OpenVpnLocator`)

**Files:**
- Create: `src/PaqetFire.Broker/Engines/OpenVpnLocator.cs`
- Test: `tests/PaqetFire.Broker.Tests/OpenVpnLocatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace PaqetFire.Broker.Engines;

  public static class OpenVpnLocator
  {
      public static string? FindExecutable(string payloadRoot);
  }
  ```

- [ ] **Step 1: Write failing unit test for `OpenVpnLocator`**

```csharp
namespace PaqetFire.Broker.Tests;

using PaqetFire.Broker.Engines;
using Xunit;

public sealed class OpenVpnLocatorTests
{
    [Fact]
    public void FindExecutable_ReturnsInstalledPath_WhenDefaultExists()
    {
        var tempPayload = Path.Combine(Path.GetTempPath(), "test-payload-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempPayload);
        try
        {
            var found = OpenVpnLocator.FindExecutable(tempPayload);
            // On Windows test machine with OpenVPN installed at C:\Program Files\OpenVPN\bin\openvpn.exe
            if (File.Exists(@"C:\Program Files\OpenVPN\bin\openvpn.exe"))
            {
                Assert.Equal(@"C:\Program Files\OpenVPN\bin\openvpn.exe", found);
            }
        }
        finally
        {
            if (Directory.Exists(tempPayload))
            {
                Directory.Delete(tempPayload, true);
            }
        }
    }

    [Fact]
    public void FindExecutable_PrefersPayload_WhenPresent()
    {
        var tempPayload = Path.Combine(Path.GetTempPath(), "test-payload-" + Guid.NewGuid().ToString("N"));
        var payloadExe = Path.Combine(tempPayload, "engines", "openvpn", "x64", "openvpn.exe");
        Directory.CreateDirectory(Path.GetDirectoryName(payloadExe)!);
        File.WriteAllText(payloadExe, "dummy");
        try
        {
            var found = OpenVpnLocator.FindExecutable(tempPayload);
            Assert.Equal(payloadExe, found);
        }
        finally
        {
            if (Directory.Exists(tempPayload))
            {
                Directory.Delete(tempPayload, true);
            }
        }
    }
}
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj --filter "FullyQualifiedName~OpenVpnLocatorTests"`
Expected: FAIL (type `OpenVpnLocator` does not exist).

- [ ] **Step 3: Implement `OpenVpnLocator`**

Create `src/PaqetFire.Broker/Engines/OpenVpnLocator.cs`:
```csharp
namespace PaqetFire.Broker.Engines;

public static class OpenVpnLocator
{
    private static readonly string[] StandardLocations =
    [
        @"C:\Program Files\OpenVPN\bin\openvpn.exe",
        @"C:\Program Files (x86)\OpenVPN\bin\openvpn.exe",
    ];

    public static string? FindExecutable(string payloadRoot)
    {
        if (!string.IsNullOrWhiteSpace(payloadRoot))
        {
            var bundled = Path.Combine(payloadRoot, "engines", "openvpn", "x64", "openvpn.exe");
            if (File.Exists(bundled))
            {
                return bundled;
            }
        }

        foreach (var location in StandardLocations)
        {
            if (File.Exists(location))
            {
                return location;
            }
        }

        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            return null;
        }

        foreach (var dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(dir, "openvpn.exe");
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj --filter "FullyQualifiedName~OpenVpnLocatorTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Broker/Engines/OpenVpnLocator.cs tests/PaqetFire.Broker.Tests/OpenVpnLocatorTests.cs
git commit -m "feat(broker): implement OpenVpnLocator discovery helper"
```

---

### Task 2: OpenVPN SOCKS & Proton Credentials Configuration Writing

**Files:**
- Modify: `src/PaqetFire.Core/Configuration/IOpenVpnConfigurationWriter.cs`
- Modify: `src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs`
- Modify: `tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs`

**Interfaces:**
- Consumes: SOCKS port, SOCKS auth path, Proton auth path.
- Produces: Complete OpenVPN runtime config containing `socks-proxy 127.0.0.1 <port> "<socksAuthPath>"` and `auth-user-pass "<protonAuthPath>"`.

- [ ] **Step 1: Write failing unit test in `OpenVpnConfigurationWriterTests`**

Update `tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs` to test SOCKS auth parameter:

```csharp
    [Fact]
    public void GenerateRuntimeConfig_InjectsSocksAuthFile_WhenProvided()
    {
        var original = "client\ndev tun\nremote 149.88.97.110 443\n";
        var writer = new OpenVpnConfigurationWriter();
        var result = writer.GenerateRuntimeConfig(
            original,
            1080,
            @"C:\ProgramData\PaqetFire\openvpn\proton-auth.txt",
            @"C:\ProgramData\PaqetFire\openvpn\socks-auth.txt",
            "PaqetFire-VPN");

        Assert.Contains("socks-proxy 127.0.0.1 1080 \"C:\\ProgramData\\PaqetFire\\openvpn\\socks-auth.txt\"", result);
        Assert.Contains("auth-user-pass \"C:\\ProgramData\\PaqetFire\\openvpn\\proton-auth.txt\"", result);
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~GenerateRuntimeConfig_InjectsSocksAuthFile"`
Expected: FAIL (method signature overload does not match).

- [ ] **Step 3: Update `IOpenVpnConfigurationWriter` and `OpenVpnConfigurationWriter`**

In `src/PaqetFire.Core/Configuration/IOpenVpnConfigurationWriter.cs`:
```csharp
namespace PaqetFire.Core.Configuration;

public interface IOpenVpnConfigurationWriter
{
    string GenerateRuntimeConfig(
        string originalOvpnText,
        int socksProxyPort,
        string authFilePath,
        string? socksAuthFilePath = null,
        string adapterName = "PaqetFire-VPN");
}
```

In `src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs`:
Update `GenerateRuntimeConfig`:
```csharp
    public string GenerateRuntimeConfig(
        string originalOvpnText,
        int socksProxyPort,
        string authFilePath,
        string? socksAuthFilePath = null,
        string adapterName = "PaqetFire-VPN")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(originalOvpnText);
        ArgumentException.ThrowIfNullOrWhiteSpace(authFilePath);

        var lines = originalOvpnText.Split(["\r\n", "\r", "\n"], StringSplitOptions.None);
        var builder = new StringBuilder();

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith('#') || trimmed.StartsWith(';'))
            {
                builder.AppendLine(line);
                continue;
            }

            var shouldFilter = false;
            foreach (var directive in DirectivesToFilter)
            {
                if (trimmed.StartsWith(directive, StringComparison.OrdinalIgnoreCase))
                {
                    shouldFilter = true;
                    break;
                }
            }

            if (!shouldFilter)
            {
                builder.AppendLine(line);
            }
        }

        builder.AppendLine();
        builder.AppendLine("# === PaqetFire Upstream OpenVPN Runtime Directives ===");
        builder.AppendLine("dev-type tun");
        builder.AppendLine($"dev {adapterName}");
        if (string.IsNullOrWhiteSpace(socksAuthFilePath))
        {
            builder.AppendLine($"socks-proxy 127.0.0.1 {socksProxyPort}");
        }
        else
        {
            builder.AppendLine($"socks-proxy 127.0.0.1 {socksProxyPort} \"{socksAuthFilePath}\"");
        }
        builder.AppendLine($"auth-user-pass \"{authFilePath}\"");
        builder.AppendLine("route-metric 1");

        return builder.ToString();
    }
```

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~OpenVpnConfigurationWriterTests"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Core/Configuration/IOpenVpnConfigurationWriter.cs src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs
git commit -m "feat(core): support SOCKS proxy credentials file in OpenVpnConfigurationWriter"
```

---

### Task 3: Xray Split-Outbound Routing Policy for Upstream OpenVPN

**Files:**
- Modify: `src/PaqetFire.Core/Configuration/PaqetFireSettings.cs`
- Modify: `src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs`
- Test: `tests/PaqetFire.Core.Tests/XrayPhysicalBindingTests.cs`

**Interfaces:**
- Consumes: `XrayRoutingPolicy.IsOpenVpnActive`
- Produces:
  - If `IsOpenVpnActive == false`: foreign rules target `paqet` outbound (port 1080).
  - If `IsOpenVpnActive == true`: foreign rules target `openvpn-out` (freedom outbound without `sendThrough`), while Iran rules continue targeting `direct` (with `sendThrough: PhysicalAdapterIp`).

- [ ] **Step 1: Write failing unit test in `XrayPhysicalBindingTests`**

```csharp
    [Fact]
    public void RoutingRules_WhenOpenVpnActive_RouteForeignTrafficToOpenVpnOutbound()
    {
        var policy = new XrayRoutingPolicy(
            RegionalPreset: RegionalRoutingPreset.IranDirect,
            DomainStrategy: XrayDomainStrategy.IPIfNonMatch,
            BypassLan: true,
            BlockAds: false,
            BlockQuic: false,
            DirectBitTorrent: false,
            PhysicalAdapterIp: "192.168.1.10",
            IsOpenVpnActive: true);

        var json = new XrayJsonConfigurationWriter().Write(policy);
        using var doc = JsonDocument.Parse(json);
        var routing = doc.RootElement.GetProperty("routing");
        var rules = routing.GetProperty("rules");

        var hasOpenVpnOutbound = false;
        var outbounds = doc.RootElement.GetProperty("outbounds");
        foreach (var outbound in outbounds.EnumerateArray())
        {
            if (outbound.GetProperty("tag").GetString() == "openvpn-out")
            {
                hasOpenVpnOutbound = true;
                Assert.Equal("freedom", outbound.GetProperty("protocol").GetString());
                Assert.False(outbound.TryGetProperty("sendThrough", out _));
            }
        }

        Assert.True(hasOpenVpnOutbound, "Expected 'openvpn-out' outbound when IsOpenVpnActive is true.");
    }
```

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~RoutingRules_WhenOpenVpnActive"`
Expected: FAIL (`IsOpenVpnActive` does not exist on `XrayRoutingPolicy`).

- [ ] **Step 3: Update `XrayRoutingPolicy` and `XrayJsonConfigurationWriter`**

In `src/PaqetFire.Core/Configuration/PaqetFireSettings.cs`:
Add `bool IsOpenVpnActive = false` to `XrayRoutingPolicy`:
```csharp
public sealed record XrayRoutingPolicy(
    RegionalRoutingPreset RegionalPreset,
    XrayDomainStrategy DomainStrategy,
    bool BypassLan,
    bool BlockAds,
    bool BlockQuic,
    bool DirectBitTorrent,
    LanSocksShare? LanShare = null,
    LanSocksShare? HotspotShare = null,
    IReadOnlyList<string>? DirectRouteDestinations = null,
    XrayDnsPolicy? Dns = null,
    EngineSocksCredentials? EngineCredentials = null,
    bool BlockMalware = false,
    string? PhysicalAdapterIp = null,
    bool IsOpenVpnActive = false);
```

In `src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs`:
1. When `policy.IsOpenVpnActive` is true, write an additional outbound:
```csharp
            if (policy.IsOpenVpnActive)
            {
                writer.WriteStartObject();
                writer.WriteString("tag", "openvpn-out");
                writer.WriteString("protocol", "freedom");
                writer.WriteEndObject();
            }
```
2. When emitting default catch-all / foreign rules:
```csharp
            var defaultForeignOutbound = policy.IsOpenVpnActive ? "openvpn-out" : PaqetOutboundTag;
```
Replace the standard `paqet` foreign rule destinations with `defaultForeignOutbound`.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~RoutingRules_WhenOpenVpnActive"`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Core/Configuration/PaqetFireSettings.cs src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs tests/PaqetFire.Core.Tests/XrayPhysicalBindingTests.cs
git commit -m "feat(xray): support openvpn-out outbound when Upstream OpenVPN is active"
```

---

### Task 4: Broker Runtime Integration & Dynamic OpenVPN Lifecycle

**Files:**
- Modify: `src/PaqetFire.Broker/Program.cs`
- Modify: `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs`

**Interfaces:**
- Consumes: `OpenVpnLocator`, `OpenVpnProcessAdapter`, `IOpenVpnConfigurationWriter`, `UpstreamOpenVpnOptions`.
- Produces:
  - Generates runtime OpenVPN configuration and credentials under `%ProgramData%\PaqetFire\openvpn\`.
  - Configures `ConnectionController` with the dynamic `OpenVpnProcessAdapter`.
  - Automatically activates 4-engine sequencing when `settings.UpstreamOpenVpn.Enabled` is true.

- [ ] **Step 1: Write integration test in `tests/PaqetFire.Broker.Tests/RuntimeRecoveryTests.cs`**

Write a test verifying that when `settings.UpstreamOpenVpn.Enabled == true` and OpenVPN executable is present, `BuildEngineConfigurationsAsync` passes `IsOpenVpnActive: true` and generates OpenVPN config.

- [ ] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj --filter "FullyQualifiedName~UpstreamOpenVpn"`
Expected: FAIL.

- [ ] **Step 3: Update `Program.cs` and `PaqetFireRuntime.cs`**

In `src/PaqetFire.Broker/Program.cs`:
1. Register `IOpenVpnConfigurationWriter`:
   `builder.Services.AddSingleton<IOpenVpnConfigurationWriter, OpenVpnConfigurationWriter>();`
2. Resolve `openVpnExecutable`:
   `var openVpnExe = OpenVpnLocator.FindExecutable(paths.PayloadRoot);`
3. Register `OpenVpnProcessAdapter`:
   ```csharp
   builder.Services.AddSingleton(services =>
   {
       var options = BundledEngineOptions.CreateOpenVpn(
           openVpnExe ?? @"C:\Program Files\OpenVPN\bin\openvpn.exe",
           Path.Combine(dataDirectory, "openvpn", "runtime.ovpn"),
           Path.Combine(dataDirectory, "openvpn", "proton-auth.txt"));
       var adapter = new OpenVpnProcessAdapter(options);
       adapter.UnexpectedExit += (_, _) => services.GetRequiredService<BrokerSignal>().Notify();
       return adapter;
   });
   ```
4. Update `ConnectionController` registration:
   ```csharp
   builder.Services.AddSingleton<IConnectionController>(services => new ConnectionController(
       services.GetRequiredService<PaqetProcessAdapter>(),
       services.GetRequiredService<XrayProcessAdapter>(),
       services.GetRequiredService<ProxiFyreProcessAdapter>(),
       services.GetRequiredService<OpenVpnProcessAdapter>()));
   ```

In `src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs`:
1. Inject `OpenVpnProcessAdapter` and `IOpenVpnConfigurationWriter`.
2. In `BuildEngineConfigurationsAsync`, if `settings.UpstreamOpenVpn.Enabled` is true:
   - Pass `IsOpenVpnActive: true` and `PhysicalAdapterIp: network.LocalIpv4Address` to `XrayRoutingPolicy`.
   - Write `proton-auth.txt` containing Proton username and password.
   - Write `socks-auth.txt` containing `engineCredentials.Username` and `engineCredentials.Password`.
   - Call `openVpnWriter.GenerateRuntimeConfig` and write `runtime.ovpn`.
3. In `CreateProxiFyreConfiguration`: Ensure `openvpn.exe` is added to ProxiFyre's exclusions so the OpenVPN process itself is never captured by ProxiFyre.

- [ ] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj`
Expected: PASS.

- [ ] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Broker/Program.cs src/PaqetFire.Broker/Runtime/PaqetFireRuntime.cs
git commit -m "feat(broker): wire OpenVpnProcessAdapter and runtime configuration into broker"
```

---

### Task 5: End-to-End Verification, Version Bump & Installer Build

**Files:**
- Modify: `PaqetFire.Version.props` (bump to `0.6.32`)
- Build installer: `artifacts/installer/PaqetFire-0.6.32-x64.msi`

**Interfaces:**
- Produces: Installed and verified `PaqetFire-0.6.32-x64.msi` with working 4-engine Upstream OpenVPN capability.

- [ ] **Step 1: Run full test suite across the solution**

Run: `dotnet test PaqetFire.slnx`
Expected: All tests PASS with 0 errors.

- [ ] **Step 2: Bump version in `PaqetFire.Version.props` to `0.6.32`**

- [ ] **Step 3: Publish Desktop and Broker, and build WiX MSI installer**

```powershell
dotnet publish src/PaqetFire.Desktop/PaqetFire.Desktop.csproj -c Release -r win-x64 -p:Platform=x64 --self-contained true -o artifacts/publish/win-x64/desktop -p:DebugType=None -p:DebugSymbols=false
dotnet publish src/PaqetFire.Broker/PaqetFire.Broker.csproj -c Release -r win-x64 --self-contained true -o artifacts/publish/win-x64/broker -p:DebugType=None -p:DebugSymbols=false
dotnet build installer/PaqetFire.Installer/PaqetFire.Installer.wixproj -c Release
```

- [ ] **Step 4: Commit and tag release**

```bash
git commit -am "chore: release v0.6.32 with native Upstream OpenVPN multi-hop routing"
```
