#nullable enable
#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using AOT;
using UnityEngine.Scripting;

namespace Daro.Internal
{
    /// <summary>
    /// iOS implementation of <see cref="IDaroPlatform"/>. Sits on top of
    /// <c>DaroUnityBridge.mm</c> (which wraps <c>DaroObjCBridge</c>).
    /// See sketch CD-1, CD-3, CD-5, CD-6, CD-8.
    /// </summary>
    /// <remarks>
    /// <para>Threading: <see cref="DaroUnityBridge"/> guarantees main-queue
    /// delivery for every callback (sketch §CD-5, source-verified) — this
    /// class therefore does NOT enqueue through <c>MainThreadDispatcher</c>
    /// before invoking event slots. <c>MainThreadDispatcher.EnsureCreated</c>
    /// is still called once on init for coroutine hosting and
    /// <c>DaroAppStateNotifier</c> use.</para>
    ///
    /// <para>Stripping defense: <c>SDK/Runtime/link.xml</c> preserves the
    /// entire <c>Daro.Internal</c> namespace; <see cref="OnNativeEvent"/>
    /// additionally carries <see cref="PreserveAttribute"/> at the call site
    /// to defend against aggressive Unity 6 IL2CPP configurations that have
    /// been observed to strip <see cref="MonoPInvokeCallbackAttribute"/>
    /// methods despite namespace coverage (sketch CD-8).</para>
    /// </remarks>
    internal sealed class DaroIOSPlatform : IDaroPlatform, IDaroIosEventSink
    {
        // ── DllImport callback typedef ───────────────────────────────────
        private delegate void DaroUnityCallbackFn(string adUnitId, string eventJson);

        // ── 8 event slots (set once by DaroSdk; fire on main thread) ─────
        private Action<string, DaroAdInfo>?                 _onAdLoaded;
        private Action<string, DaroAdLoadError>?            _onAdFailedToLoad;
        private Action<string, DaroAdInfo>?                 _onAdShown;
        private Action<string, DaroAdDisplayError>?         _onAdFailedToShow;
        private Action<string, DaroAdInfo>?                 _onAdClicked;
        private Action<string, DaroAdInfo>?                 _onAdImpression;
        private Action<string, DaroAdInfo>?                 _onAdDismissed;
        private Action<string, DaroAdInfo, DaroRewardItem>? _onEarnedReward;
        private Action<string, DaroAdInfo>?                 _onAdHidden;
        private Action<string, DaroAdInfo, DaroRevenueInfo>? _onAdRevenuePaid;

        // ── Static state for [MonoPInvokeCallback] dispatch ──────────────
        // The static handler can only access static state — the instance is
        // resolved here. This works because there is exactly one
        // DaroIOSPlatform per process (DaroPlatform.Current is a singleton).
        private static DaroIOSPlatform? _instance;
        private static TaskCompletionSource<bool>? _pendingInitTcs;

        // ── IDaroPlatform: SDK lifecycle ─────────────────────────────────

        public Task InitializeAsync(DaroSdkInitParams initParams)
        {
            DaroLog.Verbose("Sdk", $"Platform[iOS].InitializeAsync logLevel={initParams.LogLevel} hasGdprConsent={initParams.HasGdprConsent} doNotSell={initParams.DoNotSell}");
            _instance = this;
            MainThreadDispatcher.EnsureCreated();

            DaroUnity_SetCallback(OnNativeEvent);

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _pendingInitTcs = tcs;

            DaroUnity_Initialize(
                DaroIOSEncoding.NullableBoolToInt(initParams.HasGdprConsent),
                initParams.GdprConsentString,
                DaroIOSEncoding.NullableBoolToInt(initParams.DoNotSell),
                initParams.CcpaConsentString,
                // 통합 SDK 는 변종과 무관하게 `setCoppa:` 를 노출하므로 실제
                // 값을 넘긴다. CD-12 의 `-1` 하드코딩은 구 브리지에서 이 심볼이
                // MAX 변종에 없던 시절의 제약이었다 — Android 와 어긋나 있었다.
                DaroIOSEncoding.NullableBoolToInt(initParams.IsTaggedForChildDirectedTreatment),
                (int)initParams.LogLevel);

            return tcs.Task;
        }

        // ── IDaroPlatform: Runtime settings ──────────────────────────────

        public void SetUserId(string userId)
        {
            DaroLog.Verbose("Sdk", $"Platform[iOS].SetUserId userId='{userId}'");
            DaroUnity_SetUserId(userId);
        }

