# Daro Unity SDK — Public API Reference

`using Daro;` — every public type lives in the single namespace `Daro`. Every signature in this file is distilled directly from `SDK/Runtime/` source — no invented members.

Internal infrastructure (`MainThreadDispatcher`, `SafeEventInvoker`, `DaroPlatform`) is intentionally not exposed and is omitted here.

## DaroSdk — static facade

```csharp
namespace Daro
{
    public static class DaroSdk
    {
        // --- initialization ---
        public static Task InitializeAsync();
        public static bool IsInitialized { get; }
        public static event Action OnSdkInitialized;   // late-subscriber: fires once on main thread even if init is already complete

        // --- privacy (safe pre-init or post-init) ---
        public static bool?  HasGdprConsent { get; set; }
        public static string GdprConsentString { get; set; }    // nullable
        public static bool?  DoNotSell { get; set; }
        public static string CcpaConsentString { get; set; }    // nullable
        public static bool?  IsTaggedForChildDirectedTreatment { get; set; }

        // --- runtime settings (pre-init safe) ---
        public static void SetUserId(string userId);
        public static void SetAppMuted(bool muted);
        public static DaroLogLevel LogLevel { get; set; }       // default DaroLogLevel.Info

        // --- test devices (set before InitializeAsync) ---
        public static string[] TestDeviceAdvertisingIdentifiers { get; }
        public static void SetTestDeviceAdvertisingIdentifiers(params string[] identifiers);
    }
}
```

Highlights:
- `InitializeAsync()` is async. Repeated calls return the same `Task`. Pattern: `await DaroSdk.InitializeAsync();`.
- `OnSdkInitialized` honors a *late-subscriber* contract — subscribing after init still fires once on the main thread. No polling.
- `SetTestDeviceAdvertisingIdentifiers(...)` trims and de-duplicates IDs, then forwards them to Android's native MAX mediation layer during the next SDK initialization. Call it before `InitializeAsync()`; changing it after initialization only affects the next initialization. iOS test mode is configured through MAX Mediation Debugger / dashboard-side setup, not this API.

## DaroInterstitialAd

```csharp
namespace Daro
{
    public sealed class DaroInterstitialAd : IDisposable
    {
        // --- construction ---
        public DaroInterstitialAd(string adUnitId, string? placement = null);

        // --- properties ---
        public string  AdUnitId  { get; }
        public string? Placement { get; }

        // --- methods ---
        public void Load();         // async — outcome arrives via OnAdLoaded / OnAdFailedToLoad
        public bool IsReady();      // returns false after Dispose (does not throw)
        public void Show();         // throws InvalidOperationException when !IsReady()
        public void Dispose();      // idempotent. Subsequent Load/Show → ObjectDisposedException

        // --- events (all main-thread) ---
        public event Action<DaroAdInfo>           OnAdLoaded;
        public event Action<DaroAdLoadError>      OnAdFailedToLoad;
        public event Action<DaroAdInfo>           OnAdShown;
        public event Action<DaroAdDisplayError>   OnAdFailedToShow;
        public event Action<DaroAdInfo>           OnAdClicked;
        public event Action<DaroAdInfo>           OnAdImpression;
        public event Action<DaroAdInfo>           OnAdDismissed;
    }
}
```

## DaroRewardedAd

Same surface as `DaroInterstitialAd` plus two additions:

```csharp
namespace Daro
{
    public sealed class DaroRewardedAd : IDisposable
    {
        public DaroRewardedAd(string adUnitId, string? placement = null);

        // ... (same Load / IsReady / Show / Dispose + seven events as DaroInterstitialAd) ...

        // --- extra method ---
        public void SetCustomData(string customData);   // S2S validation pass-through. Call before Show().

        // --- extra event ---
        public event Action<DaroAdInfo, DaroRewardItem> OnEarnedReward;   // fires once per completed view
    }
}
```

## DaroAppOpenAd

