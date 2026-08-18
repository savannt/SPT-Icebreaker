# Icebreaker — SPT 4.1.2 port

A backport of retail EFT 1.0's **Icebreaker** map — the frozen ship — with its crew, the
Black Division garrison, authored loot, keypad doors, the blowtorch, the hovercraft transit
and the intro cutscene. Ported from
[danauraborealis/ManimalIcebreaker](https://github.com/danauraborealis/ManimalIcebreaker)
(by **Manimal**) to **SPT 4.1.2**.

---

# 🟣 JOIN THE DISCORD — https://discord.gg/nxa3W7w4rJ

### **https://discord.gg/nxa3W7w4rJ**

**This is the single most important link in this README.** All updates, release
announcements, bug fixes, early builds and support happen in the Discord **first**.
If you run this mod, join it — it is the only place you will reliably hear about
breaking changes and new versions.

**What's coming next:** I am building a **post-1.0 patcher and backend, written
from scratch and engineered to be very performant** — a proper foundation instead
of the current patchwork. **All of these mods will shortly be merged into that new
system.** If you want to follow that work, or use it when it lands, the Discord is
where it will be announced.

### 👉 **https://discord.gg/nxa3W7w4rJ** 👈

---
## Requirements

This mod will **not load** without all of the following installed:

| Dependency | Minimum version |
|---|---|
| [tarkin's Climbable Ladders](https://github.com/bmpq/spt-ladders) | 1.0.4 |
| [MoreBotsAPI](https://github.com/savannt/SPT-MoreBotsAPI) | 2.0.0 |
| [Black Division](https://github.com/savannt/SPT-BlackDivision) | 1.1.3 |
| WTT-ClientCommonLib / WTT-ServerCommonLib | 2.0.20 |
| WTT-ContentBackport | 1.1.4 |

**Requires SPT 4.1.2.** It will not run on 4.0.x — see *What the port changed* below.

Ladders is a **hard dependency**: the client plugin refuses to load without `com.tarkin.ladders`,
because the ice-level intro is boarded by climbing a rope ladder.

## Installation

1. Install the dependencies above first.
2. Download `SPT-Icebreaker-0.3.0.zip` from the latest release (~1.6 GB — it carries the map's scene bundle).
3. Extract it into your **SPT install root** — the folder containing `EscapeFromTarkov.exe` and `SPT_Runtime`.

```
BepInEx/plugins/ManimalIcebreaker/           <- client plugin + PerfectCullingRuntime + authored data
BepInEx/plugins/ManimalIcebreaker/streamingassets/   <- the map's scene bundle (~1.7 GB)
SPT_Runtime/user/mods/ManimalIcebreaker/     <- server mod (DLL + db/ + bundles/)
```

> **Note:** server mods live under `SPT_Runtime/user/mods/` — **not** `user/mods/` at the SPT root.
> This matters here: upstream's own 0.3.0 zip targets SPT 4.0, where the server folder was `SPT/`.
> Extracting *that* zip on 4.1.2 drops the server half in a folder the server never scans, and it is
> ignored with no error. The zip in this repo's releases is already laid out for 4.1.2.

Already have the upstream 0.3.0 install and only want the ported binaries?
`SPT-Icebreaker-0.3.0-dll-only.zip` carries just the three DLLs and the server db.

## Playing it

The map is **quest-gated by design**. It stays hidden until you finish the Boreas chain from
Mechanic — *Boreas – Part 6 → Stick to It → Saving Private Roman → A Bitter Victory → **Hangover***.
Once *Hangover* is complete, Icebreaker appears on the map screen.

To open it up for testing, blank the id in `SPT_Runtime/user/mods/ManimalIcebreaker/db/maplock.json`:

```json
{ "finalQuestId": "" }
```

**You spawn at ice level beside the hull, not on deck** — that is the intended arrival. Climb the rope
ladder to board. Scav raids to Icebreaker are intentionally disabled.

## What the port changed

SPT 4.1.2 is a breaking release on both halves:

- **Server:** net9.0 → **net10.0**; `AbstractModMetadata` → `IModMetadata`; `IOnLoad.OnLoad()` →
  `OnLoadAsync(CancellationToken)`; `DatabaseService`/`ConfigServer` replaced by injected tables and
  config objects; core namespaces re-foldered; route handlers take a trailing `CancellationToken`.
- **Generator hooks lost `virtual`.** `BotGenerator.PrepareAndGenerateBot` and
  `LocationLootGenerator.GenerateLocationLoot` can no longer be overridden by a DI subclass, so both
  "firewalls" were re-seated on Harmony. The loot isolation now wraps
  `LocationLifecycleService.GenerateLocationAndLoot` — one frame *outside* the generator — so it never
  unpatches a method it is executing inside.
- **Client:** ~60 obfuscated type references re-pointed at their 4.1.2 names and verified against the
  shipped assemblies.
- **Patching is now per-class.** Harmony's `PatchAll` aborts the whole sweep on the first bad target,
  so one stale name silently disabled every patch after it. Patches are applied class by class and the
  log reports how many applied versus failed, by name.

Three patches remain unresolved and are logged by name at startup
(`Patch_NoWeatherSeenDebuff`, `Patch_WeatherDesatOverride`, `Patch_AuthoredTripwireNeverInert`).
All three are weather/AI-perception cosmetics; none blocks a raid.

## Troubleshooting

**"Could not load [Manimal-Icebreaker] because it has missing dependencies: com.tarkin.ladders"** —
install Climbable Ladders 1.0.4+.

**`ladders scene bundle not found at ...\tarkin-ladders\suburbs`** — expected and harmless. Ladders maps
only vanilla location ids to its own bundles; Icebreaker's ladders are authored in its own scene and
merely *climbed* through that mod.

**The map never appears on the map screen** — that is the quest gate, not a fault. See *Playing it*.

**Stuck at water level** — climb the rope ladder; the ice-level spawn is intentional.

## Credits

Original map backport by **Manimal** —
[danauraborealis/ManimalIcebreaker](https://github.com/danauraborealis/ManimalIcebreaker).
This repository is a port to SPT 4.1.2; all original design, assets and authored map data are theirs.
Climbing behaviour comes from **tarkin's** [Climbable Ladders](https://github.com/bmpq/spt-ladders).

## License

MIT — see [LICENSE](LICENSE). Copyright (c) 2026 danauraborealis.
