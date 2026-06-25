# AppOpen — auto-trigger on foreground return

Fullscreen ad shown when the app transitions from background back to foreground. **Crucially different from Interstitial / Rewarded**: the user doesn't tap a "watch ad" button — your code chooses the moment to show.

## Minimal integration

```csharp
using Daro;
using System.Collections;
using UnityEngine;

public sealed class AppOpenHost : MonoBehaviour
{
    [SerializeField] private string _adUnitId = "your-appopen-ad-unit-id";
    private DaroAppOpenAd _ad;

    private void OnEnable()
    {
        _ad = new DaroAppOpenAd(_adUnitId);

        _ad.OnAdLoaded       += info => Debug.Log("appopen loaded");
        _ad.OnAdFailedToLoad += err  => Debug.LogWarning($"load failed: {err.Message}");
        _ad.OnAdShown        += info => Debug.Log("shown");
        _ad.OnAdFailedToShow += OnShowFailed;
        _ad.OnAdClicked      += info => Debug.Log("clicked");
        _ad.OnAdImpression   += info => Debug.Log("impression");
        _ad.OnAdDismissed    += info => Debug.Log("dismissed");

        _ad.Load();

        // Subscribe to the foreground-return signal — the canonical trigger.
        DaroAppStateNotifier.OnAppStateChanged += OnAppStateChanged;
    }

    private void OnAppStateChanged(DaroAppStateNotifier.AppState state)
    {
        // *** Canonical trigger *** — auto-show on foreground, not on user click.
        if (state == DaroAppStateNotifier.AppState.Foreground
            && _ad != null
            && _ad.IsReady())
        {
            _ad.Show();
        }
    }

    private void OnShowFailed(DaroAdDisplayError err)
    {
        Debug.LogWarning($"appopen show failed: {err.Message}");
        // Transient "already showing" should NOT trigger a manual reload
        // (see the "Don't reload after Dismiss" section). Only reload when
        // the cache is genuinely broken.
        if (err.Code != DaroAdDisplayErrorCode.FullscreenAdAlreadyShowing)
            _ad?.Load();
    }

    private void OnDisable()
    {
        DaroAppStateNotifier.OnAppStateChanged -= OnAppStateChanged;
        _ad?.Dispose();
        _ad = null;
    }
}
```

## Decisive differences vs Interstitial / Rewarded

1. **Trigger is a lifecycle event, not a user action.** Canonical = `DaroAppStateNotifier.OnAppStateChanged` Foreground transition.
2. **Don't show on cold-start.** Showing an ad as the first thing on app launch is a negative impression. Use only the warm path (background → foreground).
3. **Daro auto-preloads after dismiss.** Calling `Load()` from `OnAdDismissed` creates a race (see below).

## `DaroAppStateNotifier` — app state signal

```csharp
public static class DaroAppStateNotifier
{
    public enum AppState { Background, Foreground }
    public static event Action<AppState> OnAppStateChanged;
    public static AppState CurrentState { get; }
}
```

- Fires on the main thread.
- Same-state transitions are coalesced (no Foreground → Foreground duplicates).
- **No late-subscriber replay** — transitions that happened before you subscribed are lost. Subscribe in `Awake` / `OnEnable` to catch the first background→foreground.

## Android readiness and cache behavior

Android AppOpen uses a native cache manager. The Unity shim keeps `Load()` cache-aware:

- **Cache empty**: `Load()` starts a native fetch and a finite readiness poll. If the native AppOpen manager's lifecycle preload path intercepts the listener callback, the shim synthesizes one `OnAdLoaded` as soon as `IsReady()` turns true.
- **Cache filled**: `Load()` does not destroy and refetch the cached ad. It immediately reports `OnAdLoaded` with `latency=0`, meaning "cache reuse, no fetch occurred."

Production-correct display policy: use `OnAdLoaded` to update UI, but gate `Show()` with **`IsReady()`** immediately before showing.

```csharp
// Polling coroutine — low cost, finite deadline.
private IEnumerator PollReady()
{
    var deadline = Time.unscaledTime + 30f;
    while (Time.unscaledTime < deadline)
    {
        if (_ad != null && _ad.IsReady()) yield break;  // ready
        yield return new WaitForSeconds(0.5f);
    }
    Debug.LogWarning("AppOpen never became ready");
}
```

See `Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs` `PollAppOpenReady` for a sample status-label poll. iOS is unaffected by the Android cache manager behavior.

## ⚠ Do not call `Load()` from `OnAdDismissed`

Daro's `DaroAppOpenAdManager` builds in an `autoPreloadAfterDismiss` policy that silently fetches the next ad. A manual `Load()` call here:

- Races against the auto-preload.
- Wastes mediation inventory.
- Can trip `FullscreenAdAlreadyLoading` (-26).

```csharp
// ✗ Manual reload after dismiss — race.
void OnAdDismissed(DaroAdInfo) { _ad.Load(); }
```

✓ Update status UI in `OnAdDismissed` only. The next show-readiness check should poll `IsReady()` again.

Exception: in `OnAdFailedToShow`, real cache corruption (anything other than the "already showing" transient) can warrant a manual reload — see `OnShowFailed` in the snippet above.

## ⚠ Dismiss race right after Foreground

If a Foreground transition fires while a previous dismiss is still pending, `Show()` raises `OnAdFailedToShow("Ad is already showing")`.

Response: check `err.Code == DaroAdDisplayErrorCode.FullscreenAdAlreadyShowing` and do NOT call `Load()`. Just update the status label.

## Recommended display policy

- **First app launch (cold-start)**: do NOT show. Negative first impression.
- **Background → foreground (warm)**: auto-`Show()` when `IsReady()`.
- **Too frequent shows**: spammy. Add a per-session cooldown (e.g., last shown < N minutes ago → skip). Cooldown duration is a product decision for your game.

## Pitfalls summary

| Pattern | Effect |
|---|---|
| Manual `Load()` in `OnAdDismissed` | race + wasted inventory |
| Calling `Show()` without an `IsReady()` guard | stale UI state can race the native cache |
| Subscribing to `OnAppStateChanged` late (after Start) | first background→foreground missed |
| Forgetting to unsubscribe in `OnDisable` | dangling subscription after scene reload |
| Showing on cold-start | negative UX |

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs (CreateAppOpen 528-579, OnAppStateChanged 383-403, PollAppOpenReady 493-511, OnShowFailed 587-593), SDK/Runtime/DaroAppOpenAd.cs, SDK/Runtime/DaroAppStateNotifier.cs, docs/features/native-bridge.md (AppOpen race follow-up tracker) -->