Same surface as `DaroInterstitialAd` (no `OnEarnedReward`):

```csharp
namespace Daro
{
    public sealed class DaroAppOpenAd : IDisposable
    {
        public DaroAppOpenAd(string adUnitId, string? placement = null);

        public string  AdUnitId  { get; }
        public string? Placement { get; }

        public void Load();
        public bool IsReady();
        public void Show();
        public void Dispose();

        public event Action<DaroAdInfo>           OnAdLoaded;
        public event Action<DaroAdLoadError>      OnAdFailedToLoad;
        public event Action<DaroAdInfo>           OnAdShown;
        public event Action<DaroAdDisplayError>   OnAdFailedToShow;
        public event Action<DaroAdInfo>           OnAdClicked;
        public event Action<DaroAdInfo>           OnAdImpression;
        public event Action<DaroAdInfo>           OnAdDismissed;
    }
}
```

> AppOpen's canonical trigger is `DaroAppStateNotifier.OnAppStateChanged` on the Foreground transition with `IsReady()` then `Show()`. See [`ad-formats/appopen.md`](ad-formats/appopen.md).

## DaroBannerAd

Persistent native overlay — different lifecycle from fullscreen formats. See [`ad-formats/banner.md`](ad-formats/banner.md).

```csharp
namespace Daro
{
    public sealed class DaroBannerAd : IDisposable
    {
        // --- construction ---
        public DaroBannerAd(
            string adUnitId,
            DaroBannerSize size = DaroBannerSize.Standard,
            DaroBannerPosition position = DaroBannerPosition.BottomCenter,
            string? placement = null);

        // --- properties ---
        public string             AdUnitId  { get; }
        public string?            Placement { get; }
        public DaroBannerSize     Size      { get; }
        public DaroBannerPosition Position  { get; }   // updated via SetPosition

        // --- methods ---
        public void Load();                                  // async — result via OnAdLoaded / OnAdFailedToLoad
        public bool IsReady();                               // never throws; false after Dispose
        public void Show();                                  // throws InvalidOperationException when !IsReady()
                                                             // OnAdShown fires SYNCHRONOUSLY inside this call
        public void Hide();                                  // remove overlay, pause refresh; ad stays loaded
        public void SetPosition(DaroBannerPosition pos);     // immediate if shown, else applied on next Show
        public void Dispose();                               // idempotent; subsequent Load/Show/Hide → ObjectDisposedException

        // --- events (6, all main-thread; no OnAdFailedToShow, no OnAdDismissed) ---
        public event Action<DaroAdInfo>      OnAdLoaded;
        public event Action<DaroAdLoadError> OnAdFailedToLoad;
        public event Action<DaroAdInfo>      OnAdShown;        // synchronous from Show()
        public event Action<DaroAdInfo>      OnAdClicked;
        public event Action<DaroAdInfo>      OnAdImpression;
        public event Action<DaroAdInfo>      OnAdHidden;
    }
}
```

### DaroBannerSize

```csharp
public enum DaroBannerSize
{
    Standard = 0,   // 320×50 dp
    Mrec     = 1,   // 300×250 dp
}
```

### DaroBannerPosition

```csharp
public enum DaroBannerPosition
{
    TopLeft      = 0,
    TopCenter    = 1,
    TopRight     = 2,
    BottomLeft   = 3,
    BottomCenter = 4,
    BottomRight  = 5,
}
```

Six gravity-anchored presets. Pixel-exact custom position is not in v1.

## DaroNativeAd

Publisher-rendered ad with asset payload. See [`ad-formats/native.md`](ad-formats/native.md).

