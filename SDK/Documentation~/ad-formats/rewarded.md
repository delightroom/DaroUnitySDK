# Rewarded — reward-on-completion integration

User-initiated video ad that grants in-game currency / items when the user finishes watching. Almost identical to Interstitial but adds **`OnEarnedReward`** and **`SetCustomData()`**.

Read [`interstitial.md`](interstitial.md) first — the shared patterns live there.

## Minimal integration

```csharp
using Daro;
using UnityEngine;

public sealed class RewardedHost : MonoBehaviour
{
    [SerializeField] private string _adUnitId = "your-rewarded-ad-unit-id";
    private DaroRewardedAd _ad;

    private void OnEnable()
    {
        _ad = new DaroRewardedAd(_adUnitId);

        _ad.OnAdLoaded       += info => Debug.Log("rewarded loaded");
        _ad.OnAdFailedToLoad += err  => Debug.LogWarning($"load failed: {err.Message}");
        _ad.OnAdShown        += info => Debug.Log("shown");
        _ad.OnAdFailedToShow += err  => Debug.LogWarning($"show failed: {err.Message}");
        _ad.OnAdClicked      += info => Debug.Log("clicked");
        _ad.OnAdImpression   += info => Debug.Log("impression");
        _ad.OnAdDismissed    += info => Debug.Log("dismissed");

        // Rewarded-specific: fires when the user finishes watching.
        _ad.OnEarnedReward   += OnEarnedReward;

        _ad.Load();
    }

    public void ShowForReward()
    {
        if (_ad != null && _ad.IsReady())
        {
            // (Optional) S2S validation: pass a server-side payload to your backend.
            _ad.SetCustomData($"user-id-{System.Guid.NewGuid()}");
            _ad.Show();
        }
    }

    private void OnEarnedReward(DaroAdInfo info, DaroRewardItem reward)
    {
        // The user watched the full video. Grant the in-game reward here.
        Debug.Log($"earned {reward.Amount} of {reward.RewardType}");
        GameInventory.Add(reward.RewardType, reward.Amount);
    }

    private void OnDisable()
    {
        _ad?.Dispose();
        _ad = null;
    }
}
```

## Differences vs Interstitial (the whole list)

1. **Class**: `DaroRewardedAd` (constructor signature identical: `(string adUnitId)`).
2. **Extra event**: `OnEarnedReward` — `Action<DaroAdInfo, DaroRewardItem>`. Fires once per completed view.
3. **Extra method**: `SetCustomData(string)` — opaque string forwarded to your mediation's reward callback for S2S validation. Call before `Show()`.

Everything else (Load / IsReady / Show / Dispose / the other seven events) matches Interstitial.

## When `OnEarnedReward` fires

- Only after a **full** video view. Closing the ad mid-playback fires `OnAdDismissed` but NOT `OnEarnedReward`.
- `DaroRewardItem.Amount` and `RewardType` come from the mediation dashboard. If you haven't configured them, you get default values — not values defined in your game code.
- One firing per `Show()`. No firings before the next `Show()`.

## `DaroRewardItem` payload

```csharp
public sealed class DaroRewardItem
{
    public int    Amount     { get; }
    public string RewardType { get; }
}
```

- `Amount`: the reward count configured on the dashboard.
- `RewardType`: the reward type string configured on the dashboard (e.g. `"coin"`, `"gem"`).
- Both are dashboard values — the game does NOT decide them at runtime.

## `SetCustomData()` — server-to-server validation

When your backend does its own reward validation:

```csharp
_ad.SetCustomData("session=abc123,user=42");  // opaque string
_ad.Show();
```

This string is forwarded to the mediation network's reward callback, then your backend receives it and validates before granting the reward. The SDK is a pure pass-through — the schema is whatever your backend agrees on. Useful against client-side fraud.

## Common pitfalls (Rewarded-specific)

### Pre-loading the next ad inside `OnEarnedReward`
```csharp
// ✗ EarnedReward fires before Dismissed — call Load() from Dismissed instead.
void OnEarnedReward(DaroAdInfo, DaroRewardItem) { _ad.Load(); }
```
✓ Use `OnAdDismissed` to preload the next round (same as Interstitial).

### Granting reward when the user didn't earn it
```csharp
// ✗ Granting on Dismissed gives the reward even if the user skipped the video.
void OnAdDismissed(DaroAdInfo) { GameInventory.AddCoin(10); }
```
✓ Grant only inside `OnEarnedReward`. `OnAdDismissed` only tells you the ad UI closed; full-view completion comes from `OnEarnedReward`.

### Calling `SetCustomData()` before `Load()`
```csharp
// ✗ Custom data set pre-Load may not make it into the cached ad.
_ad.SetCustomData("...");
_ad.Load();
```
✓ Order: `Load()` → wait for `OnAdLoaded` → `SetCustomData()` → `Show()`.

<!-- source: Samples/DaroExample/Assets/Scripts/Runtime/UI/DaroExampleController.cs:462-474, SDK/Runtime/DaroRewardedAd.cs, SDK/Runtime/Models/DaroRewardItem.cs -->
