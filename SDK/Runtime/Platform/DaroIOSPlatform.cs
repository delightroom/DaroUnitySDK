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
    /// </summary>
    /// <remarks>
    /// <para>Threading: <see cref="DaroUnityBridge"/> guarantees main-queue
    /// delivery for every callback — this
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
    /// methods despite namespace coverage.</para>
    /// </remarks>
    internal sealed class DaroIOSPlatform : IDaroPlatform, IDaroIosEventSink
    {
        // ── DllImport callback typedef ───────────────────────────────────
        private delegate void DaroUnityCallbackFn(string adUnitKey, string eventJson);

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
                // 값을 넘긴다. `-1` 로 고정하면 MAX 에서도 COPPA 설정이 버려진다.
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

        public void CreateInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].CreateInterstitial adUnit='{adUnitKey}'");
            DaroUnity_CreateInterstitial(adUnitKey);
        }

        public void CreateRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].CreateRewarded adUnit='{adUnitKey}'");
            DaroUnity_CreateRewarded(adUnitKey);
        }

        public void CreateAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].CreateAppOpen adUnit='{adUnitKey}'");
            DaroUnity_CreateAppOpen(adUnitKey);
        }

        public void LoadInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].LoadInterstitial adUnit='{adUnitKey}'");
            DaroUnity_LoadInterstitial(adUnitKey);
        }

        public void LoadRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].LoadRewarded adUnit='{adUnitKey}'");
            DaroUnity_LoadRewarded(adUnitKey);
        }

        public void LoadAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].LoadAppOpen adUnit='{adUnitKey}'");
            DaroUnity_LoadAppOpen(adUnitKey);
        }

        // bool is marshalled as int by DllImport — `!= 0` keeps IL2CPP / ARM64
        // bool ABI variance from biting us. IsReady* polled frequently — no Verbose
        // here to avoid console spam (matches Android pattern).
        public bool IsInterstitialReady(string adUnitKey) => DaroUnity_IsInterstitialReady(adUnitKey) != 0;
        public bool IsRewardedReady(string adUnitKey)     => DaroUnity_IsRewardedReady(adUnitKey) != 0;
        public bool IsAppOpenReady(string adUnitKey)      => DaroUnity_IsAppOpenReady(adUnitKey) != 0;

        public void ShowInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].ShowInterstitial adUnit='{adUnitKey}'");
            DaroUnity_ShowInterstitial(adUnitKey);
        }

        public void ShowRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].ShowRewarded adUnit='{adUnitKey}'");
            DaroUnity_ShowRewarded(adUnitKey);
        }

        public void ShowAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].ShowAppOpen adUnit='{adUnitKey}'");
            DaroUnity_ShowAppOpen(adUnitKey);
        }

        public void DestroyInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[iOS].DestroyInterstitial adUnit='{adUnitKey}'");
            DaroUnity_DestroyInterstitial(adUnitKey);
        }

        public void DestroyRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].DestroyRewarded adUnit='{adUnitKey}'");
            DaroUnity_DestroyRewarded(adUnitKey);
        }

        public void DestroyAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[iOS].DestroyAppOpen adUnit='{adUnitKey}'");
            DaroUnity_DestroyAppOpen(adUnitKey);
        }

        public void SetRewardedCustomData(string adUnitKey, string customData)
        {
            DaroLog.Verbose("Rewarded", $"Platform[iOS].SetRewardedCustomData adUnit='{adUnitKey}' len={customData.Length}");
            DaroUnity_SetRewardedCustomData(adUnitKey, customData);
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

        // - DaroBannerSize / DaroBannerPosition: ordinal pass-through —
        //   C# enum values match DaroObjCBannerSize and the native shim's
        //   gravity ordinal contract directly.

        public void CreateBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].CreateBanner adUnit='{adUnitKey}'");
            DaroUnity_CreateBanner(adUnitKey);
        }

        public void LoadBanner(string adUnitKey, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].LoadBanner adUnit='{adUnitKey}' size={size}");
            DaroUnity_LoadBanner(adUnitKey, (int)size);
        }

        public void ShowBanner(string adUnitKey)
        {
            // OnAdShown is fired synchronously by DaroBannerAd.Show() in the
            // C# facade — there is no native callback for banner visibility.
            DaroLog.Verbose("Banner", $"Platform[iOS].ShowBanner adUnit='{adUnitKey}'");
            DaroUnity_ShowBanner(adUnitKey);
        }

        public void HideBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].HideBanner adUnit='{adUnitKey}'");
            DaroUnity_HideBanner(adUnitKey);
        }

        public void DestroyBanner(string adUnitKey)
        {
            // Safe before LoadBanner — native shim guards nil view.
            DaroLog.Verbose("Banner", $"Platform[iOS].DestroyBanner adUnit='{adUnitKey}'");
            DaroUnity_DestroyBanner(adUnitKey);
        }

        public void SetBannerPosition(string adUnitKey, DaroBannerPosition position)
        {
            DaroLog.Verbose("Banner", $"Platform[iOS].SetBannerPosition adUnit='{adUnitKey}' position={position}");
            DaroUnity_SetBannerPosition(adUnitKey, (int)position);
        }

        public bool TryGetBannerScreenRect(string adUnitKey, out UnityEngine.Rect rect)
        {
            rect = default;
            // Shim returns the rect already in Unity screen px, bottom-left origin
            // (points→px + y-flip done native-side). Returns 0 if not laid out /
            // hidden / unknown adUnitKey.
            if (DaroUnity_GetBannerScreenRect(adUnitKey,
                    out float x, out float y, out float w, out float h) == 0)
                return false;
            rect = new UnityEngine.Rect(x, y, w, h);
            return true;
        }

        // ── IDaroIosEventSink (forwards to event slots / pending Tcs) ────

        void IDaroIosEventSink.Loaded(string adUnitKey, DaroAdInfo info) =>
            _onAdLoaded?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.FailedToLoad(string adUnitKey, DaroAdLoadError error) =>
            _onAdFailedToLoad?.Invoke(adUnitKey, error);
        void IDaroIosEventSink.Shown(string adUnitKey, DaroAdInfo info) =>
            _onAdShown?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.FailedToShow(string adUnitKey, DaroAdDisplayError error) =>
            _onAdFailedToShow?.Invoke(adUnitKey, error);
        void IDaroIosEventSink.Clicked(string adUnitKey, DaroAdInfo info) =>
            _onAdClicked?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.Impression(string adUnitKey, DaroAdInfo info) =>
            _onAdImpression?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.Hidden(string adUnitKey, DaroAdInfo info) =>
            _onAdHidden?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.Dismissed(string adUnitKey, DaroAdInfo info) =>
            _onAdDismissed?.Invoke(adUnitKey, info);
        void IDaroIosEventSink.EarnedReward(string adUnitKey, DaroAdInfo info, DaroRewardItem reward) =>
            _onEarnedReward?.Invoke(adUnitKey, info, reward);
        void IDaroIosEventSink.RevenuePaid(string adUnitKey, DaroAdInfo info, DaroRevenueInfo revenue) =>
            _onAdRevenuePaid?.Invoke(adUnitKey, info, revenue);

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
        private static void OnNativeEvent(string adUnitKey, string eventJson)
        {
            var inst = _instance;
            if (inst == null) return;
            DaroIOSEventDispatcher.Dispatch(adUnitKey, eventJson, inst);
        }

        // ── extern C surface ───

        private const string DLL = "__Internal";

        [DllImport(DLL)] private static extern void DaroUnity_SetCallback(DaroUnityCallbackFn callback);

        [DllImport(DLL)] private static extern void DaroUnity_Initialize(
            int hasGdprConsent, string? gdprConsentString,
            int doNotSell,      string? ccpaConsentString,
            int isTaggedForCoppa, int logLevel);

        [DllImport(DLL)] private static extern void DaroUnity_SetUserId(string userId);
        [DllImport(DLL)] private static extern void DaroUnity_SetAppMuted(bool muted);
        [DllImport(DLL)] private static extern void DaroUnity_SetLogLevel(int level);

        // Runtime teardown cleanup.
        // Native side shipped in prior turn — see SDK/Plugins/iOS/DaroUnityBridge.mm.
        [DllImport(DLL)] private static extern void DaroUnity_DestroyAll();

        [DllImport(DLL)] private static extern void DaroUnity_CreateInterstitial(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_LoadInterstitial(string adUnitKey);
        [DllImport(DLL)] private static extern int  DaroUnity_IsInterstitialReady(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_ShowInterstitial(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyInterstitial(string adUnitKey);

        [DllImport(DLL)] private static extern void DaroUnity_CreateRewarded(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_LoadRewarded(string adUnitKey);
        [DllImport(DLL)] private static extern int  DaroUnity_IsRewardedReady(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_ShowRewarded(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyRewarded(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_SetRewardedCustomData(string adUnitKey, string customData);

        [DllImport(DLL)] private static extern void DaroUnity_CreateAppOpen(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_LoadAppOpen(string adUnitKey);
        [DllImport(DLL)] private static extern int  DaroUnity_IsAppOpenReady(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_ShowAppOpen(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyAppOpen(string adUnitKey);

        // ── Banner extern C surface ──────────────────────────────────────
        // Defined in SDK/Plugins/iOS/DaroUnityBannerAd.mm. No IsReady — banner
        // is an always-on overlay with no "ready" notion separate from load.
        [DllImport(DLL)] private static extern void DaroUnity_CreateBanner(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_LoadBanner(string adUnitKey, int sizeOrdinal);
        [DllImport(DLL)] private static extern void DaroUnity_ShowBanner(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_HideBanner(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyBanner(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_SetBannerPosition(string adUnitKey, int positionOrdinal);
        // Returns 1 + rect (Unity screen px, bottom-left origin) if the banner is
        // laid out; 0 otherwise. Out params untouched when 0.
        [DllImport(DLL)] private static extern int DaroUnity_GetBannerScreenRect(
            string adUnitKey, out float x, out float y, out float width, out float height);

        // ── Light Popup extern C surface ─────────────────────────────────
        // Defined in SDK/Plugins/iOS/DaroUnityLightPopup.mm.
        // 36 float = 9 colors × 4 channels (RGBA, [0,1] pre-divided).
        [DllImport(DLL)] private static extern void DaroUnity_CreateLightPopup(
            string adUnitKey,
            float bgR,        float bgG,        float bgB,        float bgA,
            float containerR, float containerG, float containerB, float containerA,
            float adMarkTextR,float adMarkTextG,float adMarkTextB,float adMarkTextA,
            float adMarkBgR,  float adMarkBgG,  float adMarkBgB,  float adMarkBgA,
            float closeBtnR,  float closeBtnG,  float closeBtnB,  float closeBtnA,
            float titleR,     float titleG,     float titleB,     float titleA,
            float bodyR,      float bodyG,      float bodyB,      float bodyA,
            float ctaBgR,     float ctaBgG,     float ctaBgB,     float ctaBgA,
            float ctaTextR,   float ctaTextG,   float ctaTextB,   float ctaTextA,
            string closeButtonText, int useNativeDefaults);
        [DllImport(DLL)] private static extern void DaroUnity_LoadLightPopup(string adUnitKey);
        [DllImport(DLL)] private static extern bool DaroUnity_IsLightPopupReady(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_ShowLightPopup(string adUnitKey);
        [DllImport(DLL)] private static extern void DaroUnity_DestroyLightPopup(string adUnitKey);

        // ── Light Popup (modal popup + auto-dismiss preset) ──────────────
        //

        // - 9 Color32 → 36 float (RGBA per channel, pre-divided to [0,1] via
        //   B(byte) helper); shim builds DaroObjCLightPopupConfiguration with
        //   [UIColor colorWithRed:green:blue:alpha:] (no further conversion).
        // - 9:9 UIColor mapping is true 1:1; CloseButtonColor maps to
        //   closeButtonTextColor because
        //   iOS lacks a separate icon-color slot.

        public void CreateLightPopup(string adUnitKey, DaroLightPopupAdOptions? o)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].CreateLightPopup adUnit='{adUnitKey}'");
            bool useNativeDefaults = o == null;
            o ??= new DaroLightPopupAdOptions();
            DaroUnity_CreateLightPopup(
                adUnitKey,
                B(o.BackgroundColor.r),            B(o.BackgroundColor.g),            B(o.BackgroundColor.b),            B(o.BackgroundColor.a),
                B(o.ContainerColor.r),             B(o.ContainerColor.g),             B(o.ContainerColor.b),             B(o.ContainerColor.a),
                B(o.AdMarkLabelTextColor.r),       B(o.AdMarkLabelTextColor.g),       B(o.AdMarkLabelTextColor.b),       B(o.AdMarkLabelTextColor.a),
                B(o.AdMarkLabelBackgroundColor.r), B(o.AdMarkLabelBackgroundColor.g), B(o.AdMarkLabelBackgroundColor.b), B(o.AdMarkLabelBackgroundColor.a),
                B(o.CloseButtonColor.r),           B(o.CloseButtonColor.g),           B(o.CloseButtonColor.b),           B(o.CloseButtonColor.a),
                B(o.TitleColor.r),                 B(o.TitleColor.g),                 B(o.TitleColor.b),                 B(o.TitleColor.a),
                B(o.BodyColor.r),                  B(o.BodyColor.g),                  B(o.BodyColor.b),                  B(o.BodyColor.a),
                B(o.CtaBackgroundColor.r),         B(o.CtaBackgroundColor.g),         B(o.CtaBackgroundColor.b),         B(o.CtaBackgroundColor.a),
                B(o.CtaTextColor.r),               B(o.CtaTextColor.g),               B(o.CtaTextColor.b),               B(o.CtaTextColor.a),
                o.CloseButtonText ?? "Close", useNativeDefaults ? 1 : 0);
        }

        public void LoadLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].LoadLightPopup adUnit='{adUnitKey}'");
            DaroUnity_LoadLightPopup(adUnitKey);
        }

        public bool IsLightPopupReady(string adUnitKey) => DaroUnity_IsLightPopupReady(adUnitKey);

        public void ShowLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].ShowLightPopup adUnit='{adUnitKey}'");
            DaroUnity_ShowLightPopup(adUnitKey);
        }

        public void DestroyLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[iOS].DestroyLightPopup adUnit='{adUnitKey}'");
            DaroUnity_DestroyLightPopup(adUnitKey);
        }

        // byte (0-255) → float (0.0-1.0) for UIColor CGFloat channels.
        // Pre-divided at C# call site so the ObjC shim does direct
        // [UIColor colorWithRed:...] without further conversion.
        private static float B(byte b) => b / 255f;

        // ── Native ad (instance-owned) ──────────────────────────────
        // Native uses a per-instance handle (vs adUnitKey-keyed dict for
        // other formats); same adUnitKey × N instances yields N independent
        // handles. Implementation: see DaroIOSNativeAdHandle (this directory)
        // + DaroUnityNativeAd.mm (peer to DaroUnityBannerAd.mm).
        public INativeAdHandle CreateNativeAdHandle(string adUnitKey, INativeAdEventSink sink)
        {
            DaroLog.Verbose("Native", $"Platform[iOS].CreateNativeAdHandle adUnit='{adUnitKey}'");
            return new DaroIOSNativeAdHandle(adUnitKey, sink);
        }
    }
}
#endif