```csharp
namespace Daro
{
    public sealed class DaroNativeAd : IDisposable
    {
        // --- construction ---
        public DaroNativeAd(string adUnitId, string? placement = null);

        // --- properties ---
        public string            AdUnitId  { get; }
        public string?           Placement { get; }
        public Vector2Int        IconSize  { get; set; }     // default (200, 200); set BEFORE Load
        public DaroNativeAdInfo? Info      { get; }          // null until OnAdLoaded; cleared on failed reload + Dispose
        public bool              IsReady   { get; }          // property (not method) — false after Dispose

        // --- methods ---
        public void Load();                  // honors IconSize; result via OnAdLoaded / OnAdFailedToLoad
        public void NotifyVisible();         // impression signal; no-op after Dispose
        public void NotifyHidden();          // no-op after Dispose
        public void NotifyClicked();         // trigger SDK click chain; no-op after Dispose
        public void Dispose();               // idempotent; destroys Info.Icon / Info.MediaImage Texture2Ds

        // --- events (4, all main-thread; no Show/Dismissed/FailedToShow/Expired) ---
        public event Action<DaroAdInfo>      OnAdLoaded;
        public event Action<DaroAdLoadError> OnAdFailedToLoad;
        public event Action<DaroAdInfo>      OnAdImpression;
        public event Action<DaroAdInfo>      OnAdClicked;
    }
}
```

> **Multi-instance**: same `adUnitId` on N instances yields N independent native ads. Natural fit for feed / list UIs.

### DaroNativeAdInfo

Asset payload. All fields nullable — not every ad has every asset. Texture2D fields are owned by the `DaroNativeAd` instance.

```csharp
public sealed class DaroNativeAdInfo
{
    public string?    Title        { get; }
    public string?    Body         { get; }
    public string?    CallToAction { get; }
    public Texture2D? Icon         { get; }
    public Texture2D? MediaImage   { get; }   // Android v1: always null
}
```

### DaroNativeAdView

`MonoBehaviour` slot-path bridge (legacy uGUI). Attach to a prefab, wire inspector slots, call `LoadFor(ad)` then `Bind(ad)` after `OnAdLoaded`.

```csharp
namespace Daro
{
    [AddComponentMenu("Daro/Native Ad View")]
    public sealed class DaroNativeAdView : MonoBehaviour
    {
        // --- inspector slots (all optional, legacy UnityEngine.UI) ---
        public UnityEngine.UI.Text?     TitleText;
        public UnityEngine.UI.Text?     BodyText;
        public UnityEngine.UI.RawImage? IconImage;
        public UnityEngine.UI.Button?   CtaButton;
        public UnityEngine.UI.RawImage? MediaContainer;

        // --- methods ---
        public void ApplySizeHints(DaroNativeAd ad);   // IconImage RectTransform → ad.IconSize
        public void LoadFor(DaroNativeAd ad);          // ApplySizeHints + ad.Load(). Recommended slot-path entry.
        public void Bind(DaroNativeAd ad);             // populate slots + wire CtaButton.onClick; requires ad.IsReady
        public void Unbind();                          // clear slots + remove click listener
    }
}
```

> Auto lifecycle: `Bind()` on an active view or `OnEnable` → `ad.NotifyVisible()`, `OnDisable` → `ad.NotifyHidden()`, `OnDestroy` → `Unbind()`.

## DaroLightPopupAd

Modal popup with construct-baked options. Interstitial-style lifecycle. See [`ad-formats/light-popup.md`](ad-formats/light-popup.md).

```csharp
namespace Daro
{
    public sealed class DaroLightPopupAd : IDisposable
    {
        // --- construction ---
        public DaroLightPopupAd(
            string adUnitId,
            DaroLightPopupAdOptions? options = null,    // null → daro defaults
            string? placement = null);

        // --- properties ---
        public string  AdUnitId  { get; }
        public string? Placement { get; }

        // --- methods ---
        public void Load();
        public bool IsReady();                      // method (not property); false after Dispose
        public void Show();                         // throws InvalidOperationException when !IsReady()
        public void Dispose();                      // idempotent

        // --- events (7, all main-thread — same shape as Interstitial) ---
        public event Action<DaroAdInfo>         OnAdLoaded;
        public event Action<DaroAdLoadError>    OnAdFailedToLoad;
        public event Action<DaroAdInfo>         OnAdShown;
        public event Action<DaroAdDisplayError> OnAdFailedToShow;
        public event Action<DaroAdInfo>         OnAdClicked;
        public event Action<DaroAdInfo>         OnAdImpression;
        public event Action<DaroAdInfo>         OnAdDismissed;
    }
}
```

