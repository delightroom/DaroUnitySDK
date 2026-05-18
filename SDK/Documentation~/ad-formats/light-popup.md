# Light Popup — modal ad with options

Light Popup is a **modal native popup** that the Daro layer renders for you. Unlike Native (publisher renders) and Banner (persistent overlay), Light Popup behaves like a fullscreen ad: you `Load()` then `Show()`, it presents itself, an auto-dismiss timer or a Close button takes it down. You customize colors and the close-button label; everything else is owned by the native layer.

The lifecycle and event surface mirror Interstitial — `Load` / `Show` / 7 events — so if you've integrated Interstitial, this will feel familiar.

## Minimal integration

```csharp
using Daro;
using UnityEngine;

public sealed class LightPopupHost : MonoBehaviour
{
    [SerializeField] private string _adUnitId = "your-light-popup-ad-unit-id";
    private DaroLightPopupAd _ad;

    private void OnEnable()
    {
        // null options → daro defaults (semi-transparent dimmer, red CTA, "Close" label).
        _ad = new DaroLightPopupAd(_adUnitId, options: null);

        _ad.OnAdLoaded       += info => Debug.Log($"light popup loaded latency={info.Latency}ms");
        _ad.OnAdFailedToLoad += err  => Debug.LogWarning($"load failed: {err.Message}");
        _ad.OnAdShown        += info => Debug.Log("shown");
        _ad.OnAdFailedToShow += err  => Debug.LogWarning($"show failed: {err.Message}");
        _ad.OnAdClicked      += info => Debug.Log("clicked");
        _ad.OnAdImpression   += info => Debug.Log("impression");
        _ad.OnAdDismissed    += info => Debug.Log("dismissed");

        _ad.Load();
    }

    public void OnShowButtonClicked()
    {
        if (_ad != null && _ad.IsReady())
            _ad.Show();
    }

    private void OnDisable()
    {
        _ad?.Dispose();
        _ad = null;
    }
}
```

## Customizing colors and the close label

`DaroLightPopupAdOptions` controls 9 colors plus the close-button text. Use C# object-initializer syntax to override only the fields you care about — the rest stay at the daro defaults.

```csharp
var options = new DaroLightPopupAdOptions
{
    CtaBackgroundColor = new Color32(0x00, 0x66, 0xFF, 0xFF),   // blue CTA
    BodyColor          = new Color32(0x00, 0xCC, 0x66, 0xFF),   // green body
    CloseButtonText    = "Close (custom)",
};

_ad = new DaroLightPopupAd(_adUnitId, options);
_ad.Load();
```

Default values (daro hex mirror):

| Field | Default |
|---|---|
| `BackgroundColor` | `#B2121416` (semi-transparent dimmer) |
| `ContainerColor` | `#121416` |
| `AdMarkLabelTextColor` | `#F7FAFF` |
| `AdMarkLabelBackgroundColor` | `#3E434F` |
| `TitleColor` | `#F7FAFF` |
| `BodyColor` | `#B6BECC` |
| `CtaBackgroundColor` | `#EB2640` (red CTA) |
| `CtaTextColor` | `#FFFFFF` |
| `CloseButtonColor` | `#F7FAFF` |
| `CloseButtonText` | `"Close"` |

## Options are baked at construct time — Dispose to change them

Options are passed to the native layer once, inside the constructor. After that, mutating the `DaroLightPopupAdOptions` instance does nothing — the native layer keeps its baked copy.

**To switch to a different color set, you must Dispose the current instance and construct a new one:**

```csharp
// Too late: this changes only the managed options object, not the already-created native popup.
options.TitleColor = Color.red;

// ✓ Dispose, construct fresh, re-load.
var newOptions = new DaroLightPopupAdOptions
{
    TitleColor = Color.red,
};

_ad?.Dispose();
_ad = new DaroLightPopupAd(_adUnitId, newOptions);
_ad.OnAdLoaded += info => Debug.Log("light popup loaded");   // re-subscribe handlers on the new instance
_ad.Load();
```

This is the same lifecycle pattern as Interstitial — instance-per-load is the natural shape; reusing one instance across configs is not.

## `Show()` discipline