        public void SetAppMuted(bool muted)
        {
            DaroLog.Verbose("Sdk", $"Platform[iOS].SetAppMuted muted={muted}");
            DaroUnity_SetAppMuted(muted);
        }

        public void SetLogLevel(DaroLogLevel level)
        {
            DaroLog.Verbose("Sdk", $"Platform[iOS].SetLogLevel level={level} (raw={(int)level})");
            DaroUnity_SetLogLevel((int)level);
        }

        // IDaroPlatform-level teardown trigger. Called by
        // DaroSdk.MarkShuttingDown on app-quit / Unity-runtime-teardown.
        // Best-effort: never throws (DaroLog.Exception isolates). Idempotent
        // — native side's DaroUnity_DestroyAll is safe to call twice (each
        // helper noops on empty dicts).
        //
        // See SDK/Plugins/iOS/DaroUnityBridge.mm DaroUnity_DestroyAll for the
        // native side: clears 3 fullscreen dicts + delegates to per-format
        // helpers (NativeAd / Banner / LightPopup) under the A2 atomic
        // destroyed-flag coordinated hybrid model.
        public void DestroyAll()
        {
            DaroLog.Verbose("Sdk", "Platform[iOS].DestroyAll");
            try
            {
                DaroUnity_DestroyAll();
            }
            catch (Exception e)
            {
                DaroLog.Exception("Sdk", e);
            }
        }

        // ── IDaroPlatform: Instance lifecycle ────────────────────────────

