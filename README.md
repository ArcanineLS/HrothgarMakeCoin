<div align="center">

# 🪙 HrothgarMakeCoin

**_Hrothgar make coin. Coin good._**

A FFXIV [Dalamud](https://github.com/goatcorp/Dalamud) plugin that automatically undercuts your retainer Market Board listings — with optional [AutoRetainer](https://github.com/PunishXIV/AutoRetainer) post-venture integration.

[![Release](https://img.shields.io/github/v/release/ArcanineLS/HrothgarMakeCoin?style=flat-square&color=8a2be2)](https://github.com/ArcanineLS/HrothgarMakeCoin/releases/latest)
[![License](https://img.shields.io/badge/license-AGPL--3.0-blue?style=flat-square)](LICENSE.md)
[![Fork of Dagobert](https://img.shields.io/badge/fork%20of-Dagobert-ad8af5?style=flat-square)](https://github.com/SHOEGAZEssb/Dagobert)

</div>

> **Formerly Dagobert.** HrothgarMakeCoin is a rebrand/fork of [**Dagobert** by SHOEGAZEssb](https://github.com/SHOEGAZEssb/Dagobert), with a modernized UI and AutoRetainer post-venture integration. All the original penny-pinching credit goes to them — see [Credits](#credits).

## ✨ Features

- **Auto-undercut** every retainer's Market Board listings with one click.
- **AutoRetainer integration** — re-price each retainer automatically right after its ventures finish (opt-in).
- **Fixed-amount or percentage** undercutting, with a max-undercut safety limit.
- **Per-item min/max prices**, added straight from the inventory right-click menu.
- **Universalis** data-center pricing option.
- **Self-undercut protection** so you don't undercut your own listings.
- **Hotkeys**, chat reporting, and Windows **Text-to-Speech** notifications.
- Modern, LightlessClient-style UI.

## 📦 Installation

1. In-game, open **Dalamud Settings** (`/xlsettings`) → **Experimental**.
2. Under **Custom Plugin Repositories**, add:

   ```text
   https://raw.githubusercontent.com/ArcanineLS/HrothgarMakeCoin/main/repo.json
   ```

3. Click **+**, then **Save**.
4. Open the **Plugin Installer** (`/xlplugins`), search for **HrothgarMakeCoin**, and install.

Open the config window with `/hrothgarmakecoin` (or the short alias `/hmc`). The legacy `/dagobert` command still works.

> **Upgrading from Dagobert?** On first launch HrothgarMakeCoin automatically imports your existing Dagobert settings (pricing, per-item limits, retainer selection, hotkeys, TTS) so nothing is lost.

## 🤖 AutoRetainer integration

If [AutoRetainer](https://github.com/PunishXIV/AutoRetainer) is installed, HrothgarMakeCoin can re-price a retainer's listings **immediately after AutoRetainer finishes that retainer's ventures**, using AutoRetainer's post-processing hook. Enable it on the **AutoRetainer** tab (off by default), then run your normal AutoVenture — each retainer is refreshed as it's processed.

| Option | Default | Description |
| --- | --- | --- |
| Auto pinch after AutoRetainer ventures | Off | Master switch for the post-venture integration. Only shown when AutoRetainer is installed. |
| Respect retainer selection | On | Only auto-pinch retainers enabled in the Retainers section. |

## ⚙️ Configuration

The config window has a left icon rail: **General**, **AutoRetainer**, and **Min/Max Prices**.

### Pricing

| Option | Default | Description |
| --- | --- | --- |
| Use HQ price | On | Uses HQ listings for HQ items. If no HQ listing is available, HrothgarMakeCoin may not find a price. |
| Undercut Mode | FixedAmount | Subtract a fixed gil amount or a percentage from the lowest price. |
| Undercut amount | 1 gil | How much to undercut by. In Percentage mode this is 1-99%. |
| Max Undercut percentage | 100% | Safety limit — price changes cutting more than this percentage are skipped. |
| Undercut Self | Off | When off, your own retainer listings are not undercut. |
| Use Universalis data center prices | Off | Uses the cheapest listing on your current data center from Universalis. |
| Default amount | 0 gil | Fallback price when none can be found. `0` disables the fallback. |
| Show inventory context menu entry | On | Adds the right-click inventory entry for per-item price limits. |

### Timing

| Option | Default | Description |
| --- | --- | --- |
| Market Board Price Check Delay | 3000 ms | Delay before opening the Market Board price list. Lower is faster but less reliable. |
| Market Board Keep Open Time | 1000 ms | How long the Market Board stays open while fetching prices. |

Recommended: `3000-4000 ms` price-check delay, `1000-2000 ms` keep-open time.

### Per-Item Min/Max Prices

Right-click an inventory item → **Add HrothgarMakeCoin price limits**. `0` means no limit. Limits are applied after a candidate price is found (including Universalis prices) and before it's written to the listing.

### Hotkeys & TTS

| Option | Default | Description |
| --- | --- | --- |
| Enable Post Pinch Hotkey | On (Shift) | Hold while posting a new item to auto-fetch the lowest price. |
| Enable Pinch Hotkey | Off (Q) | Press to start Auto Pinch from the retainer/sell list. |
| Text-to-Speech | Off | Speak a message when auto pinch finishes all / each retainer. |

## 🛠️ Building from source

Requires the **.NET 10 SDK** and a Dalamud dev environment.

```bash
git clone --recurse-submodules https://github.com/ArcanineLS/HrothgarMakeCoin.git
cd HrothgarMakeCoin
dotnet build HrothgarMakeCoin/HrothgarMakeCoin.csproj -c Release
```

Already cloned without submodules? Run `git submodule update --init --recursive` (ECommons is a submodule).

Releases are built and published automatically by [`.github/workflows/create_release.yml`](.github/workflows/create_release.yml) when you push a `v*` tag (e.g. `git tag v1.14.2.0 && git push origin v1.14.2.0`).

## ❤️ Credits

- Originally created by **[SHOEGAZEssb](https://github.com/SHOEGAZEssb)** as **[Dagobert](https://github.com/SHOEGAZEssb/Dagobert)** — the core penny-pinching engine is their work. Consider supporting them on [Ko-fi](https://ko-fi.com/timstadler).
- Rebranded, restyled, and extended with AutoRetainer integration by **[ArcanineLS](https://github.com/ArcanineLS)**.
- Built with [ECommons](https://github.com/NightmareXIV/ECommons) and integrates with [AutoRetainer](https://github.com/PunishXIV/AutoRetainer).

## 📄 License

[AGPL-3.0-or-later](LICENSE.md), inherited from Dagobert.
