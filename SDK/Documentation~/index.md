# Daro Unity SDK — Integration Knowledge Base

Read this first when integrating the Daro Unity SDK into a game project. Every code sample in this KB is distilled from the actual sample at `Samples/DaroExample/` — no invented signatures.

## Where to start

- **First integration / general patterns**: [`integration.md`](integration.md) — common lifecycle, event subscription, dispose discipline, main-thread guarantees, anti-patterns. Applies to every ad format.
- **Format-specific minimal-correct patterns**:
  - [`ad-formats/interstitial.md`](ad-formats/interstitial.md) — fullscreen interrupt ad
  - [`ad-formats/rewarded.md`](ad-formats/rewarded.md) — user-initiated reward video
  - [`ad-formats/appopen.md`](ad-formats/appopen.md) — auto-shown on foreground return
- **API reference**: [`api-reference.md`](api-reference.md) — exact public C# types, methods, events, and enums.

## Covered scope (v0)

| Ad format | KB | Notes |
|---|---|---|
| Interstitial | ✓ | fullscreen, the simplest pattern |
| Rewarded | ✓ | Interstitial + `OnEarnedReward` / `SetCustomData` |
| AppOpen | ✓ | foreground-return auto-trigger; Android cache race notes inside |
| Banner / Native / LightPopup | (phase 2) | view-based — see `Samples/DaroExample/Assets/Scripts/` demos |
| Failure-mode diagnostic (no-fill / cert / consent) | (phase 2) | mediation debugging playbook |

## Integration principles (TL;DR)

1. **Namespace**: `using Daro;` — every public type lives under this single namespace.
2. **Order**: `await DaroSdk.InitializeAsync();` → construct an ad instance → register events with `+=` → call `Load()` → call `Show()`.
3. **Events fire on the main thread**: every SDK callback is marshalled to Unity's main thread. Update UI directly inside callbacks; no dispatcher needed.
4. **`IDisposable`**: every ad instance (`DaroInterstitialAd` / `DaroRewardedAd` / `DaroAppOpenAd`) implements `IDisposable`. Call `Dispose()` and null the field in `OnDisable` / `OnDestroy`.
5. **Guard `Show()` with `IsReady()`** (or at minimum a null check on the instance). Showing before loading raises `InvalidOperationException` or surfaces `OnAdFailedToShow`.
6. **AppOpen is the exception to manual `Show()`**: subscribe to `DaroAppStateNotifier.OnAppStateChanged` and `Show()` on the Foreground transition rather than wiring it to a user-facing button.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (Interstitial 427-438, Rewarded 462-474, AppOpen 528-579), SDK/Runtime/DaroSdk.cs, SDK/Runtime/Daro*Ad.cs, docs/features/native-bridge.md -->