        public void CreateInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].CreateInterstitial adUnit='{adUnitId}'");
            DaroUnity_CreateInterstitial(adUnitId);
        }

        public void CreateRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].CreateRewarded adUnit='{adUnitId}'");
            DaroUnity_CreateRewarded(adUnitId);
        }

        public void CreateAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].CreateAppOpen adUnit='{adUnitId}'");
            DaroUnity_CreateAppOpen(adUnitId);
        }

        public void LoadInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].LoadInterstitial adUnit='{adUnitId}'");
            DaroUnity_LoadInterstitial(adUnitId);
        }

        public void LoadRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].LoadRewarded adUnit='{adUnitId}'");
            DaroUnity_LoadRewarded(adUnitId);
        }

        public void LoadAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].LoadAppOpen adUnit='{adUnitId}'");
            DaroUnity_LoadAppOpen(adUnitId);
        }

        // bool is marshalled as int by DllImport — `!= 0` keeps IL2CPP / ARM64
        // bool ABI variance from biting us. IsReady* polled frequently — no Verbose
        // here to avoid console spam (matches Android pattern).
        public bool IsInterstitialReady(string adUnitId) => DaroUnity_IsInterstitialReady(adUnitId) != 0;
        public bool IsRewardedReady(string adUnitId)     => DaroUnity_IsRewardedReady(adUnitId) != 0;
        public bool IsAppOpenReady(string adUnitId)      => DaroUnity_IsAppOpenReady(adUnitId) != 0;

        public void ShowInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].ShowInterstitial adUnit='{adUnitId}'");
            DaroUnity_ShowInterstitial(adUnitId);
        }

        public void ShowRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].ShowRewarded adUnit='{adUnitId}'");
            DaroUnity_ShowRewarded(adUnitId);
        }

        public void ShowAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].ShowAppOpen adUnit='{adUnitId}'");
            DaroUnity_ShowAppOpen(adUnitId);
        }

        public void DestroyInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].DestroyInterstitial adUnit='{adUnitId}'");
            DaroUnity_DestroyInterstitial(adUnitId);
        }

        public void DestroyRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].DestroyRewarded adUnit='{adUnitId}'");
            DaroUnity_DestroyRewarded(adUnitId);
        }

        public void DestroyAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].DestroyAppOpen adUnit='{adUnitId}'");
            DaroUnity_DestroyAppOpen(adUnitId);
        }

        public void SetRewardedCustomData(string adUnitId, string customData)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].SetRewardedCustomData adUnit='{adUnitId}' len={customData.Length}");
            DaroUnity_SetRewardedCustomData(adUnitId, customData);
        }

        // ── IDaroPlatform: event slots (set once by DaroSdk) ─────────────

        public Action<string, DaroAdInfo>?                 OnAdLoaded       { set => _onAdLoaded       = value; }
        public Action<string, DaroAdLoadError>?            OnAdFailedToLoad { set => _onAdFailedToLoad = value; }
        public Action<string, DaroAdInfo>?                 OnAdShown        { set => _onAdShown        = value; }
        public Action<string, DaroAdDisplayError>?         OnAdFailedToShow { set => _onAdFailedToShow = value; }
        public Action<string, DaroAdInfo>?                 OnAdClicked      { set => _onAdClicked      = value; }
        public Action<string, DaroAdInfo>?                 OnAdImpression   { set => _onAdImpression   = value; }
        public Action<string, DaroAdInfo>?                 OnAdDismissed    { set => _onAdDismissed    = value; }
        public Action<string, DaroAdInfo, DaroRewardItem>? OnEarnedReward   { set => _onEarnedReward   = value; }
        public Action<string, DaroAdInfo>?                 OnAdHidden       { set => _onAdHidden       = value; }
        public Action<string, DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid { set => _onAdRevenuePaid  = value; }

        // ── Banner (native-view-overlay-on-GL-surface) ───────────────────
        //
        // See docs/features/native-bridge.md (Banner overlay / iOS).
        // - DaroBannerSize / DaroBannerPosition: ordinal pass-through —
        //   C# enum values match DaroObjCBannerSize and the native shim's
        //   gravity ordinal contract directly (sketch §"Event Routing —
        //   Zero Dispatcher Changes").

        public void CreateBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].CreateBanner adUnit='{adUnitId}'");
            DaroUnity_CreateBanner(adUnitId);
        }

        public void LoadBanner(string adUnitId, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].LoadBanner adUnit='{adUnitId}' size={size}");
            DaroUnity_LoadBanner(adUnitId, (int)size);
        }

        public void ShowBanner(string adUnitId)
        {
            // OnAdShown is fired synchronously by DaroBannerAd.Show() in the
            // C# facade — there is no native callback for banner visibility.
            DaroLog.Verbose("Banner", $"Platform[iOS].ShowBanner adUnit='{adUnitId}'");
            DaroUnity_ShowBanner(adUnitId);
        }

        public void HideBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].HideBanner adUnit='{adUnitId}'");
            DaroUnity_HideBanner(adUnitId);
        }

        public void DestroyBanner(string adUnitId)
        {
            // Safe before LoadBanner — native shim guards nil view.
            DaroLog.Verbose("Banner", $"Platform[iOS].DestroyBanner adUnit='{adUnitId}'");
            DaroUnity_DestroyBanner(adUnitId);
        }

        public void SetBannerPosition(string adUnitId, DaroBannerPosition position)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].SetBannerPosition adUnit='{adUnitId}' position={position}");
            DaroUnity_SetBannerPosition(adUnitId, (int)position);
        }

        public bool TryGetBannerScreenRect(string adUnitId, out UnityEngine.Rect rect)
        {
            rect = default;
            // Shim returns the rect already in Unity screen px, bottom-left origin
            // (points→px + y-flip done native-side). Returns 0 if not laid out /
            // hidden / unknown adUnitId.
            if (DaroUnity_GetBannerScreenRect(adUnitId,
                    out float x, out float y, out float w, out float h) == 0)
                return false;
            rect = new UnityEngine.Rect(x, y, w, h);
            return true;
        }

        // ── IDaroIosEventSink (forwards to event slots / pending Tcs) ────

        void IDaroIosEventSink.Loaded(string adUnitId, DaroAdInfo info) =>
            _onAdLoaded?.Invoke(adUnitId, info);
        void IDaroIosEventSink.FailedToLoad(string adUnitId, DaroAdLoadError error) =>
            _onAdFailedToLoad?.Invoke(adUnitId, error);
        void IDaroIosEventSink.Shown(string adUnitId, DaroAdInfo info) =>
            _onAdShown?.Invoke(adUnitId, info);
        void IDaroIosEventSink.FailedToShow(string adUnitId, DaroAdDisplayError error) =>
            _onAdFailedToShow?.Invoke(adUnitId, error);
        void IDaroIosEventSink.Clicked(string adUnitId, DaroAdInfo info) =>
            _onAdClicked?.Invoke(adUnitId, info);
        void IDaroIosEventSink.Impression(string adUnitId, DaroAdInfo info) =>
            _onAdImpression?.Invoke(adUnitId, info);
        void IDaroIosEventSink.Hidden(string adUnitId, DaroAdInfo info) =>
            _onAdHidden?.Invoke(adUnitId, info);
        void IDaroIosEventSink.Dismissed(string adUnitId, DaroAdInfo info) =>
            _onAdDismissed?.Invoke(adUnitId, info);
        void IDaroIosEventSink.EarnedReward(string adUnitId, DaroAdInfo info, DaroRewardItem reward) =>
            _onEarnedReward?.Invoke(adUnitId, info, reward);
        void IDaroIosEventSink.RevenuePaid(string adUnitId, DaroAdInfo info, DaroRevenueInfo revenue) =>
            _onAdRevenuePaid?.Invoke(adUnitId, info, revenue);

        void IDaroIosEventSink.SdkInitialized()
        {
            var tcs = _pendingInitTcs;
            _pendingInitTcs = null;
            tcs?.TrySetResult(true);
        }

        void IDaroIosEventSink.SdkInitFailed(DaroSdkInitException ex)
        {
            var tcs = _pendingInitTcs;
            _pendingInitTcs = null;
            tcs?.TrySetException(ex);
        }

        // ── [MonoPInvokeCallback] entry — kept tiny; logic in dispatcher ─

        [Preserve, MonoPInvokeCallback(typeof(DaroUnityCallbackFn))]
        private static void OnNativeEvent(string adUnitId, string eventJson)
        {
            var inst = _instance;
            if (inst == null) return;
            DaroIOSEventDispatcher.Dispatch(adUnitId, eventJson, inst);
        }

        // ── extern C surface (sketch §"Interfaces — Extern C Surface") ───

        private const string DLL = "__Internal";

        [DllImport(DLL)] private static extern void DaroUnity_SetCallback(DaroUnityCallbackFn callback);

        [DllImport(DLL)] private static extern void DaroUnity_Initialize(
            int hasGdprConsent, string? gdprConsentString,
            int doNotSell,      string? ccpaConsentString,
            int isTaggedForCoppa, int logLevel);

        [DllImport(DLL)] private static extern void DaroUnity_SetUserId(string userId);
        [DllImport(DLL)] private static extern void DaroUnity_SetAppMuted(bool muted);
        [DllImport(DLL)] private static extern void DaroUnity_SetLogLevel(int level);

        // Sprint native-object-lifecycle-cleanup §DestroyAll hygiene path.
        // Native side shipped in prior turn — see SDK/Plugins/iOS/DaroUnityBridge.mm.
        [DllImport(DLL)] private static extern void DaroUnity_DestroyAll();

        [DllImport(DLL)] private static extern void DaroUnity_CreateInterstitial(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_LoadInterstitial(string adUnitId);
        [DllImport(DLL)] private static extern int  DaroUnity_IsInterstitialReady(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_ShowInterstitial(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyInterstitial(string adUnitId);

        [DllImport(DLL)] private static extern void DaroUnity_CreateRewarded(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_LoadRewarded(string adUnitId);
        [DllImport(DLL)] private static extern int  DaroUnity_IsRewardedReady(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_ShowRewarded(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyRewarded(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_SetRewardedCustomData(string adUnitId, string customData);

        [DllImport(DLL)] private static extern void DaroUnity_CreateAppOpen(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_LoadAppOpen(string adUnitId);
        [DllImport(DLL)] private static extern int  DaroUnity_IsAppOpenReady(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_ShowAppOpen(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyAppOpen(string adUnitId);

        // ── Banner extern C surface ──────────────────────────────────────
        // Defined in SDK/Plugins/iOS/DaroUnityBannerAd.mm. No IsReady — banner
        // is an always-on overlay with no "ready" notion separate from load.
        [DllImport(DLL)] private static extern void DaroUnity_CreateBanner(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_LoadBanner(string adUnitId, int sizeOrdinal);
        [DllImport(DLL)] private static extern void DaroUnity_ShowBanner(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_HideBanner(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyBanner(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_SetBannerPosition(string adUnitId, int positionOrdinal);
        // Returns 1 + rect (Unity screen px, bottom-left origin) if the banner is
        // laid out; 0 otherwise. Out params untouched when 0.
        [DllImport(DLL)] private static extern int DaroUnity_GetBannerScreenRect(
            string adUnitId, out float x, out float y, out float width, out float height);

        // ── Light Popup extern C surface ─────────────────────────────────
        // Defined in SDK/Plugins/iOS/DaroUnityLightPopup.mm.
        // 36 float = 9 colors × 4 channels (RGBA, [0,1] pre-divided) — see
        // sketch §"Configuration extern C signature" for the rejected
        // alternatives (byte 4-arg, packed int).
        [DllImport(DLL)] private static extern void DaroUnity_CreateLightPopup(
            string adUnitId,
            float bgR,        float bgG,        float bgB,        float bgA,
            float containerR, float containerG, float containerB, float containerA,
            float adMarkTextR,float adMarkTextG,float adMarkTextB,float adMarkTextA,
            float adMarkBgR,  float adMarkBgG,  float adMarkBgB,  float adMarkBgA,
            float closeBtnR,  float closeBtnG,  float closeBtnB,  float closeBtnA,
            float titleR,     float titleG,     float titleB,     float titleA,
            float bodyR,      float bodyG,      float bodyB,      float bodyA,
            float ctaBgR,     float ctaBgG,     float ctaBgB,     float ctaBgA,
            float ctaTextR,   float ctaTextG,   float ctaTextB,   float ctaTextA,
            string closeButtonText);
        [DllImport(DLL)] private static extern void DaroUnity_LoadLightPopup(string adUnitId);
        [DllImport(DLL)] private static extern bool DaroUnity_IsLightPopupReady(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_ShowLightPopup(string adUnitId);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyLightPopup(string adUnitId);

        // ── Light Popup (modal popup + auto-dismiss preset) ──────────────
        //
        // See docs/features/native-bridge.md (Light Popup / iOS).
        // - 9 Color32 → 36 float (RGBA per channel, pre-divided to [0,1] via
        //   B(byte) helper); shim builds DaroObjCLightPopupConfiguration with
        //   [UIColor colorWithRed:green:blue:alpha:] (no further conversion).
        // - 9:9 UIColor mapping is true 1:1 (sketch §"Configuration Field
        //   Mapping"); CloseButtonColor → closeButtonTextColor only because
        //   iOS lacks a separate icon-color slot (CD-3).

        public void CreateLightPopup(string adUnitId, DaroLightPopupAdOptions o)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].CreateLightPopup adUnit='{adUnitId}'");
            DaroUnity_CreateLightPopup(
                adUnitId,
                B(o.BackgroundColor.r),            B(o.BackgroundColor.g),            B(o.BackgroundColor.b),            B(o.BackgroundColor.a),
                B(o.ContainerColor.r),             B(o.ContainerColor.g),             B(o.ContainerColor.b),             B(o.ContainerColor.a),
                B(o.AdMarkLabelTextColor.r),       B(o.AdMarkLabelTextColor.g),       B(o.AdMarkLabelTextColor.b),       B(o.AdMarkLabelTextColor.a),
                B(o.AdMarkLabelBackgroundColor.r), B(o.AdMarkLabelBackgroundColor.g), B(o.AdMarkLabelBackgroundColor.b), B(o.AdMarkLabelBackgroundColor.a),
                B(o.CloseButtonColor.r),           B(o.CloseButtonColor.g),           B(o.CloseButtonColor.b),           B(o.CloseButtonColor.a),
                B(o.TitleColor.r),                 B(o.TitleColor.g),                 B(o.TitleColor.b),                 B(o.TitleColor.a),
                B(o.BodyColor.r),                  B(o.BodyColor.g),                  B(o.BodyColor.b),                  B(o.BodyColor.a),
                B(o.CtaBackgroundColor.r),         B(o.CtaBackgroundColor.g),         B(o.CtaBackgroundColor.b),         B(o.CtaBackgroundColor.a),
                B(o.CtaTextColor.r),               B(o.CtaTextColor.g),               B(o.CtaTextColor.b),               B(o.CtaTextColor.a),
                o.CloseButtonText ?? "Close");
        }

        public void LoadLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].LoadLightPopup adUnit='{adUnitId}'");
            DaroUnity_LoadLightPopup(adUnitId);
        }

        public bool IsLightPopupReady(string adUnitId) => DaroUnity_IsLightPopupReady(adUnitId);

        public void ShowLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].ShowLightPopup adUnit='{adUnitId}'");
            DaroUnity_ShowLightPopup(adUnitId);
        }

        public void DestroyLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].DestroyLightPopup adUnit='{adUnitId}'");
            DaroUnity_DestroyLightPopup(adUnitId);
        }

        // byte (0-255) → float (0.0-1.0) for UIColor CGFloat channels.
        // Pre-divided at C# call site so the ObjC shim does direct
        // [UIColor colorWithRed:...] without further conversion.
        private static float B(byte b) => b / 255f;

        // ── Native ad (CD-8 instance-owned) ──────────────────────────────
        // CD-1: Native uses a per-instance handle (vs adUnitId-keyed dict for
        // other formats); same adUnitId × N instances yields N independent
        // handles. Implementation: see DaroIOSNativeAdHandle (this directory)
        // + DaroUnityNativeAd.mm (peer to DaroUnityBannerAd.mm).
        public INativeAdHandle CreateNativeAdHandle(string adUnitId, INativeAdEventSink sink)
        {
            DaroLog.Verbose("Native", $"Platform[iOS].CreateNativeAdHandle adUnit='{adUnitId}'");
            return new DaroIOSNativeAdHandle(adUnitId, sink);
        }
    }
}
#endif
