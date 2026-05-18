# Daro Unity SDK — Integration Knowledge Base

Read this first when integrating the Daro Unity SDK into a game project. Every code sample in this KB is distilled from the actual sample at `Samples/DaroExample/` — no invented signatures.

## Where to start

- **First integration / general patterns**: [`integration.md`](integration.md) — common lifecycle, event subscription, dispose discipline, main-thread guarantees, anti-patterns. Applies to every ad format.
- **Format-specific minimal-correct patterns**:
  - [`ad-formats/interstitial.md`](ad-formats/interstitial.md) — fullscreen interrupt ad
  - [`ad-formats/rewarded.md`](ad-formats/rewarded.md) — user-initiated reward video
  - [`ad-formats/appopen.md`](ad-formats/appopen.md) — auto-shown on foreground return
  - [`ad-formats/banner.md`](ad-formats/banner.md) — persistent overlay at top/bottom of screen
  - [`ad-formats/native.md`](ad-formats/native.md) — publisher-rendered ad in your own Unity UI
  - [`ad-formats/light-popup.md`](ad-formats/light-popup.md) — modal popup with customizable colors
- **Troubleshooting**: [`troubleshooting.md`](troubleshooting.md) — first-response diagnostics for no-fill, invalid ad unit, consent / ATT, iOS signing, EDM4U, stale exports.
- **API reference**: [`api-reference.md`](api-reference.md) — exact public C# types, methods, events, and enums.

## Covered scope

| Ad format | KB | Notes |
|---|---|---|
| Interstitial | ✓ | fullscreen, the simplest pattern |
| Rewarded | ✓ | Interstitial + `OnEarnedReward` / `SetCustomData` |
| AppOpen | ✓ | foreground-return auto-trigger; Android cache race notes inside |
| Banner | ✓ | persistent native overlay; Load / Show / Hide / Dispose lifecycle |
| Native | ✓ | publisher-renders pattern; slot-path (`DaroNativeAdView`) + raw path; multi-instance |
| LightPopup | ✓ | modal Dialog (Android) / present (iOS) with 9-color + close-label options |
| Failure-mode diagnostic | ✓ | [troubleshooting.md](troubleshooting.md) |

## Integration principles (TL;DR)

1. **Namespace**: `using Daro;` — every public type lives under this single namespace.
2. **Order**: `await DaroSdk.InitializeAsync();` → construct an ad instance → register events with `+=` → call `Load()` → call `Show()`.
3. **Events fire on the main thread**: every SDK callback is marshalled to Unity's main thread. Update UI directly inside callbacks; no dispatcher needed.
4. **`IDisposable`**: every ad instance (`DaroInterstitialAd` / `DaroRewardedAd` / `DaroAppOpenAd`) implements `IDisposable`. Call `Dispose()` and null the field in `OnDisable` / `OnDestroy`.
5. **Guard `Show()` with `IsReady()`** (or at minimum a null check on the instance). Showing before loading raises `InvalidOperationException` or surfaces `OnAdFailedToShow`.
6. **AppOpen is the exception to manual `Show()`**: subscribe to `DaroAppStateNotifier.OnAppStateChanged` and `Show()` on the Foreground transition rather than wiring it to a user-facing button.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (Interstitial 427-438, Rewarded 462-474, AppOpen 528-579, Banner 603-663, LightPopup 665-730), Samples/DaroExample/Assets/Scripts/Runtime/NativeAdTests/NativeAdManualTest.cs, SDK/Runtime/DaroSdk.cs, SDK/Runtime/Daro*Ad.cs, docs/features/native-bridge.md -->
