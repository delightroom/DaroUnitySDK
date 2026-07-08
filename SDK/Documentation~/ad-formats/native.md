# Native — publisher-rendered ad integration

Native ads are **publisher-rendered**: the SDK delivers the asset payload (title / body / CTA text / icon image) and you render it inside your own Unity UI. This is fundamentally different from Banner (a native overlay) and from the fullscreen formats (mediation owns the entire screen).

Two integration paths:
- **Slot path** — drop a `DaroNativeAdView` MonoBehaviour on your prefab, wire its inspector slots to legacy uGUI widgets, and let it auto-bind. Recommended.
- **Raw path** — read `DaroNativeAd.Info` (a `DaroNativeAdInfo` POCO) and render it through any UI you like (TextMeshPro, custom widgets, UI Toolkit).

Native is the only format with **multi-instance** support: N independent `DaroNativeAd` instances sharing the same `adUnitId` is a normal pattern for list / feed UIs.

## Minimal integration (slot path)

### Scene setup

1. Add a Canvas. (Native uses legacy uGUI — `Text` / `RawImage` / `Button` — for Unity 2021.3 compatibility.)
2. Add a child GameObject with a `DaroNativeAdView` component (menu: `Daro/Native Ad View`).
3. Inside that GameObject, add UI children: Title (`Text`), Body (`Text`), Icon (`RawImage`), CTA (`Button` with child `Text`), Media (`RawImage`).
4. Drag the children into the matching inspector slots on `DaroNativeAdView`. All slots are optional — wire only what your layout uses.

### Driver MonoBehaviour

```csharp
using Daro;
using UnityEngine;

public sealed class NativeAdHost : MonoBehaviour
{
    [SerializeField] private string           _adUnitId = "your-native-ad-unit-id";
    [SerializeField] private DaroNativeAdView _view;

    private DaroNativeAd _ad;

    private async void OnEnable()
    {
        await DaroSdk.InitializeAsync();
        // Re-entrance guard — if we navigated away while init was pending,
        // OnDisable already fired and we should not create a new ad now.
        if (!isActiveAndEnabled) return;

        _ad = new DaroNativeAd(_adUnitId);

        _ad.OnAdLoaded       += info =>
        {
            // Bind only AFTER OnAdLoaded fires. Before that, Info is null
            // and IsReady is false; Bind would throw.
            _view.Bind(_ad);
        };
        _ad.OnAdFailedToLoad += err  => Debug.LogWarning($"native load failed: {err.Message}");
        _ad.OnAdImpression   += info => Debug.Log("native impression");
        _ad.OnAdClicked      += info => Debug.Log("native clicked");

        // LoadFor(ad) = ApplySizeHints(ad) + ad.Load(). The size hint is
        // critical on Android — see "Icon size hint" below.
        _view.LoadFor(_ad);
    }

    private void OnDisable()
    {
        _ad?.Dispose();
        _ad = null;
    }
}
```

The slot path takes care of three things automatically:
- **Size hint propagation** — `LoadFor(ad)` reads the IconImage's RectTransform pixel size into `ad.IconSize` before calling `Load()`.
- **Visibility notifications** — `Bind()` on an active `DaroNativeAdView` calls `ad.NotifyVisible()`; later `OnEnable` / `OnDisable` calls `ad.NotifyVisible()` / `ad.NotifyHidden()` for you.
- **Click wiring** — `Bind(ad)` adds a listener to `CtaButton.onClick` that calls `ad.NotifyClicked()`.

## Raw path

Use the raw path when:
- You need TextMeshPro or custom widgets that aren't `UnityEngine.UI.Text` / `RawImage` / `Button`.
- You want a layout the slot path can't express (rotated layouts, animated reveals, etc.).
- You're rendering through UI Toolkit. (A UI Toolkit native-ad element is on the v2 roadmap; until then, raw path.)

```csharp
// Inside OnAdLoaded:
var info = _ad.Info;   // DaroNativeAdInfo (or null on a failed reload)
if (info == null) return;

_titleField.text       = info.Title        ?? string.Empty;
_bodyField.text        = info.Body         ?? string.Empty;
_ctaText.text          = info.CallToAction ?? string.Empty;
_iconWidget.SetTexture(info.Icon);
// info.MediaImage is always null on Android v1.

_ctaButton.onClick.AddListener(() => _ad.NotifyClicked());
```

You also own the visibility lifecycle:
```csharp
// When your UI activates:
_ad.NotifyVisible();
// When your UI deactivates:
_ad.NotifyHidden();
```

If you forget `NotifyVisible()`, the mediation layer will not count an impression. If you forget `NotifyClicked()`, the click chain breaks — the user taps your CTA button but nothing further happens.

Also set the icon size hint before `Load()`:
```csharp
_ad.IconSize = new Vector2Int(200, 200);   // or whatever pixel size your icon widget actually uses
_ad.Load();
```

## Lifecycle and event order

1. `new DaroNativeAd(adUnitId)` → handle constructed, no network call yet.
2. (`_ad.IconSize = ...;`) — required on raw path, automatic on slot path.
3. `_ad.Load()` → mediation responds:
   - Success → `OnAdLoaded` (main thread). `Info` populated, `IsReady` becomes `true`.
   - Failure → `OnAdFailedToLoad`. `Info` cleared (a failed reload does not leave stale assets visible).
