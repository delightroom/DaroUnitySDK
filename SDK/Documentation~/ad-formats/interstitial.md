# Interstitial — minimal-correct integration

Fullscreen interrupt ad. Shown between game rounds, on menu transitions, or after a significant user action. The simplest format — the baseline that Rewarded and AppOpen build on.

## Minimal integration

```csharp
using Daro;
using UnityEngine;

public sealed class InterstitialHost : MonoBehaviour
{
    [SerializeField] private string _adUnitId = "your-interstitial-ad-unit-id";
    private DaroInterstitialAd _ad;

    private void OnEnable()
    {
        _ad = new DaroInterstitialAd(_adUnitId);

        _ad.OnAdLoaded       += OnLoaded;
        _ad.OnAdFailedToLoad += OnLoadFailed;
        _ad.OnAdShown        += OnShown;
        _ad.OnAdFailedToShow += OnShowFailed;
        _ad.OnAdClicked      += OnClicked;
        _ad.OnAdImpression   += OnImpression;
        _ad.OnAdDismissed    += OnDismissed;

        _ad.Load();
    }

    public void Show()
    {
        if (_ad != null && _ad.IsReady())
            _ad.Show();
        else
            Debug.LogWarning("Interstitial not ready");
    }

    private void OnDisable()
    {
        _ad?.Dispose();
        _ad = null;
    }

    // --- handlers — all main-thread ---
    private void OnLoaded(DaroAdInfo info)              => Debug.Log($"loaded latency={info.Latency}ms");
    private void OnLoadFailed(DaroAdLoadError e)        => Debug.LogWarning($"load failed code={e.Code} raw={e.RawCode}: {e.Message}");
    private void OnShown(DaroAdInfo info)               => Debug.Log("shown");
    private void OnShowFailed(DaroAdDisplayError e)     => Debug.LogWarning($"show failed code={e.Code}: {e.Message}");
    private void OnClicked(DaroAdInfo info)             => Debug.Log("clicked");
    private void OnImpression(DaroAdInfo info)          => Debug.Log("impression");
    private void OnDismissed(DaroAdInfo info)
    {
        Debug.Log("dismissed");
        // Preload the next interstitial — reusing the same instance is fine.
        _ad?.Load();
    }
}
```

## Event ordering

1. `Load()` → (mediation responds) → `OnAdLoaded` *or* `OnAdFailedToLoad`.
2. `Show()` → `OnAdImpression` (mediation impression count) + `OnAdShown` (user-visible). The order between these two depends on mediation; assume each fires exactly once.
3. (Optional) user click → `OnAdClicked` (may fire one or more times depending on mediation behavior).
4. Ad closes → `OnAdDismissed`. Trigger the next `Load()` from this handler.
5. `Show()` failure (expired cache, another fullscreen already up, etc.) → `OnAdFailedToShow`. The Impression / Shown / Dismissed sequence does NOT fire in this branch.

## Reload policy

- **Recommended**: call `_ad?.Load()` from `OnAdDismissed`. Keeps the next interrupt prepared.
- The same instance can be reused — event handlers are registered once.
- To switch to a different ad unit, `Dispose()` and construct a new `DaroInterstitialAd(otherUnitId)`.

## Common pitfalls (Interstitial-specific)

### Polling `Load()` every frame
```csharp
// ✗ Load is async; calling every frame overwhelms the mediation layer.
void Update() { if (!_ad.IsReady()) _ad.Load(); }
```
✓ Retry with backoff inside `OnAdFailedToLoad`. Pre-fetch the next round inside `OnAdDismissed`.

### Ignoring `OnAdFailedToShow`
The user expected an ad and the show failed → broken UX. Add a fallback (advance to the next screen, show a toast). Don't let the user stare at nothing.

### Stale cache after long backgrounding
Mediation caches can expire if the app sits in the background for several minutes. If `OnAdFailedToShow` reports `FullscreenAdNotReady`, call `Load()` to rebuild the cache.

## Error codes you'll see in practice

Full enum lives in [`../api-reference.md`](../api-reference.md) under `DaroAdLoadErrorCode` / `DaroAdDisplayErrorCode`.

| Code | Meaning | Recommended response |
|---|---|---|
| `NoFill` (204) | no inventory available right now | backoff + retry, no user-visible action |
| `NotInitialized` (-2) | `Load()` called before `InitializeAsync()` resolved | `await DaroSdk.InitializeAsync()` first |
| `InvalidAdUnitIdentifier` (-5603) | typo / wrong app / dashboard misconfigured | verify dashboard + bundle id |
| `NetworkError` (-1000) / `NoNetwork` (-1009) | connectivity problem | user-facing message + retry |
| `FullscreenAdAlreadyLoading` (-26) | concurrent `Load()` calls | guard against duplicate invocations |
| `FullscreenAdAlreadyShowing` (-23, display) | another fullscreen is on screen | reschedule |

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs:427-438, SDK/Runtime/DaroInterstitialAd.cs, SDK/Runtime/Models/DaroAdLoadErrorCode.cs, SDK/Runtime/Models/DaroAdDisplayErrorCode.cs -->
