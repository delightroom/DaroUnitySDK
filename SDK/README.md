# Daro Unity SDK

Unity SDK for Daro. Target platforms: **Android, iOS**.

## Minimum Unity version

**Unity 2019.4 LTS or newer.** Aligned with AdMob mediation Unity SDK support floor. `package.json` declares `"unity": "2019.4"`.

This folder is the UPM package root. Sample Unity projects in this repo (under `Samples/<name>/`) consume it via `file:` path in their `Packages/manifest.json`:

```json
"com.delightroom.daro.unity": "file:../../SDK"
```

## Dependencies

- **EDM4U** (`com.google.external-dependency-manager`) — resolves native Android/iOS dependencies from `Editor/*Dependencies.xml`.

## Integration guide

See [`docs/integration.md`](../docs/integration.md) in the repo for the consumer setup flow (DaroSettings → key files → EDM4U resolve → SDK init → ad load/show).
