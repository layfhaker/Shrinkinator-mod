# Shrinkinator

BepInEx mod for **R.E.P.O.** that adds a shop weapon: the **Shrinkinator**. A shot sprays green mist; anything in the cloud shrinks.

- **Valuables** shrink **permanently** (size and mass), down to a configurable minimum so they cannot vanish from stacking. Dollar value stays the same.
- **Enemies and players** shrink **temporarily** (20 seconds by default), then grow back. Shrunk players get a lower camera, smaller hitboxes, and shorter grab reach.
- Shrunk enemies are **lighter and easier to grab** (works like a strength upgrade: a short tug picks them up and briefly stuns). They are **not** hard-stunned by the ray itself.
- **6 charges** via the vanilla battery (recharge at stations).
- Procedural retro ray-gun model (can be turned off in config).
- Multiplayer: Photon RPC. **Every player in the lobby needs the mod.**

## Install

r2modman / Thunderstore Mod Manager (recommended), or manually:

1. BepInEx 5.4.21 (x64), launch the game once.
2. [REPOLib](https://thunderstore.io/c/repo/p/Zehs/REPOLib/) 4.2.0+.
3. Put `Shrinkinator.dll` in `BepInEx/plugins/`.

The gun shows up in the shop (weapons), priced 1.5× the vanilla pistol (configurable).

## Build

.NET SDK 8+:

```bash
dotnet build -c Release
# bin/Release/netstandard2.1/Shrinkinator.dll
```

## Config

`BepInEx/config/com.kimi.shrinkinator.cfg` after first launch. Notable defaults:

| Setting | Default | Meaning |
|---|---|---|
| `ScaleFactor` | `0.35` | Size multiplier (~3× smaller). |
| `DurationSeconds` | `20` | Enemy/player shrink duration. |
| `ValueScalePrice` | `false` | Do **not** reduce valuable price. |
| `ValuableMinScale` | `0.2` | Floor for valuable size vs original (stops infinite stacking). |
| `Charges` | `6` | Shots per full battery. |
| `UseCustomModel` | `true` | Procedural gun mesh. |

## Thunderstore / CI

Push to `main` builds the mod. If the repo secret `THUNDERSTORE_TOKEN` is set, the same workflow publishes a new version to Thunderstore (R.E.P.O. community, team **Avariiiprime**) when the version in `Shrinkinator.csproj` is new.

Create a Thunderstore service-account token (team settings → Service Accounts) and add it as `THUNDERSTORE_TOKEN` in GitHub → Settings → Secrets and variables → Actions.

Manual publish: **Actions → Publish to Thunderstore → Run workflow**.