### DaroLightPopupAdOptions

Color + label customization. `class` (not struct) — field initializers carry daro defaults. Use C# object-initializer to override selectively.

```csharp
public sealed class DaroLightPopupAdOptions
{
    public Color32 BackgroundColor            = new Color32(0x12, 0x14, 0x16, 0xB2);
    public Color32 ContainerColor             = new Color32(0x12, 0x14, 0x16, 0xFF);
    public Color32 AdMarkLabelTextColor       = new Color32(0xF7, 0xFA, 0xFF, 0xFF);
    public Color32 AdMarkLabelBackgroundColor = new Color32(0x3E, 0x43, 0x4F, 0xFF);
    public Color32 TitleColor                 = new Color32(0xF7, 0xFA, 0xFF, 0xFF);
    public Color32 BodyColor                  = new Color32(0xB6, 0xBE, 0xCC, 0xFF);
    public Color32 CtaBackgroundColor         = new Color32(0xEB, 0x26, 0x40, 0xFF);
    public Color32 CtaTextColor               = new Color32(0xFF, 0xFF, 0xFF, 0xFF);
    public Color32 CloseButtonColor           = new Color32(0xF7, 0xFA, 0xFF, 0xFF);
    public string  CloseButtonText            = "Close";
}
```

> **Bake-at-construct**: options are forwarded to the native layer once at constructor time. Post-construct mutations are not propagated. To change colors, `Dispose()` the instance and construct a new one.

## DaroAppStateNotifier

```csharp
namespace Daro
{
    public static class DaroAppStateNotifier
    {
        public enum AppState { Background, Foreground }

        public static event Action<AppState> OnAppStateChanged;   // main-thread; same-state transitions coalesced
        public static AppState CurrentState { get; }              // starts at Foreground
    }
}
```

