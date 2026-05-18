#nullable enable
using System;
using System.Threading.Tasks;

namespace Daro.Internal
{
    /// <summary>
    /// Platform abstraction for DaroSDK. Implemented by
    /// <c>DaroIOSPlatform</c>, <c>DaroAndroidPlatform</c>, and
    /// <c>DaroEditorPlatform</c>. Keyed by <c>adUnitId</c> — each
    /// implementation holds its own internal adUnitId → native handle
    /// dictionary; <c>IntPtr</c> never appears in this interface.
    /// Event callbacks are set once by <c>DaroSdk</c> and fire on the
    /// Unity main thread.
    /// </summary>
    internal interface IDaroPlatform
    {
        // ── SDK lifecycle ─────────────────────────────────────────────────
        Task InitializeAsync(DaroSdkInitParams initParams);

        // SDK-internal teardown trigger. Called by DaroSdk.MarkShuttingDown
        // on app-quit / Unity-runtime-teardown. Clears all live native ad
        // objects + attached views (per platform's own DestroyAll semantics);
        // best-effort — must not throw. Idempotent — safe to call twice.
        // See docs/dev/native-object-lifecycle-cleanup/tasks/teardown-contract.md.
        void DestroyAll();

        // ── Runtime settings ──────────────────────────────────────────────
        void SetUserId(string userId);
        void SetAppMuted(bool muted);
        void SetLogLevel(DaroLogLevel level);

        // ── Instance lifecycle ────────────────────────────────────────────
        void CreateInterstitial(string adUnitId, string? placement);
        void CreateRewarded(string adUnitId, string? placement);
        void CreateAppOpen(string adUnitId, string? placement);

        // ── Ad operations ─────────────────────────────────────────────────
        void LoadInterstitial(string adUnitId);
        void LoadRewarded(string adUnitId);
        void LoadAppOpen(string adUnitId);

        bool IsInterstitialReady(string adUnitId);
        bool IsRewardedReady(string adUnitId);
        bool IsAppOpenReady(string adUnitId);

        void ShowInterstitial(string adUnitId);
        void ShowRewarded(string adUnitId);
        void ShowAppOpen(string adUnitId);

        void DestroyInterstitial(string adUnitId);
        void DestroyRewarded(string adUnitId);
        void DestroyAppOpen(string adUnitId);

        void SetRewardedCustomData(string adUnitId, string customData);

        // ── Banner ad operations (always-on overlay) ──────────────────────
        // Size baked at Load (DaroBannerAdView 가 ctor 시점에 size 고정).
        // Position 은 Show 후에도 변경 가능.
        void CreateBanner(string adUnitId, string? placement);
        void LoadBanner(string adUnitId, DaroBannerSize size);
        void ShowBanner(string adUnitId);
        void HideBanner(string adUnitId);
        void DestroyBanner(string adUnitId);
        void SetBannerPosition(string adUnitId, DaroBannerPosition position);

        // ── Light Popup ad operations ─────────────────────────────────────
        // Options baked at Create time (immutable per instance). placement forwarded
        // through Kotlin shim to DaroLightPopupAdUnit. Mirrors the v1 fullscreen
        // 5-method mold (Create / Load / IsReady / Show / Destroy).
        void CreateLightPopup(string adUnitId, string? placement, DaroLightPopupAdOptions options);
        void LoadLightPopup(string adUnitId);
        bool IsLightPopupReady(string adUnitId);
        void ShowLightPopup(string adUnitId);
        void DestroyLightPopup(string adUnitId);

        // ── Native ad (instance-owned, CD-8) ──────────────────────────────
        // Native ad uses a *per-instance handle* instead of the adUnitId-keyed
        // dict pattern of Interstitial / Rewarded / AppOpen / Banner. This
        // permits multi-instance for the same adUnitId (list UI use case).
        // Each handle owns its own native loader + callback proxy; routing
        // is per-instance via the supplied INativeAdEventSink. No platform
        // event slots required for native — the sink replaces them.
        // See sketch-native-ad-android.md §4.
        INativeAdHandle CreateNativeAdHandle(string adUnitId, string? placement, INativeAdEventSink sink);

        // ── Event callbacks (set once by DaroSdk; fire on main thread) ────
        // string adUnitId routes to the correct ad instance.
        Action<string, DaroAdInfo>?                 OnAdLoaded       { set; }
        Action<string, DaroAdLoadError>?            OnAdFailedToLoad { set; }
        Action<string, DaroAdInfo>?                 OnAdShown        { set; }
        Action<string, DaroAdDisplayError>?         OnAdFailedToShow { set; }
        Action<string, DaroAdInfo>?                 OnAdClicked      { set; }
        Action<string, DaroAdInfo>?                 OnAdImpression   { set; }
        Action<string, DaroAdInfo>?                 OnAdDismissed    { set; }
        Action<string, DaroAdInfo, DaroRewardItem>? OnEarnedReward   { set; }

        // Banner-only — fires after HideBanner completes. native callback 부재이므로
        // platform impl 이 Hide 호출 직후 이 slot 에 직접 enqueue.
        Action<string, DaroAdInfo>?                 OnAdHidden       { set; }
    }
}
