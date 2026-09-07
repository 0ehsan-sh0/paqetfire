# Hotspot Client Guides Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Provide dedicated, interactive step-by-step XAML onboarding guides for v2rayNG and HAPP/Hiddify in PaqetFire Desktop, replacing the cramped inline disclaimer with clean branded launcher cards.

**Architecture:** Create two modular WinUI 3 UserControls (`V2rayNgGuideControl.xaml` and `HappGuideControl.xaml`) with responsive step cards, field-specific copy buttons, and vector brand badges. Embed them in `MainWindow.xaml` with seamless navigation back to the Routing view, updating dynamic endpoint values in real time.

**Tech Stack:** C# 10 / .NET 10, WinUI 3 (Windows App SDK), XAML vector graphics.

**Spec:** [docs/adr/0003-in-app-hotspot-client-guides.md](file:///e:/Developing/Projects/paqetfire/docs/adr/0003-in-app-hotspot-client-guides.md) and user request for separate XAML pages and clickable icon launchers.

## Global Constraints

- Must follow native WinUI 3 design language, ThemeResource brushes, and fluid typography.
- SOCKS-only policy remains enforced (reusing LAN credentials).
- Vector badges used for icons (no binary bitmap assets required).
- Dynamic binding/updating of active hotspot IP, port, username, password, and SOCKS URI.

---

### Task 1: Create `V2rayNgGuideControl` UserControl

**Files:**
- Create: `src/PaqetFire.Desktop/Controls/V2rayNgGuideControl.xaml`
- Create: `src/PaqetFire.Desktop/Controls/V2rayNgGuideControl.xaml.cs`

**Interfaces:**
- Produces: `event EventHandler? BackRequested;`
- Produces: `void UpdateEndpoint(string address, int port, string username, string password, string socksUri);`

- [ ] **Step 1: Create `V2rayNgGuideControl.xaml`**
  Build the XAML layout featuring:
  - Header with `← Back to Routing` button, title "v2rayNG Setup Guide", and subtitle.
  - Vector icon badge for v2rayNG (paper airplane motif in purple/blue gradient).
  - Numbered step cards (Connect to Hotspot, Install v2rayNG, Import from Clipboard with 1-click copy, Manual Entry with field copy buttons for Address, Port, User, Pass, Enable Remote DNS, Connect).

- [ ] **Step 2: Create `V2rayNgGuideControl.xaml.cs`**
  Implement:
  - `BackRequested` event raised on back button click.
  - `UpdateEndpoint` method populating the dynamic text blocks and copy button tags.
  - Copy button click handlers copying individual fields to clipboard via `DataPackage` with feedback notifications.

- [ ] **Step 3: Verify Compilation**
  Run `dotnet build src/PaqetFire.Desktop/PaqetFire.Desktop.csproj` to confirm XAML markup and code-behind compile without errors.

---

### Task 2: Create `HappGuideControl` UserControl

**Files:**
- Create: `src/PaqetFire.Desktop/Controls/HappGuideControl.xaml`
- Create: `src/PaqetFire.Desktop/Controls/HappGuideControl.xaml.cs`

**Interfaces:**
- Produces: `event EventHandler? BackRequested;`
- Produces: `void UpdateEndpoint(string address, int port, string username, string password, string socksUri);`

- [ ] **Step 1: Create `HappGuideControl.xaml`**
  Build the XAML layout featuring:
  - Header with `← Back to Routing` button, title "HAPP / Hiddify Setup Guide", and subtitle.
  - Vector icon badge for HAPP/Hiddify (shield/connection motif in teal/cyan gradient).
  - Numbered step cards (Connect to Hotspot, Install from App Store / Play Store, Import from Clipboard, Manual SOCKS5 entry with copy buttons, Remote DNS setting, Connect & verify).

- [ ] **Step 2: Create `HappGuideControl.xaml.cs`**
  Implement:
  - `BackRequested` event raised on back button click.
  - `UpdateEndpoint` method populating dynamic values.
  - Field-specific copy button handlers.

- [ ] **Step 3: Verify Compilation**
  Run `dotnet build src/PaqetFire.Desktop/PaqetFire.Desktop.csproj` to verify the control compiles.

---

### Task 3: Integrate Launchers into `MainWindow.xaml` and Wire Navigation in `MainWindow.xaml.cs`

**Files:**
- Modify: `src/PaqetFire.Desktop/MainWindow.xaml`
- Modify: `src/PaqetFire.Desktop/MainWindow.xaml.cs`

**Interfaces:**
- Consumes: `V2rayNgGuideControl`, `HappGuideControl`
- Produces: `ShowPage("guide-v2rayng")`, `ShowPage("guide-happ")`, launcher click events

- [ ] **Step 1: Add namespace and guide controls in `MainWindow.xaml`**
  - Add `xmlns:controls="using:PaqetFire.Desktop.Controls"` to root `Window`.
  - In `HotspotOptions`, replace the tiny text block with two clickable card buttons (`OpenV2rayNgGuideButton` and `OpenHappGuideButton`), with vector badge icons, app title, platform subtitle, and chevron icon.
  - Inside the main content `Grid`, add `<controls:V2rayNgGuideControl x:Name="V2rayNgGuideView" Visibility="Collapsed" />` and `<controls:HappGuideControl x:Name="HappGuideView" Visibility="Collapsed" />`.

- [ ] **Step 2: Wire navigation and data propagation in `MainWindow.xaml.cs`**
  - Update `ShowPage(string destination)` to support `"guide-v2rayng"` and `"guide-happ"`.
  - In `MainWindow` constructor or initialization, hook `V2rayNgGuideView.BackRequested += (_, _) => ShowPage("routing");` and `HappGuideView.BackRequested += (_, _) => ShowPage("routing");`.
  - Implement `OpenV2rayNgGuideButton_Click` and `OpenHappGuideButton_Click` to populate active endpoint values into the guide control and navigate to it via `ShowPage`.
  - Update `UpdateHotspotEndpointText` or `UpdateHotspotUri` to sync updated values to guide controls if active.

- [ ] **Step 3: Run Full Build and Test Run**
  Run `dotnet build` and `dotnet test` to verify zero build errors, zero warnings, and passing tests.

---

### Task 4: Documentation & Walkthrough

**Files:**
- Modify: `docs/hotspot-sharing.md`
- Create / Update: Walkthrough artifact

- [ ] **Step 1: Update `docs/hotspot-sharing.md`**
  Update the phone setup section to describe the new in-app visual guides and their dynamic copy functionality.

- [ ] **Step 2: Build verification**
  Confirm full solution builds cleanly.