4. **Bind only after `OnAdLoaded`.** `Bind` before ready → `InvalidOperationException`.
5. Visibility activates → `NotifyVisible()` (auto on slot path, manual on raw) → impression accounting begins.
6. Mediation viewability rule met → `OnAdImpression`.
7. User taps CTA → `NotifyClicked()` (auto on slot path, manual on raw) → `OnAdClicked` + native click chain (deep link / browser / store).
8. Cleanup → `Dispose()` → handle released, `Info.Icon` / `Info.MediaImage` Texture2D destroyed. If the publisher forgets `Dispose()`, the finalizer backstop also attempts to release the handle and owned textures later, but that timing is non-deterministic.

There is no `Show()`, no `OnAdShown`, no `OnAdDismissed`. Publisher controls visibility by activating / deactivating the prefab.

## Icon size hint — required, not optional

`DaroNativeAd.IconSize` defaults to 200×200. **You must keep it above 0.**

On Android, the mediation layer's internal image loader (Glide) reads the icon view's width/height when deciding what resolution to download. A 0×0 view causes it to **skip the download entirely**, leaving `Info.Icon` null.

- **Slot path**: `view.LoadFor(ad)` auto-derives the hint from the IconImage RectTransform. You don't have to think about it.
- **Raw path**: set `ad.IconSize = new Vector2Int(w, h)` before `Load()`. Use the same pixel dimensions as your icon widget.

## Multi-instance — same ad unit, N independent ads

Unlike Banner / Interstitial (where a duplicate construct replaces the prior instance), Native is **instance-owned**:

```csharp
// All three are independent ads, even though they share the adUnitId.
var ad1 = new DaroNativeAd(adUnit);
var ad2 = new DaroNativeAd(adUnit);
var ad3 = new DaroNativeAd(adUnit);
ad1.Load(); ad2.Load(); ad3.Load();   // three independent mediation loads
```

Use this for feeds and lists. Each instance gets its own `DaroNativeAdView` bind, its own `NotifyVisible` / `NotifyClicked` ownership, and its own `Dispose` lifetime.

## Texture ownership

`Info.Icon` and `Info.MediaImage` are `Texture2D` instances **owned by the `DaroNativeAd`**. `Dispose()` destroys them. Do not:
- Cache the `Texture2D` reference past `Dispose()`.
- Re-parent it into another instance's state.
- Assume `Info` is still valid after a reload — `OnAdFailedToLoad` clears `Info`, and a successful reload swaps it.

If you want to preserve an icon for offline display, copy it (`new Texture2D(...) + Graphics.CopyTexture`) before disposal.

## Platform notes

### Android

Each `DaroNativeAd` instance attaches a 200×200 transparent host `FrameLayout` above the Unity GL surface (touch-transparent — game touches pass through). This is invisible scaffolding required by the mediation layer's view-size accounting; the actual rendering happens in your Unity UI.

`MediaImage` is always null on Android v1 — video / large media is a v2 scope.

### iOS

Each instance attaches a 1×1 transparent host to `UnityGetGLViewController().view`. The same scaffolding role as on Android. `OnAdImpression` fires at the mediation revenue moment, independent of actual viewability — treat it as a billing/accounting event, not a "user saw it" signal.

In no-fill environments, iOS daro retries up to ~10 times with exponential backoff (~2-minute window), so you may see up to 11 `OnAdFailedToLoad` events from a single `Load()` call. This is a mediation-environment signal, not a wiring bug. Wait for the inventory to recover; do not add another retry loop on top.

## Common failures

### Empty slots after Bind

Title is blank, icon is missing, CTA text is empty:
1. Did you call `Bind(ad)` from inside `OnAdLoaded`? Earlier and `IsReady` is false.
2. Are the slots wired in the inspector? `DaroNativeAdView`'s slots are all optional — an unwired slot is silently skipped.
3. Is the underlying ad missing those fields? Not every ad has every asset. `Info.Title` / `Body` / `CallToAction` are nullable; check the actual payload.

### Icon never appears

On Android, you forgot to set `IconSize` on the raw path (or your IconImage RectTransform has size 0×0 on the slot path). The mediation image loader skipped the download. Set a non-zero `IconSize` before `Load()`.

### Click launches the ad but `OnAdClicked` never fires

You're on the raw path and your Button.onClick handler does not call `_ad.NotifyClicked()`. Wire it up. (On the slot path, `Bind(ad)` does this for you on the `CtaButton`.)

### Impression never fires

You forgot `NotifyVisible()` (raw path), OR the user never sees the ad long enough to meet the mediation viewability rule (Android: roughly 1 second at ≥50% pixels visible). Also note iOS fires impression on revenue, which can land before user-observable visibility.

### No-fill / invalid ad unit

`OnAdFailedToLoad` with `DaroAdLoadErrorCode.NoFill (204)` is normal — mediation has no inventory. `InvalidAdUnitIdentifier (-5603)` points at a dashboard problem (disabled / wrong app / bundle id mismatch). See the [troubleshooting](../troubleshooting.md) guide for the decision tree.

### `Bind` throws InvalidOperationException

You called `Bind(ad)` before `OnAdLoaded` fired. Move the call inside the `OnAdLoaded` handler.

### `Bind` throws ArgumentNullException

The ad argument is null. You either passed the wrong field or the ad was disposed and nulled before Bind ran.

## What Native does NOT have

- **No `Show` / `OnAdShown`.** Publisher controls visibility via GameObject SetActive.
- **No `OnAdDismissed`.** Same reason.
- **No `OnAdFailedToShow`.** No native show pathway.
- **No `OnAdExpired`.** Mediation does not push an expiry signal — implement a publisher-side timer if you need one.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/NativeAdTests/NativeAdManualTest.cs, SDK/Runtime/DaroNativeAd.cs, SDK/Runtime/DaroNativeAdView.cs, SDK/Runtime/DaroNativeAdInfo.cs, docs/features/native-bridge.md (Native ad) -->
