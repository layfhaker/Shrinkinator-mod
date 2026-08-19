# Changelog

## 1.0.9
- Republish after accidental deprecation so Thunderstore Mod Manager can index the latest version.

## 1.0.8
- Shrunk enemies can be picked up without a strength upgrade: grab strength is boosted for the shrink duration, follow-anchor is disabled while you pull, and they get a brief vanilla grab-stun.

## 1.0.7
- Grab on shrunk enemies no longer fights PhysFollow (the “I grab them but they ignore me” bug).

## 1.0.6
- Enemy shrink no longer scales physics/colliders/skinned-mesh transforms (fixes flying, broken textures, and ungrabbable hitboxes). Visual scale only, with follow-target compensation.

## 1.0.5
- Shrinkinator bullets no longer deal vanilla HurtCollider damage (enemies were dying/despawning on hit).

## 1.0.4
- Valuable dollar value is no longer reduced when shrunk (size/mass still shrink).
- Shrunk enemies are lighter to lift.

## 1.0.3
- Mist is translucent and fades out (no more permanent opaque cloud).
- One shot drains one battery bar (not several).
- One bullet / one mist cloud per trigger pull.

## 1.0.2
- Keep the runtime item template inactive so Photon/UI no longer break when starting a run from lobby.

## 1.0.1
- Removed global `Resources.Load` Harmony patches that wiped main-menu buttons (TMP_Settings).
- Admin-menu and shop spawn resolved via PrefabRef / MultiplayerPool caches instead.

## 1.0.0
- Initial Thunderstore release.
