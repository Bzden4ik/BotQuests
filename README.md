# LifePMC

> **SPT 4.0 BepInEx plugin** — gives PMC bots raid objectives by sending them to custom map points, where they explore, investigate and loot the area.

---

## What it does

Without this mod, PMC bots wander aimlessly or simply camp in one spot. **LifePMC** gives each PMC bot a purpose: they pick a point of interest on the map, sprint to it, and then explore the surrounding area — entering buildings, checking rooms, and looting if [LootingBots](https://hub.sp-tarkov.com/files/file/1096-looting-bots/) is installed.

### Behaviour overview

1. **Navigate** — bot selects a random point from the map's point list and sprints toward it
2. **Explore** — once arrived, the bot spends a configurable amount of time at the location:
   - **With sub-points** (placed via PointEditor): the bot walks between each sub-point in shuffled order
     - `> pass` sub-point — bot walks near it, triggers a loot scan, immediately moves on
     - `| stay` sub-point — bot walks up close, triggers a loot scan, waits for the configured duration
   - **Without sub-points**: bot auto-wanders randomly within a **50 m radius** of the main point on NavMesh, triggering loot scans along the way
3. **Cooldown** — after finishing, the bot waits before picking the next objective
4. **Combat** — if the bot spots an enemy or comes under fire, LifePMC yields to combat AI (SAIN, vanilla) and resumes after a cooldown

---

## Requirements

| Dependency | Required | Notes |
|---|---|---|
| [SPT](https://www.sp-tarkov.com/) | ✅ | Version **4.0** |
| [BepInEx](https://github.com/BepInEx/BepInEx) | ✅ | Included with SPT |
| [BigBrain](https://hub.sp-tarkov.com/files/file/1109-bigbrain/) | ✅ | AI layer framework |
| [SAIN](https://hub.sp-tarkov.com/files/file/1062-sain/) | ⚪ Soft | Automatically takes combat priority over LifePMC |
| [LootingBots](https://hub.sp-tarkov.com/files/file/1096-looting-bots/) | ⚪ Soft | Enables bots to actually loot items at sub-points |
| [PointEditor](https://github.com/) | ⚪ Recommended | In-game tool to create points for LifePMC |

---

## Installation

1. Download the latest release
2. Drop `LifePMC.dll` into `BepInEx/plugins/`
3. *(Optional but recommended)* Install **PointEditor** and create points in-game
4. Launch SPT and start a raid — bots will automatically load points for the current map

---

## Supported Maps

| Map | File name | Status |
|---|---|---|
| Customs | `bigmap.json` | ✅ Supported |
| Woods | `woods.json` | 🔜 Planned |
| Shoreline | `shoreline.json` | 🔜 Planned |
| Interchange | `interchange.json` | 🔜 Planned |
| Reserve | `rezervbase.json` | 🔜 Planned |
| Labs | `laboratory.json` | 🔜 Planned |
| Lighthouse | `lighthouse.json` | 🔜 Planned |
| Streets | `tarkovstreets.json` | 🔜 Planned |
| Ground Zero | `sandbox.json` | 🔜 Planned |

> Points are loaded from `BepInEx/plugins/PointEditor/<mapId>.json`.
> Currently **Customs** is the only map with a point set. All other maps are supported by the system — you just need to create points for them using **PointEditor**.

---

## Configuration

All settings are available in-game via the BepInEx Configuration Manager, or by editing `BepInEx/config/com.lifepmc.mod.cfg`.

### Timings

| Setting | Default | Description |
|---|---|---|
| Пауза после цели (сек) | `20` | Cooldown before the bot picks the next point after completing one |
| Таймаут квеста (сек) | `300` | Maximum time allowed to reach a single point before giving up |
| Пауза после боя (сек) | `30` | How long the bot waits before resuming after combat |
| Время ожидания на точке (сек) | `30` | Time spent at a point when `wait_time = 0` in the JSON |
| Время ожидания на саб-точке (сек) | `15` | Default stay duration for `stay`-type sub-points with `wait_time = 0` |

### Limits

| Setting | Default | Description |
|---|---|---|
| Макс. застреваний | `5` | Max stuck events before the bot stops accepting objectives for the rest of the raid |

---

## Point file format

Points are stored as JSON arrays. LifePMC reads two files per map:

**`<mapId>.json`** — main points (objectives)
```json
[
  {
    "id": "custom_bigmap_1",
    "map": "bigmap",
    "quest_name": "Custom",
    "zone_name": "ZoneSnipeTower",
    "x": 276.8,
    "y": 1.5,
    "z": -63.1,
    "wait_time": 120
  }
]
```

**`<mapId>_subpoints.json`** — sub-points linked to main points
```json
[
  {
    "id": "sub_bigmap_1",
    "parent_id": "custom_bigmap_1",
    "map": "bigmap",
    "type": "pass",
    "x": 274.8,
    "y": 1.2,
    "z": -58.0,
    "wait_time": 0
  },
  {
    "id": "sub_bigmap_2",
    "parent_id": "custom_bigmap_1",
    "map": "bigmap",
    "type": "stay",
    "x": 260.9,
    "y": 2.1,
    "z": -75.3,
    "wait_time": 15
  }
]
```

| Field | Description |
|---|---|
| `id` | Unique identifier — must be unique across all points on the map |
| `parent_id` | ID of the main point this sub-point belongs to |
| `type` | `"pass"` — walk through and scan; `"stay"` — stop and wait |
| `wait_time` | Seconds to spend at this location (`0` = use config default) |

> The easiest way to create and manage these files is with the **PointEditor** companion mod.

---

## How it works (technical)

- Uses **BigBrain** `CustomLayer` + `CustomLogic` to inject a custom AI layer
- Layer is registered for brain types: `"PMC"`, `"PmcBear"`, `"PmcUsec"` at priority **25**
- SAIN layers run at higher priority and automatically override LifePMC during combat
- LootingBots integration is fully reflection-based (soft dependency — mod works without it)
- Anti-stuck detection: if the bot moves less than 0.5 m in 5 seconds while navigating, it abandons the current target and picks a new one

---

## DebugOverlay integration

If [DebugOver](https://github.com/) is installed, each bot's current objective is displayed in the overlay:

| Status | Meaning |
|---|---|
| `→ ZoneName` | Sprinting to the main point |
| `~ ZoneName` | Auto-wandering (no sub-points) |
| `▶ ZoneName [2/8]` | Walking past sub-point #2 of 8 |
| `⏸ ZoneName [4/8]` | Standing at a `stay` sub-point |
| `At: ZoneName` | Waiting at the point (no sub-points, old behaviour) |

---

## Known issues / notes

- Only **Customs** has a pre-made point set right now. You need PointEditor to add points to other maps.
- Bots may get stuck on complex geometry (stairs, doors). The anti-stuck system will skip the blocked target after 5 seconds.
- Very large maps with few NavMesh-accessible areas may cause the auto-wander to log `нет NavMesh` warnings — this is harmless.

---

## License

MIT
