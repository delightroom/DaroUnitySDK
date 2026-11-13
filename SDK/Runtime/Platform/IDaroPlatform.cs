#nullable enable
using System;
using System.Threading.Tasks;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Platform abstraction for DaroSDK. Implemented by
    /// <c>DaroIOSPlatform</c>, <c>DaroAndroidPlatform</c>, and
    /// <c>DaroEditorPlatform</c>. Keyed by <c>adUnitKey</c> — each
    /// implementation holds its own internal adUnitKey → native handle
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

        void DestroyAll();

        // ── Runtime settings ──────────────────────────────────────────────
        void SetUserId(string userId);
        void SetAppMuted(bool muted);
        void SetLogLevel(DaroLogLevel level);

        // ── Instance lifecycle ────────────────────────────────────────────
        void CreateInterstitial(string adUnitKey);
        void CreateRewarded(string adUnitKey);
        void CreateAppOpen(string adUnitKey);

        // ── Ad operations ─────────────────────────────────────────────────
        void LoadInterstitial(string adUnitKey);
        void LoadRewarded(string adUnitKey);
        void LoadAppOpen(string adUnitKey);

        bool IsInterstitialReady(string adUnitKey);
        bool IsRewardedReady(string adUnitKey);
        bool IsAppOpenReady(string adUnitKey);

        void ShowInterstitial(string adUnitKey);
        void ShowRewarded(string adUnitKey);
        void ShowAppOpen(string adUnitKey);

        void DestroyInterstitial(string adUnitKey);
        void DestroyRewarded(string adUnitKey);
        void DestroyAppOpen(string adUnitKey);

        void SetRewardedCustomData(string adUnitKey, string customData);

        // ── Banner ad operations (always-on overlay) ──────────────────────
        // Size baked at Load (DaroBannerAdView 가 ctor 시점에 size 고정).
        // Position 은 Show 후에도 변경 가능.
        void CreateBanner(string adUnitKey);
        // Preload: 뷰 없이 광고 한 개를 받아 둔다. 다음 LoadBanner 가 한 번 넘겨받는다
        // ( 로딩 중이면 결과 대기, 실패·만료면 일반 로드 ). TTL·중복 판정은 네이티브 SDK 몫이다.
        void PreloadBanner(string adUnitKey, DaroBannerSize size);
        void LoadBanner(string adUnitKey, DaroBannerSize size);
        void ShowBanner(string adUnitKey);
        void HideBanner(string adUnitKey);
        void DestroyBanner(string adUnitKey);
        void SetBannerPosition(string adUnitKey, DaroBannerPosition position);

        // Banner footprint — the laid-out native banner view's actual on-screen
        // rect in Unity screen px (bottom-left origin, Screen.safeArea
        // convention). Returns false (rect = default) if not yet laid out / not
        // shown / unknown adUnitKey. Backs DaroBannerAd.GetScreenRect.
        bool TryGetBannerScreenRect(string adUnitKey, out Rect rect);

        // ── Light Popup ad operations ─────────────────────────────────────
        // Options baked at Create time (immutable per instance). Mirrors the v1 fullscreen
        // 5-method mold (Create / Load / IsReady / Show / Destroy).
        void CreateLightPopup(string adUnitKey, DaroLightPopupAdOptions? options);
        void LoadLightPopup(string adUnitKey);
        bool IsLightPopupReady(string adUnitKey);
        void ShowLightPopup(string adUnitKey);
        void DestroyLightPopup(string adUnitKey);

        // ── Native ad (instance-owned) ──────────────────────────────
        // Native ad uses a *per-instance handle* instead of the adUnitKey-keyed
        // dict pattern of Interstitial / Rewarded / AppOpen / Banner. This
        // permits multi-instance for the same adUnitKey (list UI use case).
        // Each handle owns its own native loader + callback proxy; routing
        // is per-instance via the supplied INativeAdEventSink. No platform
        // event slots required for native — the sink replaces them.

        INativeAdHandle CreateNativeAdHandle(string adUnitKey, INativeAdEventSink sink);

        // ── Event callbacks (set once by DaroSdk; fire on main thread) ────
        // string adUnitKey routes to the correct ad instance.
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

        // Banner-only — PreloadBanner 의 결과. 네이티브 광고는 INativeAdEventSink 로 간다.
        Action<string, DaroAdInfo>?                 OnAdPreloaded       { set; }
        Action<string, DaroAdLoadError>?            OnAdFailedToPreload { set; }

        // ILRD — per-impression revenue from the mediation layer.
        // Registry-keyed formats only; native ad routes revenue through
        // its INativeAdEventSink instead.
        Action<string, DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid { set; }
    }
}
