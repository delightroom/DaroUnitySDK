# Integration Patterns — Lifecycle, Events, Dispose, Main-Thread, Anti-Patterns

Shared patterns for every ad format (Interstitial / Rewarded / AppOpen). Format-specific details live under `ad-formats/<format>.md`.

## 1. SDK initialization

```csharp
using Daro;
using System.Threading.Tasks;
using UnityEngine;

public sealed class GameBootstrap : MonoBehaviour
{
    private async void Start()
    {
        // (Optional) privacy settings — settable before OR after init.
        DaroSdk.HasGdprConsent = true;
        DaroSdk.SetUserId("user-12345");

        await DaroSdk.InitializeAsync();
        // From here on, you can construct ad instances and call Load().

        // Late subscriber: even if init already completed, this fires once
        // on the main thread on the next tick.
        DaroSdk.OnSdkInitialized += () => Debug.Log("Daro SDK ready");
    }
}
```

Key points:

- `DaroSdk.InitializeAsync()` returns a `Task`. Repeated calls return the same Task — safe to `await` from anywhere.
- Privacy settings (`HasGdprConsent` / `GdprConsentString` / `DoNotSell` / `CcpaConsentString` / `IsTaggedForChildDirectedTreatment`) are safe to set before *and* after init. The SDK does NOT own the consent UX — your app must display GDPR dialogs / ATT prompts / UMP flows and then assign the resulting values here.
- `DaroSdk.SetUserId(string)` / `SetAppMuted(bool)` / `LogLevel` setters are also pre-init safe.
- `OnSdkInitialized` has a *late-subscriber* contract — subscribing after init still fires the handler once on the main thread. No polling required.

## 2. Ad instance lifecycle (canonical pattern)

```csharp
using Daro;
using UnityEngine;

public sealed class AdHost : MonoBehaviour
{
    private const string AdUnitId = "your-ad-unit-id";
    private DaroInterstitialAd _ad;   // nullable field

    private void OnEnable()
    {
        // 1. Construct (ad unit id, optional placement).
        _ad = new DaroInterstitialAd(AdUnitId);

        // 2. Register every event handler BEFORE calling Load().
        _ad.OnAdLoaded       += info => Debug.Log($"loaded ad={info.AdUnitId}");
        _ad.OnAdFailedToLoad += err  => Debug.LogWarning($"load failed code={err.Code}: {err.Message}");
        _ad.OnAdShown        += info => Debug.Log("shown");
        _ad.OnAdFailedToShow += err  => Debug.LogWarning($"show failed: {err.Message}");
        _ad.OnAdClicked      += info => Debug.Log("clicked");
        _ad.OnAdImpression   += info => Debug.Log("impression");
        _ad.OnAdDismissed    += info => Debug.Log("dismissed");

        // 3. Async load — outcome arrives via OnAdLoaded / OnAdFailedToLoad.
        _ad.Load();
    }

    public void OnShowButtonClicked()
    {
        // 4. Guard Show() with a null check + IsReady().
        if (_ad != null && _ad.IsReady())
            _ad.Show();
    }

    private void OnDisable()
    {
        // 5. Always Dispose() in OnDisable / OnDestroy. Null the field so
        // a subsequent OnEnable can construct a fresh instance cleanly.
        _ad?.Dispose();
        _ad = null;
    }
}
```

Order: **Construct → `+=` events → `Load()` → (wait) → `Show()` → `Dispose()`**. Breaking this order triggers the anti-patterns below.

## 3. Event subscription

- Seven events on Interstitial / AppOpen: `OnAdLoaded`, `OnAdFailedToLoad`, `OnAdShown`, `OnAdFailedToShow`, `OnAdClicked`, `OnAdImpression`, `OnAdDismissed`.
- Rewarded adds one more: `OnEarnedReward` (eight total).
- All events use the standard C# `event Action<...>` pattern — `+=` to register, `-=` to remove.
- **Register handlers before calling `Load()`**. Registration after `Load()` technically works but can race with a fast `OnAdLoaded` and drop the first event.
- If you reuse the same instance across `OnEnable` cycles (`+=` every time without `-=`), you will accumulate duplicate subscribers. The safer pattern: construct a fresh instance in `OnEnable`, `Dispose()` + null in `OnDisable` (matches the snippet above and the `Samples/DaroExample` controller).