- No late-subscriber replay — subscribe in `Awake` / `OnEnable`.
- Built on `OnApplicationPause` / `OnApplicationFocus` (Unity's lifecycle hooks).

## Common types (`namespace Daro`)

### DaroAdInfo

```csharp
public sealed class DaroAdInfo
{
    public DaroAdFormat AdFormat  { get; }
    public string       AdUnitId  { get; }
    public double?      Latency   { get; }   // milliseconds, nullable
}
```

### DaroAdLoadError

```csharp
public sealed class DaroAdLoadError
{
    public DaroAdLoadErrorCode Code     { get; }
    public string              Message  { get; }
    public string?             AdUnitId { get; }   // nullable — init-time failures are unit-agnostic
    public int                 RawCode  { get; }   // raw mediation code, kept for deeper diagnosis
}
```

### DaroAdDisplayError

```csharp
public sealed class DaroAdDisplayError
{
    public DaroAdDisplayErrorCode Code    { get; }
    public string                 Message { get; }
    public int                    RawCode { get; }
}
```

### DaroRewardItem

```csharp
public sealed class DaroRewardItem
{
    public int    Amount     { get; }   // dashboard-configured value
    public string RewardType { get; }   // dashboard-configured string (e.g. "coin")
}
```

## Enums

### DaroLogLevel

```csharp
public enum DaroLogLevel
{
    None    = 0,
    Error   = 1,
    Warn    = 2,
    Info    = 3,    // default
    Verbose = 4,
}
```

### DaroAdFormat

```csharp
public enum DaroAdFormat
{
    Banner       = 0,
    Interstitial = 1,
    Rewarded     = 2,
    Native       = 3,
    AppOpen      = 4,
    LightPopup   = 5,
}
```

Surfaced as `DaroAdInfo.AdFormat` on every event payload. Banner / Native / LightPopup are view-based formats with different lifecycle shapes than the three fullscreen formats — pre-read the matching `ad-formats/<format>.md`.

### DaroAdLoadErrorCode

```csharp
public enum DaroAdLoadErrorCode
{
    Unspecified                = -1,
    NotInitialized             = -2,
    InitializationFailed       = -3,
    NoFill                     = 204,
    AdLoadFailed               = -5001,
    InvalidAdUnitIdentifier    = -5603,
    NetworkError               = -1000,
    NetworkTimeout             = -1001,
    NoNetwork                  = -1009,
    FullscreenAdAlreadyLoading = -26,
}
```

### DaroAdDisplayErrorCode

```csharp
public enum DaroAdDisplayErrorCode
{
    Unspecified                       = -1,
    FullscreenAdAlreadyShowing        = -23,
    FullscreenAdNotReady              = -24,
    FullscreenAdInvalidViewController = -25,
    FullscreenAdLoadWhileShowing      = -27,
    NetworkError                      = -1000,
}
```

## Invariants — easy-to-miss rules

- **Every callback fires on Unity's main thread**. Background-thread calls into `Load()` / `Show()` are marshalled internally; the resulting callback still arrives on the main thread.
- **Subscribe before `Load()`**. Subscribing after Load can race with a fast `OnAdLoaded`.
- **`OnSdkInitialized` is the only event with late-subscriber semantics**. The others fire only after their triggering call.
- **Post-`Dispose()` behavior**:
  - `IsReady()` → returns `false` (no throw).
  - `Load()` / `Show()` / `SetCustomData()` → `ObjectDisposedException`.
- **`Dispose()` is idempotent**. Calling it twice is safe.
- **Finalizer ensures native handles are released** even if you forget `Dispose()`, but the timing is non-deterministic.
- **AppOpen + Android**: the Unity shim makes `Load()` cache-aware. Cache-empty loads use a polling-backed readiness bridge; cache-filled loads complete immediately with `latency=0`. Always gate `Show()` with `IsReady()`. See [`ad-formats/appopen.md`](ad-formats/appopen.md).
- **Banner `OnAdShown` is synchronous** — emitted from inside `Show()` itself, not from a native callback. Banner has no `OnAdFailedToShow` and no `OnAdDismissed`; see [`ad-formats/banner.md`](ad-formats/banner.md).
- **Native is instance-owned**: same `adUnitId` on N instances → N independent ads. Banner / Interstitial / LightPopup follow the opposite "duplicate replaces prior" rule.
- **Native textures are owned by the instance**: `Info.Icon` / `Info.MediaImage` are destroyed by `Dispose()`. Do not retain past disposal.
- **LightPopup options are baked at construct time**: post-construct mutations of `DaroLightPopupAdOptions` are not propagated. Dispose + reconstruct to change colors.

<!-- source: SDK/Runtime/DaroSdk.cs, SDK/Runtime/DaroInterstitialAd.cs, SDK/Runtime/DaroRewardedAd.cs, SDK/Runtime/DaroAppOpenAd.cs, SDK/Runtime/DaroBannerAd.cs, SDK/Runtime/DaroBannerSize.cs, SDK/Runtime/DaroBannerPosition.cs, SDK/Runtime/DaroNativeAd.cs, SDK/Runtime/DaroNativeAdView.cs, SDK/Runtime/DaroNativeAdInfo.cs, SDK/Runtime/DaroLightPopupAd.cs, SDK/Runtime/DaroLightPopupAdOptions.cs, SDK/Runtime/DaroAppStateNotifier.cs, SDK/Runtime/Models/{DaroAdInfo,DaroAdLoadError,DaroAdDisplayError,DaroAdLoadErrorCode,DaroAdDisplayErrorCode,DaroLogLevel,DaroAdFormat,DaroRewardItem}.cs, SDK/Runtime/AssemblyInfo.cs, docs/features/native-bridge.md -->
