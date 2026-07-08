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
        // Load() displays by default. Show() is useful after Hide().
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

## `Load()` vs `Show()` — default display

| Call | What it does |
|---|---|
| `Load(size baked at construct)` | Asks the mediation layer for a banner ad and displays the native overlay by default when loading succeeds. |
| `Show()` | Re-displays the overlay at the current `Position` after `Hide()`. If the banner is already visible, it is a no-op. |

`Show()` still requires that the prior `Load()` completed successfully:
- `Show()` before `Load()` resolves → `InvalidOperationException`.
- Guard with `if (_banner.IsReady()) _banner.Show();`.

`OnAdShown` fires once after a successful `Load()` displays the banner, and again after each `Hide()` → `Show()` re-display. Banner has no native show callback, so the SDK synthesizes this event.

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

Effective immediately if currently shown; applied on the next default `Load()` display or `Show()` otherwise.

## Auto-refresh is server-controlled — you don't trigger it

The Daro dashboard configures a per-ad-unit `refreshInterval`. The native banner view runs its own refresh timer; each cycle fires `OnAdLoaded` → `OnAdImpression` again. You don't call `Load()` again.

- `Hide()` pauses the refresh timer; `Show()` resumes.
- If a refresh was already in flight when `Hide()` ran, the native side may finish it, but the SDK suppresses extra public `OnAdLoaded` events for already-loaded hidden banners. A first load that completes after an immediate `Hide()` still fires `OnAdLoaded` so `IsReady()` can become true for a later `Show()`.
- App backgrounded → auto-paused via lifecycle observer.
- `refreshInterval = 0` on the dashboard disables refresh.

Do not implement your own refresh loop on top.

## Scene ownership — the consumer's job

After a successful `Load()`, the underlying native view stays attached to the host view tree (Android decor view / iOS GL view controller) until you call `Hide()` or `Dispose()`. Switching screens, loading a new scene, or hiding your Unity Canvas does **not** detach it. Mediation refresh keeps firing impressions on the still-attached view.

Whoever owns the screen the banner belongs to must explicitly call:
- `Hide()` when the screen goes away but the banner might return, OR
- `Dispose()` when the screen permanently unloads.

The `OnDisable` / `OnDestroy` pattern in the snippet above is the simplest correct shape for a single-screen banner.

## Native overlay caveat — no z-order, no safe-area inset

The banner overlay is a sibling of Unity's GL surface in the host view tree, not a Canvas child. Consequences:

- **No Canvas sort order.** The banner draws over everything Unity renders — you cannot place a uGUI element on top of it inside Unity.
- If your Unity UI must avoid the banner, reserve that space in your own layout after reading `GetScreenRect()`. You still cannot draw Unity Canvas content over the native banner.
- **No `Camera.WorldToScreenPoint` anchoring.** Position is one of six gravity presets, not a Unity-space coordinate.
- **Safe area is device-OS managed.** Banner positions use the system's safe-area on iOS notch / Android cutout devices; do not double-inset in your own UI.
- **Impression timing follows mediation billing/revenue rules.** `OnAdImpression` is not a "layout is ready" signal — do not block UI on it.

## Platform notes

### Android

Overlay sits on `Activity.addContentView` above the Unity GL surface as a transparent `FrameLayout`. Game-area touch passes through; the banner view itself receives banner touches. `Load()` attaches it visible by default. `Hide()` removes the overlay from the native hierarchy and pauses it; `Show()` reattaches and resumes it.

### iOS

Overlay attaches as a subview of `UnityGetGLViewController().view` during `Load()`. `Hide()` removes the subview (not `view.hidden = YES`); `Show()` re-adds it. This is intentional — UIKit's `hidden` doesn't cascade through the inner ad view stack the way you'd expect.

`OnAdImpression` on iOS fires at the mediation revenue moment (`didPayRevenue`) while the banner is intended to be visible. Treat it as a billing/accounting event, not a "layout is ready" signal.

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

### Unexpected impressions

`Load()` displays Banner by default, so impressions after a successful load are expected. If the banner should not be on screen, call `Hide()` (or `Dispose()`) — Unity GameObject state does not propagate to the native view.

### Network error

`DaroAdLoadErrorCode.NetworkError (-1000)` / `NoNetwork (-1009)` / `NetworkTimeout (-1001)`. Surface a retry path or fall back gracefully.

## What Banner does NOT have

- **No `OnAdFailedToShow`.** The C# pre-check (`!IsReady()` → `InvalidOperationException`) is the only show-time failure surface. Banner has no native show-failure callback.
- **No `OnAdDismissed`.** Banner has no "user dismissed" concept — it's always-on. Use `OnAdHidden` (your `Hide()` call) instead.
- **No `OnAdRefreshed`.** Each refresh cycle reuses the existing `OnAdLoaded` / `OnAdImpression` events; there's no separate refresh signal.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (Banner section 603-663, Back handler 172-178), SDK/Runtime/DaroBannerAd.cs, SDK/Runtime/DaroBannerSize.cs, SDK/Runtime/DaroBannerPosition.cs, docs/features/native-bridge.md (Banner overlay) -->