## 4. Main-thread guarantee

**Every SDK callback fires on Unity's main thread.** The internal `MainThreadDispatcher` marshals worker-thread events for you. Inside a callback you can safely:

- Call `Debug.Log`, mutate `UnityEngine.UI` widgets, invoke other `MonoBehaviour` methods.
- Read `Time.unscaledTime`, `Camera.main`, `SceneManager.GetActiveScene()`.

If your own code spawns a background thread and calls into the SDK from there, the SDK still marshals the resulting callback back to the main thread — you don't have to.

Bonus: **exception isolation**. If one event handler throws, the remaining handlers on the same event still fire (the SDK's internal `SafeEventInvoker`). The throw is reported via the project's log helper. This is intentional, but to avoid silent failures, wrap risky handler bodies with your own try/catch and a meaningful log.

## 5. Dispose discipline

- Every ad class implements `IDisposable`. Explicit `Dispose()` is preferred — a finalizer cleans up if you forget, but non-deterministically.
- `Dispose()` is **idempotent**. Calling it twice (e.g., `OnDisable` firing twice during teardown) is safe.
- After `Dispose()`, calling `Load()` / `Show()` / `SetCustomData()` throws `ObjectDisposedException`. `IsReady()` returns `false` without throwing.
- Recommended pattern:
  ```csharp
  _ad?.Dispose();
  _ad = null;            // also blocks accidental reuse + helps GC
  ```

## 6. Common anti-patterns

### 6-1. Event subscription leak
```csharp
// ✗ Subscribing in OnEnable without unsubscribing in OnDisable accumulates duplicates.
void OnEnable() { _ad.OnAdLoaded += Handler; }
// (no OnDisable counterpart)
```
✓ Use the `Dispose()` + null pattern (above) OR explicit `-=` in `OnDisable`. One or the other, never neither.

### 6-2. Show before Load completes
```csharp
// ✗ Show fires immediately after Load — the ad isn't cached yet.
_ad = new DaroInterstitialAd(unit);
_ad.Load();
_ad.Show();
```
✓ Wait for `OnAdLoaded` or guard with `IsReady()`.

### 6-3. Reuse after Dispose
```csharp
// ✗ ObjectDisposedException on the second call.
_ad.Dispose();
_ad.Load();
```
✓ Construct a fresh instance after disposal. The standard `OnDisable` (Dispose+null) → `OnEnable` (new + events + Load) cycle handles this.

### 6-4. Static field holding the ad
```csharp
// ✗ Static lifetime survives scene reload / domain reload — the cached
// native handle desyncs from the lifecycle of your actual gameplay code.
public static DaroInterstitialAd Shared;
```
✓ Hold the ad in a `MonoBehaviour` instance field, scoped to the scene that uses it.

### 6-5. Calling Show() from a background thread
```csharp
// ✗ Native bridges expect main-thread calls.
Task.Run(() => _ad.Show());
```
✓ Use a coroutine, `Update()`, or any user-input callback (all main-thread). If you must coordinate with a background task, marshal back yourself before calling SDK methods.

### 6-6. Manual reload inside AppOpen's OnAdDismissed
AppOpen only. The SDK auto-preloads after dismiss; a manual `Load()` here creates a race and wastes mediation inventory. See [`ad-formats/appopen.md`](ad-formats/appopen.md) for the full reasoning.

## 7. Which format for which scenario

| Scenario | Recommended format | KB |
|---|---|---|
| Interrupt between game rounds | Interstitial | [interstitial.md](ad-formats/interstitial.md) |
| User-initiated reward ("get 5 coins") | Rewarded | [ad-formats/rewarded.md](ad-formats/rewarded.md) |
| Returning from background to foreground | AppOpen | [ad-formats/appopen.md](ad-formats/appopen.md) |
| Persistent strip at top/bottom of screen | Banner | (phase 2) |
| Native widget / carousel inside game UI | Native / LightPopup | (phase 2) |

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (whole controller), SDK/Runtime/DaroSdk.cs, SDK/Runtime/DaroInterstitialAd.cs, SDK/Runtime/Internal/SafeEventInvoker.cs, SDK/Runtime/Internal/MainThreadDispatcher.cs, docs/features/event-handler.md -->
