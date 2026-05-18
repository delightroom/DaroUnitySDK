# Banner — persistent overlay integration

Banner is an **always-on native view overlay** anchored to the device-screen edge — Android attaches it to the Activity's decor view, iOS to the GL view controller's view. It is NOT a child of your Unity Canvas. Sort order, safe area, and scene transitions do not see it the way uGUI does.

Standard sizes: `Standard` 320×50 dp and `Mrec` 300×250 dp. Six gravity-anchored positions (no pixel-exact custom in v1).

## Minimal integration

```csharp
using Daro;
using UnityEngine;

public sealed class BannerHost : MonoBehaviour
{
    [SerializeField] private string _adUnitId = "your-banner-ad-unit-id";
    private DaroBannerAd _banner;

    private void OnEnable()
    {
        _banner = new DaroBannerAd(
            _adUnitId,
            DaroBannerSize.Standard,
            DaroBannerPosition.BottomCenter);

        _banner.OnAdLoaded       += info => Debug.Log($"banner loaded latency={info.Latency}ms");
        _banner.OnAdFailedToLoad += err  => Debug.LogWarning($"banner load failed: {err.Message}");
        _banner.OnAdShown        += info => Debug.Log("banner shown");
        _banner.OnAdClicked      += info => Debug.Log("banner clicked");
        _banner.OnAdImpression   += info => Debug.Log("banner impression");
        _banner.OnAdHidden       += info => Debug.Log("banner hidden");

        _banner.Load();
    }

    public void OnShowButtonClicked()
    {
        if (_banner != null && _banner.IsReady())
            _banner.Show();
    }

    public void OnHideButtonClicked()
    {
        _banner?.Hide();
    }

    private void OnDisable()
    {
        // Banner is a persistent overlay attached to the host view tree — it
        // does NOT auto-detach on scene change. Always Dispose at the screen
        // lifecycle boundary where the banner should disappear permanently.
        _banner?.Dispose();
        _banner = null;
    }
}
```

## `Load()` vs `Show()` — they do different things

| Call | What it does |
|---|---|
| `Load(size baked at construct)` | Asks the mediation layer for a banner ad. The overlay is **not visible** yet. |
| `Show()` | Attaches / makes the overlay visible at the current `Position`. |

`Show()` requires that the prior `Load()` completed successfully:
- `Show()` before `Load()` resolves → `InvalidOperationException`.
- Guard with `if (_banner.IsReady()) _banner.Show();`.

`OnAdShown` fires **synchronously inside `Show()`** — Banner has no native show callback, so the SDK emits it directly. This is the only ad format with a synchronous event; treat it like a return signal, not an async one.

## `Hide()` vs `Dispose()` — also different things

| Call | What it does |
|---|---|
| `Hide()` | Removes the overlay from the host view tree, **pauses auto-refresh**, but keeps the ad loaded. A subsequent `Show()` re-displays without a new network round-trip. |
| `Dispose()` | Permanently releases the native handle. Subsequent `Load` / `Show` / `Hide` → `ObjectDisposedException`. |

Use `Hide()` for "temporarily off-screen, may show again." Use `Dispose()` when the screen / scene that owns the banner is going away for good.

`OnAdHidden` fires asynchronously on the main thread.

## Repositioning

```csharp
_banner.SetPosition(DaroBannerPosition.TopCenter);
```

Effective immediately if currently shown; applied on the next `Show()` otherwise. The example sample combines `SetPosition` + `Show()` in one call, which works whether or not the overlay is currently visible.

## Auto-refresh is server-controlled — you don't trigger it

The Daro dashboard configures a per-ad-unit `refreshInterval`. The native banner view runs its own refresh timer; each cycle fires `OnAdLoaded` → `OnAdImpression` again. You don't call `Load()` again.

- `Hide()` pauses the refresh timer; `Show()` resumes.
- App backgrounded → auto-paused via lifecycle observer.
- `refreshInterval = 0` on the dashboard disables refresh.

Do not implement your own refresh loop on top.

## Scene ownership — the consumer's job

