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
    }
}
```

Highlights:
- `InitializeAsync()` is async. Repeated calls return the same `Task`. Pattern: `await DaroSdk.InitializeAsync();`.
- `OnSdkInitialized` honors a *late-subscriber* contract — subscribing after init still fires once on the main thread. No polling.

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
- **AppOpen + Android 1.3.6**: `OnAdLoaded` can be swallowed by an internal preload race. Poll `IsReady()` instead. See [`ad-formats/appopen.md`](ad-formats/appopen.md).

<!-- source: SDK/Runtime/DaroSdk.cs, SDK/Runtime/DaroInterstitialAd.cs, SDK/Runtime/DaroRewardedAd.cs, SDK/Runtime/DaroAppOpenAd.cs, SDK/Runtime/DaroAppStateNotifier.cs, SDK/Runtime/Models/{DaroAdInfo,DaroAdLoadError,DaroAdDisplayError,DaroAdLoadErrorCode,DaroAdDisplayErrorCode,DaroLogLevel,DaroAdFormat,DaroRewardItem}.cs, SDK/Runtime/AssemblyInfo.cs, docs/features/native-bridge.md -->