`Show()` requires that `Load()` succeeded:
- `Show()` before ready → `InvalidOperationException`.
- `Show()` after `Dispose()` → `ObjectDisposedException`.

Guard with `IsReady()`:
```csharp
if (_ad != null && _ad.IsReady())
    _ad.Show();
```

`Show()` failures land in `OnAdFailedToShow` (e.g. another fullscreen is already on screen).

## Auto-dismiss

The native layer dismisses the popup automatically:

| Platform | Auto-dismiss timing |
|---|---|
| Android | 8 seconds |
| iOS | 6 seconds + 3-second fade-out |

The user can also tap the Close button or tap the CTA at any time. Either way, `OnAdDismissed` fires once the popup is fully gone.

The auto-dismiss duration is not configurable from the consumer API.

## Reload policy

The same instance can be reused for another `Load()` after `OnAdDismissed` — handler subscriptions stay attached. This is similar to Interstitial's "preload the next round inside `OnAdDismissed`" pattern, though Light Popup is more episodic and less frequently used in tight loops, so a one-shot per screen is more typical.

To change options, follow the Dispose-then-construct path above (re-subscribing handlers).

## Platform notes

### Android

Renders as a `Dialog(activity)` on top of the host Activity. The 8-second auto-dismiss is a daro-side timer.

### iOS

Renders via `present(_:animated:)`. The 6s + 3s fade is a daro-side timer that differs slightly from Android — same C# API surface, different visual cadence.

**iOS fill behavior follows the Native ad mediation chain.** daro iOS builds Light Popup on top of `MANativeAdLoader`, so the no-fill / retry / ATT-impact patterns documented in [`native.md`](native.md) apply here too. In a no-fill environment you can see repeated `OnAdFailedToLoad` events from a single `Load()` while the mediation layer retries internally. This is environmental, not a wiring bug.

`OnAdImpression` on iOS fires at the mediation revenue moment, independent of strict viewability. Treat it as a billing/accounting event, not a "user saw it" signal.

## Common failures

### No-fill / invalid ad unit

Same shape as the other formats — `OnAdFailedToLoad` with `DaroAdLoadErrorCode.NoFill (204)` or `InvalidAdUnitIdentifier (-5603)`. The latter is ambiguous (dashboard disabled / wrong app / bundle id mismatch). See [troubleshooting](../troubleshooting.md) for the decision tree.

### Show() not presenting

`OnAdFailedToShow` with `DaroAdDisplayErrorCode.FullscreenAdAlreadyShowing (-23)` — another fullscreen is on screen. Reschedule.

`DaroAdDisplayErrorCode.FullscreenAdNotReady (-24)` — cache expired or never loaded. Call `Load()` again, wait for `OnAdLoaded`, retry.

### Custom options didn't apply

You set `DaroLightPopupAdOptions` fields after construction, or you reused the same instance across config changes. Options are bake-at-construct. Dispose the instance and construct a new one with the new options.

### iOS shows but Android doesn't (or vice versa)

The ad unit might only be configured on one platform's dashboard, or fill rates differ today. Verify the dashboard. The C# API is platform-uniform; the underlying mediation environment is not.

### `Show()` throws InvalidOperationException

`IsReady()` was false. The most common cause is calling `Show()` immediately after `Load()` without waiting for `OnAdLoaded`. Wait for the event or guard with `IsReady()`.

### iOS impression fires before the popup is visible

This is the iOS revenue-time impression pattern, shared with Banner and Native on iOS. Not a bug — do not use `OnAdImpression` as a "popup is visible" signal. Use `OnAdShown` instead.

## What Light Popup does NOT support

- **Multi-instance with the same ad unit.** A modal popup can't show two at once. Duplicate construction replaces the prior instance.
- **Configurable auto-dismiss timing.** The 8s (Android) / 6s+3s (iOS) timers are owned by the native layer.
- **Custom layouts.** Only colors and the close-button text are customizable. For full publisher rendering, use [Native](native.md).
- **`OnAdRefreshed`.** Light Popup is a one-shot — no auto-refresh concept.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (Light Popup section 665-730), SDK/Runtime/DaroLightPopupAd.cs, SDK/Runtime/DaroLightPopupAdOptions.cs, docs/features/native-bridge.md (Light Popup) -->