While a `DaroBannerAd` instance is alive, the underlying native view stays attached to the host view tree (Android decor view / iOS GL view controller). Switching screens, loading a new scene, or hiding your Unity Canvas does **not** detach it. Mediation refresh keeps firing impressions on the still-attached view.

Whoever owns the screen the banner belongs to must explicitly call:
- `Hide()` when the screen goes away but the banner might return, OR
- `Dispose()` when the screen permanently unloads.

The `OnDisable` / `OnDestroy` pattern in the snippet above is the simplest correct shape for a single-screen banner.

## Native overlay caveat — no z-order, no safe-area inset

The banner overlay is a sibling of Unity's GL surface in the host view tree, not a Canvas child. Consequences:

- **No Canvas sort order.** The banner draws over everything Unity renders — you cannot place a uGUI element on top of it inside Unity. Reserve no Canvas space for it.
- **No `Camera.WorldToScreenPoint` anchoring.** Position is one of six gravity presets, not a Unity-space coordinate.
- **Safe area is device-OS managed.** Banner positions use the system's safe-area on iOS notch / Android cutout devices; do not double-inset in your own UI.
- **Impression timing follows mediation viewability rules.** `OnAdImpression` is not a "layout is ready" signal — do not block UI on it.

## Platform notes

### Android

Overlay sits on `Activity.addContentView` above the Unity GL surface as a transparent `FrameLayout`. Game-area touch passes through; the banner view itself receives banner touches. `Hide()` uses `View.GONE` (reclaims layout space, eliminates phantom touch zone) rather than `INVISIBLE`.

### iOS

Overlay attaches as a subview of `UnityGetGLViewController().view`. `Hide()` removes the subview (not `view.hidden = YES`); `Show()` re-adds it. This is intentional — UIKit's `hidden` doesn't cascade through the inner ad view stack the way you'd expect.

`OnAdImpression` on iOS fires at the mediation revenue moment (`didPayRevenue`), independent of actual viewability. Treat it as a billing/accounting event, not a "user saw the ad" signal.

## Common failures

### No-fill / invalid ad unit

`OnAdFailedToLoad` with `DaroAdLoadErrorCode.NoFill (204)` is normal — mediation simply has no inventory right now. Backoff and retry.

`DaroAdLoadErrorCode.InvalidAdUnitIdentifier (-5603)` is ambiguous on the wire and points at one of three causes:
1. The ad unit is disabled on the Daro/MAX dashboard.
2. The ad unit belongs to a different app.
3. The app's bundle id does not match the dashboard's registered app.

Verify the dashboard before debugging in code. Refer to the [troubleshooting](../troubleshooting.md) guide for the full decision tree.

### Show before Load

`InvalidOperationException` from `Show()`. Always `if (_banner.IsReady()) _banner.Show()`.

### Stale overlay after scene change

You moved to a new scene but the banner is still there. `Dispose()` was never called. Add it to the screen-owner's teardown path (`OnDisable` / `OnDestroy`).

### Phantom impressions while "hidden"

You set a GameObject inactive but did not call `Hide()`. The native overlay is still attached and the refresh timer keeps firing impressions. Call `Hide()` (or `Dispose()`) — Unity GameObject state does not propagate to the native view.

### Network error

`DaroAdLoadErrorCode.NetworkError (-1000)` / `NoNetwork (-1009)` / `NetworkTimeout (-1001)`. Surface a retry path or fall back gracefully.

## What Banner does NOT have

- **No `OnAdFailedToShow`.** The C# pre-check (`!IsReady()` → `InvalidOperationException`) is the only show-time failure surface. Banner has no native show-failure callback.
- **No `OnAdDismissed`.** Banner has no "user dismissed" concept — it's always-on. Use `OnAdHidden` (your `Hide()` call) instead.
- **No `OnAdRefreshed`.** Each refresh cycle reuses the existing `OnAdLoaded` / `OnAdImpression` events; there's no separate refresh signal.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (Banner section 603-663, Back handler 172-178), SDK/Runtime/DaroBannerAd.cs, SDK/Runtime/DaroBannerSize.cs, SDK/Runtime/DaroBannerPosition.cs, docs/features/native-bridge.md (Banner overlay) -->
