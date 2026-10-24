# Upstream OpenVPN Integration Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Integrate a toggleable Upstream OpenVPN engine into PaqetFire that allows users to import Proton VPN `.ovpn` configurations and route non-Iran traffic through a dedicated `PaqetFire-VPN` Wintun adapter via Paqet transport while preserving Xray Iran-direct bypass.

**Architecture:** Paqet transport connects first as the anti-censorship carrier. OpenVPN is supervised by the broker with `--windows-driver wintun` and `--socks-proxy 127.0.0.1 1080`, creating a dynamic `PaqetFire-VPN` adapter with default routes for foreign exit traffic. Xray binds its `direct` outbound to the active physical adapter IP (`sendThrough`) to bypass OpenVPN for domestic Iranian destinations (`geoip:ir`, `geosite:category-ir`) and local LAN devices.

**Tech Stack:** .NET 10, C# 13, WinUI 3, Windows DPAPI, OpenVPN 2.6+, Wintun, Xray-core, xUnit.v3.

**Spec:** [`docs/adr/0005-upstream-openvpn-integration.md`](file:///E:/Developing/Projects/paqetfire/docs/adr/0005-upstream-openvpn-integration.md)

## Global Constraints
- Target Framework: `net10.0-windows10.0.26100.0`, Min: `10.0.19041.0`.
- All credentials (OpenVPN username, password, sensitive ovpn tokens) must be stored encrypted using Windows DPAPI.
- Bundled adapter driver is strictly `wintun.dll` creating the isolated `PaqetFire-VPN` adapter; no kernel INF installers or reboots.
- Sockets for domestic destinations must explicitly bind to the active physical adapter IP (`sendThrough`) in Xray configuration to guarantee Iran direct bypass.
- Upstream OpenVPN is toggleable per-profile; when disabled, the standard 3-engine Paqet -> Xray -> ProxiFyre pipeline executes.
- Exit verification and overview cards must reflect the OpenVPN provider exit and never attribute to Psiphon.
- Proton VPN onboarding banner must link to `https://protonvpn.com/support/openvpn-windows-setup` and instruct the user about obtaining free credentials.

---

### Task 1: Upstream OpenVPN Domain Model & Validator

**Files:**
- Create: `src/PaqetFire.Core/Configuration/UpstreamOpenVpnOptions.cs`
- Create: `src/PaqetFire.Core/Configuration/UpstreamOpenVpnValidator.cs`
- Modify: `src/PaqetFire.Core/Configuration/PaqetProfile.cs`
- Modify: `src/PaqetFire.Core/Configuration/PaqetProfileValidator.cs`
- Test: `tests/PaqetFire.Core.Tests/UpstreamOpenVpnValidatorTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace PaqetFire.Core.Configuration;

  public sealed record UpstreamOpenVpnOptions
  {
      public bool Enabled { get; init; }
      public string? OvpnConfigText { get; init; }
      public string? Username { get; init; }
      public string? Password { get; init; }
      public string? RemoteHost { get; init; }
      public int? RemotePort { get; init; }
      public string? Protocol { get; init; }
  }

  public static class UpstreamOpenVpnValidator
  {
      public static IReadOnlyList<string> Validate(UpstreamOpenVpnOptions? options);
      public static (string? RemoteHost, int? RemotePort, string? Protocol) ParseServerInfo(string ovpnContent);
  }
  ```

- [x] **Step 1: Write failing tests for UpstreamOpenVpnValidator and parsing**

```csharp
namespace PaqetFire.Core.Tests;

using PaqetFire.Core.Configuration;
using Xunit;

public sealed class UpstreamOpenVpnValidatorTests
{
    [Fact]
    public void DisabledOptions_RequireNoFields()
    {
        var options = new UpstreamOpenVpnOptions { Enabled = false };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Empty(errors);
    }

    [Fact]
    public void EnabledOptions_WithoutConfig_FailsValidation()
    {
        var options = new UpstreamOpenVpnOptions { Enabled = true };
        var errors = UpstreamOpenVpnValidator.Validate(options);
        Assert.Contains(errors, e => e.Contains("OpenVPN configuration content is required"));
    }

    [Fact]
    public void ParseServerInfo_ExtractsHostPortAndProtocol()
    {
        var sampleOvpn = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\n";
        var (host, port, proto) = UpstreamOpenVpnValidator.ParseServerInfo(sampleOvpn);
        Assert.Equal("149.88.97.110", host);
        Assert.Equal(443, port);
        Assert.Equal("tcp", proto);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~UpstreamOpenVpnValidatorTests"`
Expected: FAIL (types do not exist).

- [x] **Step 3: Implement UpstreamOpenVpnOptions and UpstreamOpenVpnValidator**

Implement `UpstreamOpenVpnOptions.cs` and `UpstreamOpenVpnValidator.cs` with regex parsing for `remote <host> <port>` and `proto <protocol>`. Update `PaqetProfile.cs` to include `public UpstreamOpenVpnOptions? UpstreamOpenVpn { get; init; }` and wire validation in `PaqetProfileValidator.cs`.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~UpstreamOpenVpnValidatorTests"`
Expected: PASS.

- [x] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Core/Configuration/UpstreamOpenVpnOptions.cs src/PaqetFire.Core/Configuration/UpstreamOpenVpnValidator.cs src/PaqetFire.Core/Configuration/PaqetProfile.cs src/PaqetFire.Core/Configuration/PaqetProfileValidator.cs tests/PaqetFire.Core.Tests/UpstreamOpenVpnValidatorTests.cs
git commit -m "feat(core): add UpstreamOpenVpnOptions and configuration validation"
```

---

### Task 2: OpenVPN Runtime Configuration & Auth Writer

**Files:**
- Create: `src/PaqetFire.Core/Configuration/IOpenVpnConfigurationWriter.cs`
- Create: `src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs`
- Test: `tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace PaqetFire.Core.Configuration;

  public interface IOpenVpnConfigurationWriter
  {
      string GenerateRuntimeConfig(
          string originalOvpnText,
          int socksProxyPort,
          string authFilePath,
          string adapterName = "PaqetFire-VPN");
  }
  ```

- [x] **Step 1: Write failing tests for OpenVpnConfigurationWriter**

```csharp
namespace PaqetFire.Core.Tests;

using PaqetFire.Core.Configuration;
using Xunit;

public sealed class OpenVpnConfigurationWriterTests
{
    [Fact]
    public void GenerateRuntimeConfig_InjectsSocksProxyAndWintunAdapter()
    {
        var writer = new OpenVpnConfigurationWriter();
        var original = "client\ndev tun\nproto tcp\nremote 149.88.97.110 443\n";
        var generated = writer.GenerateRuntimeConfig(original, 1080, @"C:\data\auth.txt", "PaqetFire-VPN");

        Assert.Contains("socks-proxy 127.0.0.1 1080", generated);
        Assert.Contains("windows-driver wintun", generated);
        Assert.Contains("dev-type tun", generated);
        Assert.Contains("dev PaqetFire-VPN", generated);
        Assert.Contains(@"auth-user-pass ""C:\data\auth.txt""", generated);
    }
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~OpenVpnConfigurationWriterTests"`
Expected: FAIL.

- [x] **Step 3: Implement OpenVpnConfigurationWriter**

Implement `OpenVpnConfigurationWriter.cs`:
Strip any existing `auth-user-pass`, `socks-proxy`, `http-proxy`, or `dev` lines, then append the authoritative PaqetFire directives:
- `socks-proxy 127.0.0.1 <port>`
- `windows-driver wintun`
- `dev-type tun`
- `dev <adapterName>`
- `auth-user-pass "<authFilePath>"`
- `route-metric 1`

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~OpenVpnConfigurationWriterTests"`
Expected: PASS.

- [x] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Core/Configuration/IOpenVpnConfigurationWriter.cs src/PaqetFire.Core/Configuration/OpenVpnConfigurationWriter.cs tests/PaqetFire.Core.Tests/OpenVpnConfigurationWriterTests.cs
git commit -m "feat(core): implement OpenVpnConfigurationWriter with socks-proxy and wintun injection"
```

---

### Task 3: Payload Manifest & Engine Kind Extension

**Files:**
- Modify: `src/PaqetFire.Core/Engines/EngineKind.cs`
- Modify: `src/PaqetFire.Broker/Engines/BundledEngineOptions.cs`
- Modify: `payload/payload-manifest.json`
- Test: `tests/PaqetFire.Broker.Tests/BundledEngineCompatibilityTests.cs`

**Interfaces:**
- Modifies: `EngineKind` enum: adds `OpenVpn`.
- Produces in `BundledEngineOptions`: `CreateOpenVpn(RuntimePaths paths)`.

- [x] **Step 1: Write test verifying EngineKind.OpenVpn is recognized**

```csharp
[Fact]
public void EngineKind_ContainsOpenVpn()
{
    Assert.True(Enum.IsDefined(typeof(EngineKind), EngineKind.OpenVpn));
}
```

- [x] **Step 2: Add EngineKind.OpenVpn and CreateOpenVpn**

Update `EngineKind.cs` to add `OpenVpn`.
Update `BundledEngineOptions.cs` to declare OpenVPN executable path (`engines/openvpn/x64/openvpn.exe`), configuration paths, and timeout values.

- [x] **Step 3: Run broker tests to verify compilation and baseline**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj`
Expected: PASS.

- [x] **Step 4: Commit changes**

```bash
git add src/PaqetFire.Core/Engines/EngineKind.cs src/PaqetFire.Broker/Engines/BundledEngineOptions.cs payload/payload-manifest.json
git commit -m "feat(engines): declare EngineKind.OpenVpn and payload configuration"
```

---

### Task 4: Xray Outbound Physical Interface Binding for Iran-Direct Bypass

**Files:**
- Modify: `src/PaqetFire.Core/Routing/RoutingPolicy.cs`
- Modify: `src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs`
- Test: `tests/PaqetFire.Core.Tests/XrayJsonConfigurationWriterTests.cs`

**Interfaces:**
- Consumes: Active physical LAN adapter IP from network detector.
- Produces in Xray config: When physical IP is provided, `direct` outbound includes `"sendThrough": "<physical_ip>"`.

- [x] **Step 1: Write failing test in XrayJsonConfigurationWriterTests**

```csharp
[Fact]
public void DirectOutbound_WithPhysicalAdapterIp_EmitsSendThrough()
{
    var policy = CreateSamplePolicy() with { PhysicalAdapterIp = "192.168.1.10" };
    var json = XrayJsonConfigurationWriter.Write(policy);
    
    // Direct outbound should contain sendThrough
    Assert.Contains("\"sendThrough\": \"192.168.1.10\"", json);
}
```

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~DirectOutbound_WithPhysicalAdapterIp"`
Expected: FAIL.

- [x] **Step 3: Implement physical IP binding in XrayJsonConfigurationWriter**

In `XrayJsonConfigurationWriter.cs`:
When writing the `direct` (`freedom`) outbound, if `policy.PhysicalAdapterIp` is not null or empty, emit `"sendThrough": policy.PhysicalAdapterIp`.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~DirectOutbound_WithPhysicalAdapterIp"`
Expected: PASS.

- [x] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Core/Routing/RoutingPolicy.cs src/PaqetFire.Core/Configuration/XrayJsonConfigurationWriter.cs tests/PaqetFire.Core.Tests/XrayJsonConfigurationWriterTests.cs
git commit -m "feat(xray): bind direct outbound to physical adapter IP for Iran bypass"
```

---

### Task 5: OpenVPN Process Adapter in Broker

**Files:**
- Create: `src/PaqetFire.Broker/Engines/OpenVpnProcessOptions.cs`
- Create: `src/PaqetFire.Broker/Engines/OpenVpnProcessAdapter.cs`
- Test: `tests/PaqetFire.Broker.Tests/OpenVpnProcessAdapterTests.cs`

**Interfaces:**
- Produces:
  ```csharp
  namespace PaqetFire.Broker.Engines;

  public sealed class OpenVpnProcessAdapter : IEngineAdapter, IDisposable
  {
      public EngineKind Kind => EngineKind.OpenVpn;
      public ValueTask StartAsync(CancellationToken cancellationToken);
      public ValueTask StopAsync(CancellationToken cancellationToken);
      public ValueTask<EngineStatus> GetStatusAsync(CancellationToken cancellationToken);
  }
  ```

- [x] **Step 1: Write unit tests for OpenVpnProcessAdapter lifecycle**

Write tests asserting adapter reports `EngineKind.OpenVpn`, transitions between `Stopped`, `Starting`, `Running`, and catches readiness signal (`Initialization Sequence Completed`).

- [x] **Step 2: Run test to verify it fails**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj --filter "FullyQualifiedName~OpenVpnProcessAdapterTests"`
Expected: FAIL.

- [x] **Step 3: Implement OpenVpnProcessAdapter**

Implement `OpenVpnProcessAdapter` following existing pattern in `PaqetProcessAdapter.cs` and `XrayProcessAdapter.cs`:
- Launch `openvpn.exe --config <path>` with suppressed console window.
- Stream stdout and watch for `"Initialization Sequence Completed"`.
- Throw timeout if readiness is not achieved within `ReadinessTimeout`.
- Clean shutdown on `StopAsync` by sending management signal or terminating child process.

- [x] **Step 4: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Broker.Tests/PaqetFire.Broker.Tests.csproj --filter "FullyQualifiedName~OpenVpnProcessAdapterTests"`
Expected: PASS.

- [x] **Step 5: Commit changes**

```bash
git add src/PaqetFire.Broker/Engines/OpenVpnProcessOptions.cs src/PaqetFire.Broker/Engines/OpenVpnProcessAdapter.cs tests/PaqetFire.Broker.Tests/OpenVpnProcessAdapterTests.cs
git commit -m "feat(broker): implement OpenVpnProcessAdapter engine supervisor"
```

---

### Task 6: 4-Engine Connection Controller & Sequencing

**Files:**
- Modify: `src/PaqetFire.Core/Connections/ConnectionController.cs`
- Modify: `src/PaqetFire.Core/Connections/ConnectionStatus.cs`
- Test: `tests/PaqetFire.Core.Tests/ConnectionControllerTests.cs`

**Interfaces:**
- Updates `ConnectionController` to accept an optional `IEngineAdapter? openVpn`.
- Connect sequence when OpenVPN is present:
  `Paqet` -> `Xray` -> `OpenVPN` -> `ProxiFyre`.
- Disconnect sequence:
  `ProxiFyre` -> `OpenVPN` -> `Xray` -> `Paqet`.

- [x] **Step 1: Write test for 4-engine connect and disconnect sequence**

```csharp
[Fact]
public async Task ConnectAsync_WithOpenVpn_StartsInCorrectOrder()
{
    var paqet = new MockEngineAdapter(EngineKind.Paqet);
    var xray = new MockEngineAdapter(EngineKind.Xray);
    var openVpn = new MockEngineAdapter(EngineKind.OpenVpn);
    var proxiFyre = new MockEngineAdapter(EngineKind.ProxiFyre);

    var controller = new ConnectionController(paqet, xray, proxiFyre, openVpn);
    await controller.ConnectAsync(CancellationToken.None);

    Assert.True(paqet.IsRunning);
    Assert.True(xray.IsRunning);
    Assert.True(openVpn.IsRunning);
    Assert.True(proxiFyre.IsRunning);
}
```

- [x] **Step 2: Implement 4-engine sequencing in ConnectionController**

In `ConnectionController.cs`:
Support nullable `_openVpn` adapter. If non-null, start after `_xray` and before `_proxiFyre`. In `RollBackConnectAsync` and `DisconnectAsync`, tear down in reverse order. Include `OpenVpnStatus` in `ConnectionStatus`.

- [x] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~ConnectionControllerTests"`
Expected: PASS.

- [x] **Step 4: Commit changes**

```bash
git add src/PaqetFire.Core/Connections/ConnectionController.cs src/PaqetFire.Core/Connections/ConnectionStatus.cs tests/PaqetFire.Core.Tests/ConnectionControllerTests.cs
git commit -m "feat(connections): support 4-engine sequence with Upstream OpenVPN"
```

---

### Task 7: Profile Secret Protection for OpenVPN Credentials

**Files:**
- Modify: `src/PaqetFire.Core/Profiles/ConnectionProfile.cs`
- Modify: `src/PaqetFire.Core/Profiles/ProfileCatalogJson.cs`
- Modify: `src/PaqetFire.Core/Profiles/IProfileSecretProtector.cs`
- Test: `tests/PaqetFire.Core.Tests/ProfileCatalogSecretProtectionTests.cs`

**Interfaces:**
- Produces: DPAPI encryption/decryption of `Username`, `Password`, and embedded private keys/auth in `UpstreamOpenVpnOptions`.

- [x] **Step 1: Write test for OpenVPN credentials encryption/decryption**

Write test verifying that serialized profile JSON stores cipher bytes (DPAPI) rather than raw username/password, and deserializes back to plain text in-memory.

- [x] **Step 2: Implement DPAPI protection in ProfileCatalogJson**

Update `ProfileCatalogJson.cs` and `ConnectionProfile.cs` to pass `UpstreamOpenVpn` credentials through `IProfileSecretProtector`.

- [x] **Step 3: Run test to verify it passes**

Run: `dotnet test tests/PaqetFire.Core.Tests/PaqetFire.Core.Tests.csproj --filter "FullyQualifiedName~ProfileCatalogSecretProtectionTests"`
Expected: PASS.

- [x] **Step 4: Commit changes**

```bash
git add src/PaqetFire.Core/Profiles/ConnectionProfile.cs src/PaqetFire.Core/Profiles/ProfileCatalogJson.cs tests/PaqetFire.Core.Tests/ProfileCatalogSecretProtectionTests.cs
git commit -m "feat(security): protect Upstream OpenVPN credentials using Windows DPAPI"
```

---

### Task 8: WinUI 3 Desktop UI - Upstream OpenVPN Card & Proton Setup Link

**Files:**
- Modify: `src/PaqetFire.Desktop/Views/ProfilesPage.xaml`
- Modify: `src/PaqetFire.Desktop/ViewModels/ProfileEditorViewModel.cs`

**Interfaces:**
- Adds "Upstream OpenVPN" card to profile editor:
  - Toggle: "Use Upstream OpenVPN" (Switches between direct Paqet exit and Proton VPN Canada exit).
  - Info banner: *"Requires a free Proton VPN account. Download an OpenVPN configuration file (TCP) and obtain your credentials from your account dashboard."*
  - Button: "Open Proton VPN Setup Guide" (invokes `Process.Start(new ProcessStartInfo("https://protonvpn.com/support/openvpn-windows-setup") { UseShellExecute = true })`).
  - Button: "Import .ovpn file" (opens Windows FileOpenPicker).
  - TextBoxes for OpenVPN Username and Password (PasswordBox).
  - Detected server summary badge.

- [x] **Step 1: Update ProfileEditorViewModel with Upstream OpenVPN properties and file picker**

Add observable properties:
- `bool IsUpstreamOpenVpnEnabled`
- `string? OpenVpnConfigText`
- `string? OpenVpnUsername`
- `string? OpenVpnPassword`
- `string? OpenVpnServerSummary`
- RelayCommands: `ImportOvpnFileCommand`, `OpenProtonGuideCommand`.

- [x] **Step 2: Update ProfilesPage.xaml with the Upstream OpenVPN Card**

Build the WinUI 3 XAML markup containing the toggle, info banner, buttons, and credential inputs with proper theme bindings.

- [x] **Step 3: Build the Desktop application project**

Run: `dotnet build src/PaqetFire.Desktop/PaqetFire.Desktop.csproj -c Release`
Expected: Build succeeded with 0 errors.

- [x] **Step 4: Commit changes**

```bash
git add src/PaqetFire.Desktop/Views/ProfilesPage.xaml src/PaqetFire.Desktop/ViewModels/ProfileEditorViewModel.cs
git commit -m "feat(ui): add Upstream OpenVPN card with Proton VPN onboarding guide"
```

---

### Task 9: Exit IP Verification & Diagnostics Attribution

**Files:**
- Modify: `src/PaqetFire.Core/Diagnostics/ConnectionVerification.cs`
- Modify: `src/PaqetFire.Desktop/ViewModels/OverviewViewModel.cs`
- Test: `tests/PaqetFire.Core.Tests/ConnectionVerificationTests.cs`

**Interfaces:**
- Verifies exit IP and country on Overview: when Upstream OpenVPN is active, tests verify public IP via the tunnel and display provider exit (e.g. ProtonVPN Canada), without any Psiphon naming or attribution.

- [x] **Step 1: Write test ensuring exit verification reflects active tunnel provider**

Write test verifying that `ConnectionVerification` queries public IP and properly parses the exit without static hardcoding or external tool attribution.

- [x] **Step 2: Update ConnectionVerification and OverviewViewModel**

Ensure that when Upstream OpenVPN is active, the Overview session card and Diagnostics display:
- Exit IP & Country (e.g. `Canada - Proton VPN`)
- Adapter Name: `PaqetFire-VPN (Wintun)`
- Zero references to Psiphon.

- [x] **Step 3: Run tests to verify all tests pass**

Run: `dotnet test PaqetFire.slnx`
Expected: ALL test suites PASS.

- [x] **Step 4: Commit changes**

```bash
git add src/PaqetFire.Core/Diagnostics/ConnectionVerification.cs src/PaqetFire.Desktop/ViewModels/OverviewViewModel.cs tests/PaqetFire.Core.Tests/ConnectionVerificationTests.cs
git commit -m "feat(diagnostics): verify OpenVPN exit IP and report Wintun adapter state"
```

---

## Self-Review Checklist
1. **Spec coverage:** 
   - Bundled engine & Wintun adapter -> Task 2 & Task 3 & Task 5.
   - Iran bypass via physical adapter binding -> Task 4.
   - Per-profile toggle (enable/disable) -> Task 1, Task 6 & Task 8.
   - Proton VPN onboarding banner & link -> Task 8.
   - No Psiphon attribution -> Task 9.
   - DPAPI credential encryption -> Task 7.
2. **Placeholder scan:** Zero "TBD", "TODO", or vague steps. All steps have concrete code and exact commands.
3. **Type consistency:** `UpstreamOpenVpnOptions`, `EngineKind.OpenVpn`, `PaqetFire-VPN`, `IOpenVpnConfigurationWriter` used consistently across tasks.
