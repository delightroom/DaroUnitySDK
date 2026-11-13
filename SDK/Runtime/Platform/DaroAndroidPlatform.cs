#nullable enable
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Android implementation of <see cref="IDaroPlatform"/>. Sits on top of
    /// the Kotlin shim shipped as <c>SDK/Plugins/Android/daro-android-wrapper.aar</c>
    /// (source lives under <c>SDK/Plugins/Android-src~</c>), which wraps native
    /// <c>so.daro:daro-m:1.3.12</c> (daro-core resolves transitively via the
    /// daro-m POM).
    /// </summary>
    /// <remarks>
    /// <para>Threading: the Kotlin shim fires raw callbacks on whatever thread
    /// native delivers (load / show / lifecycle worker threads). Every proxy
    /// method here marshals into <see cref="MainThreadDispatcher"/> before
    /// invoking the C# event slot, so consumer handlers always run on the
    /// Unity main thread.</para>
    ///
    /// <para>Stripping defense: <c>SDK/Runtime/link.xml</c> preserves the
    /// entire <c>Daro.Internal</c> namespace — both this class and its inner
    /// <see cref="DaroAdCallbackProxy"/> / <see cref="DaroRewardedCallbackProxy"/>
    /// are covered without per-method <c>[Preserve]</c> attributes.</para>
    /// </remarks>
    internal sealed class DaroAndroidPlatform : IDaroPlatform
    {
        // ── Bridge singleton (init + global settings only) ───────────────────
        private readonly AndroidJavaClass _bridge =
            new AndroidJavaClass("so.daro.unity.DaroUnityBridge");

        // ── Context refs (captured once at InitializeAsync) ──────────────────
        // Stable across the process lifetime — Unity's UnityPlayerActivity
        // declares android:configChanges="...|orientation|screenSize|..." so
        // the Activity is never recreated on rotation.
        private AndroidJavaObject? _activity;
        private AndroidJavaObject? _application;
        private DaroInitCallbackProxy? _initProxy;

        // ── Per-unit Kotlin ad instance refs ─────────────────────────────────
        // Owned for instance-method calls (load / show / isReady / destroy).
        private readonly Dictionary<string, AndroidJavaObject> _adObjects = new();
        private readonly Dictionary<string, int> _bannerGenerations = new();

        // ── Per-unit AndroidJavaProxy strong-refs (JNI GC anchor) ────────────
        // Must outlive every native callback. The Kotlin shim holds the Java
        // reference; this dictionary keeps the C# side alive for as long as
        // the platform lives.
        private readonly Dictionary<string, AndroidJavaProxy> _proxies = new();

        // ── Event slots (set by DaroSdk.WirePlatformEvents) ─────
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
        private Action<string, DaroAdInfo>?                 _onAdPreloaded;
        private Action<string, DaroAdLoadError>?            _onAdFailedToPreload;

        // ── Layer 2 platform-level disposed guard ────────────────────────────
        // Distinct from the Kotlin Layer 1 (`@Volatile destroyed` per ad class).
        // Set in Dispose(); checked in every proxy method BEFORE Enqueue,
        // ensuring a callback that beat Layer 1 still drops before delivering
        // a stale event to consumers.
        private volatile bool _disposed;

        // ── IDaroPlatform event setters ──────────────────────────────────────
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdLoaded       { set => _onAdLoaded       = value; }
        Action<string, DaroAdLoadError>?            IDaroPlatform.OnAdFailedToLoad { set => _onAdFailedToLoad = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdShown        { set => _onAdShown        = value; }
        Action<string, DaroAdDisplayError>?         IDaroPlatform.OnAdFailedToShow { set => _onAdFailedToShow = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdClicked      { set => _onAdClicked      = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdImpression   { set => _onAdImpression   = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdDismissed    { set => _onAdDismissed    = value; }
        Action<string, DaroAdInfo, DaroRewardItem>? IDaroPlatform.OnEarnedReward   { set => _onEarnedReward   = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdHidden       { set => _onAdHidden       = value; }
        Action<string, DaroAdInfo, DaroRevenueInfo>? IDaroPlatform.OnAdRevenuePaid { set => _onAdRevenuePaid  = value; }
        Action<string, DaroAdInfo>?                 IDaroPlatform.OnAdPreloaded       { set => _onAdPreloaded       = value; }
        Action<string, DaroAdLoadError>?            IDaroPlatform.OnAdFailedToPreload { set => _onAdFailedToPreload = value; }

        // ── Banner ad operations ─────────────────────────────────────────────

        public void CreateBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Android].CreateBanner adUnit='{adUnitKey}'");
            CreateAdObject(adUnitKey, DaroAdFormat.Banner,
                "so.daro.unity.DaroUnityBannerAd");
        }

        public void PreloadBanner(string adUnitKey, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform.PreloadBanner adUnit='{adUnitKey}' size={size} adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            // Kotlin shim: preload(activity, bannerSizeOrdinal, callback). generation 을 올리지
            // 않는다 — 표시 중인 배너의 콜백을 무효화하면 안 되고, preload 결과는 프록시
            // 동일성으로만 거른다. 컨트롤러는 뷰와 같은 Activity ( Unity 의 단일 Activity ) 로 만든다.
            adObj.Call("preload", _activity, (int)size, proxy);
        }

        public void LoadBanner(string adUnitKey, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform.LoadBanner adUnit='{adUnitKey}' size={size} adObj={_adObjects.ContainsKey(adUnitKey)} proxy={_proxies.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            var generation = NextBannerGeneration(adUnitKey);
            // Kotlin shim: load(activity, bannerSizeOrdinal, generation, callback)
            adObj.Call("load", _activity, (int)size, generation, proxy);
        }

        public void ShowBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform.ShowBanner adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            // Kotlin shim's show() takes no args — banner is always-on overlay,
            // doesn't need Activity at show time (already captured at load).
            adObj.Call("show");
        }

        public void HideBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform.HideBanner adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            adObj.Call("hide", CurrentBannerGeneration(adUnitKey), proxy);
        }

        public void DestroyBanner(string adUnitKey)
        {
            DaroLog.Verbose("Banner", $"Platform[Android].DestroyBanner adUnit='{adUnitKey}'");
            DestroyAdObject(adUnitKey);
        }

        public void SetBannerPosition(string adUnitKey, DaroBannerPosition position)
        {
            DaroLog.Verbose("Banner", $"Platform.SetBannerPosition adUnit='{adUnitKey}' position={position} adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            adObj.Call("setPosition", BannerPositionToGravity(position));
        }

        public bool TryGetBannerScreenRect(string adUnitKey, out Rect rect)
        {
            rect = default;
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return false;

            int[]? r;
            try
            {
                // Kotlin shim: getScreenRectPx() -> IntArray? {left, top, width, height}
                // in display px (top-left origin), or null if not laid out / hidden.
                r = adObj.Call<int[]>("getScreenRectPx");
            }
            catch (Exception e)
            {
                DaroLog.Warn("Banner",
                    $"Platform[Android].getScreenRectPx threw adUnit='{adUnitKey}': {e.Message}");
                return false;
            }
            if (r == null || r.Length < 4 || r[2] <= 0 || r[3] <= 0) return false;

            // Android top-left origin → Unity screen px, bottom-left origin
            // (Screen.safeArea convention).
            float w = r[2];
            float h = r[3];
            float x = r[0];
            float yBottomLeft = Screen.height - (r[1] + h);
            rect = new Rect(x, yBottomLeft, w, h);
            return true;
        }

        // android.view.Gravity bitmask values are stable across Android versions;
        // listed inline rather than via AndroidJavaClass lookup to avoid extra
        // JNI traffic on every SetPosition call.
        private static int BannerPositionToGravity(DaroBannerPosition position)
        {
            const int TOP    = 0x30;
            const int BOTTOM = 0x50;
            const int LEFT   = 0x03;
            const int RIGHT  = 0x05;
            const int CENTER_HORIZONTAL = 0x01;

            return position switch
            {
                DaroBannerPosition.TopLeft      => TOP    | LEFT,
                DaroBannerPosition.TopCenter    => TOP    | CENTER_HORIZONTAL,
                DaroBannerPosition.TopRight     => TOP    | RIGHT,
                DaroBannerPosition.BottomLeft   => BOTTOM | LEFT,
                DaroBannerPosition.BottomCenter => BOTTOM | CENTER_HORIZONTAL,
                DaroBannerPosition.BottomRight  => BOTTOM | RIGHT,
                _                               => BOTTOM | CENTER_HORIZONTAL,
            };
        }

        // ── InitializeAsync ──────────────────────────────────────────────────

        public Task InitializeAsync(DaroSdkInitParams p)
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].InitializeAsync logLevel={p.LogLevel} hasGdprConsent={p.HasGdprConsent} doNotSell={p.DoNotSell}");
            using var unityPlayer = new AndroidJavaClass("com.unity3d.player.UnityPlayer");
            _activity    = unityPlayer.GetStatic<AndroidJavaObject>("currentActivity");
            _application = _activity.Call<AndroidJavaObject>("getApplication");

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _initProxy = new DaroInitCallbackProxy(tcs);
            _bridge.CallStatic("initialize",
                _application,
                DaroAndroidEncoding.NullableBoolToTristate(p.HasGdprConsent),
                p.GdprConsentString ?? "",
                DaroAndroidEncoding.NullableBoolToTristate(p.DoNotSell),
                p.CcpaConsentString ?? "",
                DaroAndroidEncoding.NullableBoolToTristate(p.IsTaggedForChildDirectedTreatment),
                DaroAndroidEncoding.LogLevelToDebugMode(p.LogLevel),
                string.Join("\n", p.TestDeviceAdvertisingIdentifiers ?? Array.Empty<string>()),
                _initProxy
            );

            return tcs.Task;
        }

        private sealed class DaroInitCallbackProxy : AndroidJavaProxy
        {
            private readonly TaskCompletionSource<bool> _tcs;

            public DaroInitCallbackProxy(TaskCompletionSource<bool> tcs)
                : base("so.daro.unity.IDaroInitCallback") => _tcs = tcs;

            // The facade dispatches Task completion and SDK events to Unity's main thread.
            public void onInitialized() => _tcs.TrySetResult(true);

            public void onInitializationFailed(string message) =>
                _tcs.TrySetException(new DaroSdkInitException(
                    message, (int)DaroAdLoadErrorCode.InitializationFailed));
        }

        // ── Runtime settings ─────────────────────────────────────────────────

        public void SetUserId(string userId)
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].SetUserId userId='{userId}'");
            _bridge.CallStatic("setUserId", _application, userId);
        }

        public void SetAppMuted(bool muted)
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].SetAppMuted muted={muted}");
            _bridge.CallStatic("setAppMuted", muted);
        }

        // Native init still uses SDKConfig.setDebugMode (boolean Daro core SDK
        // signature). The Kotlin shim's own `daroLogLevel: Int` tracks our
        // shim's gate state separately and is updated here on every runtime
        // LogLevel change so the two layers stay in lockstep.
        // DaroUnityBridge.setLogLevel propagates the value to the Kotlin gate.
        public void SetLogLevel(DaroLogLevel level)
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].SetLogLevel level={level} (raw={(int)level})");
            _bridge.CallStatic("setLogLevel", (int)level);
        }

        // ── Instance lifecycle ───────────────────────────────────────────────

        public void CreateInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].CreateInterstitial adUnit='{adUnitKey}'");
            CreateAdObject(adUnitKey, DaroAdFormat.Interstitial,
                "so.daro.unity.DaroUnityInterstitialAd");
        }

        public void CreateRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].CreateRewarded adUnit='{adUnitKey}'");
            CreateAdObject(adUnitKey, DaroAdFormat.Rewarded,
                "so.daro.unity.DaroUnityRewardedAd");
        }

        public void CreateAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].CreateAppOpen adUnit='{adUnitKey}'");
            CreateAdObject(adUnitKey, DaroAdFormat.AppOpen,
                "so.daro.unity.DaroUnityAppOpenAd");
        }

        private void CreateAdObject(
            string adUnitKey, DaroAdFormat format, string kotlinClass)
        {
            // Tear down any stale object for this adUnitKey before creating fresh.
            DestroyAdObject(adUnitKey);

            AndroidJavaProxy proxy = format switch
            {
                DaroAdFormat.Rewarded => new DaroRewardedCallbackProxy(adUnitKey, this),
                DaroAdFormat.Banner   => new DaroBannerCallbackProxy(adUnitKey, this),
                _                     => new DaroAdCallbackProxy(adUnitKey, format, this),
            };

            // Kotlin ctor signature is `(adUnitKey: String)` for all three classes.
            var adObj = new AndroidJavaObject(kotlinClass, adUnitKey);

            _proxies[adUnitKey]   = proxy;
            _adObjects[adUnitKey] = adObj;
        }

        // ── Ad operations ────────────────────────────────────────────────────

        public void LoadInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].LoadInterstitial adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)} proxy={_proxies.ContainsKey(adUnitKey)}");
            LoadAd(adUnitKey);
        }

        public void LoadRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].LoadRewarded adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)} proxy={_proxies.ContainsKey(adUnitKey)}");
            LoadAd(adUnitKey);
        }

        private void LoadAd(string adUnitKey)
        {
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            adObj.Call("load", _activity, proxy);
        }

        public void LoadAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].LoadAppOpen adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)} proxy={_proxies.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            // AppOpen native API takes Application context for load (Builder
            // requirement) and Activity for show.
            adObj.Call("load", _application, proxy);
        }

        // IsReady* family is polled frequently (often every frame from consumer
        // code); we skip dev-track tracing here to avoid console spam.
        public bool IsInterstitialReady(string adUnitKey) => IsAdReady(adUnitKey);
        public bool IsRewardedReady(string adUnitKey)     => IsAdReady(adUnitKey);
        public bool IsAppOpenReady(string adUnitKey)      => IsAdReady(adUnitKey);

        private bool IsAdReady(string adUnitKey)
        {
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return false;
            return adObj.Call<bool>("isReady");
        }

        public void ShowInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].ShowInterstitial adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            ShowAd(adUnitKey);
        }

        public void ShowRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].ShowRewarded adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            ShowAd(adUnitKey);
        }

        public void ShowAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].ShowAppOpen adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            ShowAd(adUnitKey);
        }

        private void ShowAd(string adUnitKey)
        {
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            adObj.Call("show", _activity);
        }

        public void SetRewardedCustomData(string adUnitKey, string customData)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].SetRewardedCustomData adUnit='{adUnitKey}' len={customData.Length} adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            adObj.Call("setCustomData", customData);
        }

        // ── Destroy ──────────────────────────────────────────────────────────

        public void DestroyInterstitial(string adUnitKey)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].DestroyInterstitial adUnit='{adUnitKey}'");
            DestroyAdObject(adUnitKey);
        }

        public void DestroyRewarded(string adUnitKey)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].DestroyRewarded adUnit='{adUnitKey}'");
            DestroyAdObject(adUnitKey);
        }

        public void DestroyAppOpen(string adUnitKey)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].DestroyAppOpen adUnit='{adUnitKey}'");
            DestroyAdObject(adUnitKey);
        }

        /// <summary>
        /// Native-first dispose ordering: Kotlin sets its
        /// <c>@Volatile destroyed</c> flag inside <c>destroy()</c> before
        /// calling <c>ad.destroy()</c>, so any further worker-thread listener
        /// invocation skips its callback before reaching the C# proxy. Only
        /// then is the JNI proxy ref dropped. <c>AndroidJavaProxy</c> is not
        /// <c>IDisposable</c>; the underlying JNI ref is released when the
        /// proxy is GC'd, so we just drop our last reference here.
        /// </summary>
        private void DestroyAdObject(string adUnitKey)
        {
            InvalidateBannerGenerationIfNeeded(adUnitKey);
            if (_adObjects.TryGetValue(adUnitKey, out var adObj))
            {
                adObj.Call("destroy");   // Kotlin: @Volatile destroyed = true (Layer 1)
                adObj.Dispose();
                _adObjects.Remove(adUnitKey);
            }
            _proxies.Remove(adUnitKey);
        }

        private int NextBannerGeneration(string adUnitKey)
        {
            _bannerGenerations.TryGetValue(adUnitKey, out var current);
            var next = current + 1;
            _bannerGenerations[adUnitKey] = next;
            return next;
        }

        private int CurrentBannerGeneration(string adUnitKey)
        {
            _bannerGenerations.TryGetValue(adUnitKey, out var current);
            return current;
        }

        private void InvalidateBannerGenerationIfNeeded(string adUnitKey)
        {
            if (_proxies.TryGetValue(adUnitKey, out var proxy) && proxy is DaroBannerCallbackProxy)
            {
                NextBannerGeneration(adUnitKey);
            }
        }

        // Preload 결과용. Load 의 generation 은 보지 않는다 — 로딩 중에 Load 가 광고를 가져간
        // 경우에도 ( 핸드오프 ) OnAdPreloaded 가 나가야 한다. 객체 교체·해제는 프록시가 바뀌어 걸린다.
        private bool IsCurrentBannerProxy(string adUnitKey, DaroBannerCallbackProxy proxy)
        {
            return !_disposed
                && _proxies.TryGetValue(adUnitKey, out var currentProxy)
                && ReferenceEquals(currentProxy, proxy);
        }

        private bool IsCurrentBannerCallback(
            string adUnitKey,
            int generation,
            DaroBannerCallbackProxy proxy)
        {
            return !_disposed
                && _bannerGenerations.TryGetValue(adUnitKey, out var current)
                && current == generation
                && _proxies.TryGetValue(adUnitKey, out var currentProxy)
                && ReferenceEquals(currentProxy, proxy);
        }

        // ── Full platform teardown ───────────────────────────────────────────

        /// <summary>
        /// IDaroPlatform-level teardown trigger. Called by
        /// <c>DaroSdk.MarkShuttingDown</c> on app-quit / Unity-runtime-teardown.
        /// Best-effort: never throws (DaroLog.Exception isolates). Idempotent
        /// via the <c>_disposed</c> gate inside <see cref="Dispose"/>.
        /// </summary>
        /// <remarks>
        /// <see cref="Dispose"/> tears down tracked instances and clears their
        /// dictionaries. Native ads use separate handles, so a second sweep
        /// releases them through the Kotlin bridge.
        /// </remarks>
        public void DestroyAll()
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].DestroyAll _adObjects={_adObjects.Count} _proxies={_proxies.Count}");
            try
            {
                Dispose();
            }
            catch (Exception e)
            {
                DaroLog.Exception("Sdk", e);
            }

            // Native ad per-instance handle pattern bypasses
            // _adObjects, so Dispose() above doesn't reach live native ads.
            // Kotlin DaroUnityBridge.destroyAll iterates a static registry
            // of live DaroUnityNativeAd instances. Order: per-instance dispose (above) first, then
            // native ad sweep — mirrors iOS helper-dispatcher pattern.
            //
            // Note: `_bridge` was disposed by Dispose() above. We create a
            // fresh AndroidJavaClass handle for the sweep call — bridge is
            // a stateless static object in Kotlin, so a new JNI handle
            // resolves to the same backing class.
            try
            {
                using var bridge = new AndroidJavaClass("so.daro.unity.DaroUnityBridge");
                bridge.CallStatic("destroyAll");
            }
            catch (Exception e)
            {
                DaroLog.Exception("Sdk", e);
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;  // Layer 2 armed — all proxy callbacks short-circuit.

            // Per-object try/catch so a single ad's destroy throw (e.g.
            // AndroidJavaException bubbling up from Kotlin shim, or a
            // bridge-call mid-teardown) doesn't halt the rest of the loop —
            // without it, partial-cleanup state leaks views / dict entries /
            // bridge handles across the remaining ads.
            foreach (var kv in _adObjects)
            {
                try
                {
                    kv.Value.Call("destroy");
                    kv.Value.Dispose();
                }
                catch (Exception e)
                {
                    DaroLog.Exception("Sdk", e);
                }
            }
            _adObjects.Clear();

            // AndroidJavaProxy is not IDisposable — JNI ref released on GC.
            _proxies.Clear();

            // Same isolation discipline for the singleton JNI handles —
            // one misbehaving Dispose() must not skip the rest.
            try { _activity?.Dispose(); }    catch (Exception e) { DaroLog.Exception("Sdk", e); }
            try { _application?.Dispose(); } catch (Exception e) { DaroLog.Exception("Sdk", e); }
            try { _bridge.Dispose(); }       catch (Exception e) { DaroLog.Exception("Sdk", e); }
        }

        // ─────────────────────────────────────────────────────────────────────
        // Inner proxy classes
        //
        // Each implements the matching Kotlin callback interface via
        // AndroidJavaProxy. Method names MUST match the Kotlin interface
        // declarations — JNI resolves by string at runtime, mismatches fail
        // silently (no compile-time error).
        // ─────────────────────────────────────────────────────────────────────

        private class DaroAdCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitKey;
            private readonly DaroAdFormat        _format;
            private readonly DaroAndroidPlatform _platform;

            internal DaroAdCallbackProxy(
                string adUnitKey, DaroAdFormat format, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroAdCallback")
            {
                _adUnitKey = adUnitKey;
                _format   = format;
                _platform = platform;
            }

            // latencyMs sourced from Kotlin's adInfo.latency / err.latency (millis).
            // Forwarded as-is to C# DaroAdInfo.Latency to match Daro's
            // cross-platform millis contract.
            // 귀속은 이벤트마다 Kotlin 이 실어 온다. 여기서 기억하지 않는다 — 기억하면 다음 광고를
            // 미리 로드하는 동안 지금 광고의 콜백이 다음 광고의 귀속으로 찍힌다.
            private DaroAdInfo MakeInfo(string adUnitKey, int? latencyMs, string? mediationPlatform, string? adNetwork) =>
                new DaroAdInfo(_format, adUnitKey, latencyMs, mediationPlatform, adNetwork);

            public void onAdLoaded(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitKey, info));
            }

            public void onAdFailedToLoad(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                // rawCode = 네이티브가 준 값 그대로. shim 이 `DaroError.errorCode`
                // 를 보내므로 이것은 **Daro 코드**다 — `DaroAdLoadError.RawCode`
                // 의 계약("DaroSDK DaroError.Code.rawValue")과 iOS 경로에 맞는다.
                //
                // `MaxError.code` 를 그대로 전달하면 iOS 와 오류 코드 계약이
                // 달라지므로 shim 이 제공하는 Daro 오류 코드를 사용한다.
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitKey, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitKey, err));
            }

            public void onAdShown(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitKey, info));
            }

            // errorCode is always -1 from Kotlin (DaroAdDisplayFailError has
            // no int code). Stored as rawCode for diagnostic continuity even
            // though it conveys no extra info on Android.
            public void onAdFailedToShow(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitKey, err));
            }

            public void onAdClicked(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitKey, info));
            }

            public void onAdImpression(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitKey, info));
            }

            public void onAdDismissed(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitKey, info));
            }

            // No latency on the revenue path — daro-m's paid event tuple is
            // (valueMicros, currencyCode, precisionType) only.
            public void onAdRevenuePaid(
                string adUnitKey, long valueMicros, string currencyCode, int precisionType,
                string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info    = MakeInfo(adUnitKey, null, mediationPlatform, adNetwork);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitKey, info, revenue));
            }
        }

        // Standalone proxy for Rewarded — extends AndroidJavaProxy directly
        // with IDaroRewardedCallback (all 8 methods). NOT subclassing
        // DaroAdCallbackProxy because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and lose `onEarnedReward` JNI dispatch.
        private sealed class DaroRewardedCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitKey;
            private readonly DaroAndroidPlatform _platform;

            internal DaroRewardedCallbackProxy(string adUnitKey, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroRewardedCallback")
            {
                _adUnitKey = adUnitKey;
                _platform = platform;
            }

            // 귀속은 이벤트마다 Kotlin 이 실어 온다. 여기서 기억하지 않는다 — 기억하면 다음 광고를
            // 미리 로드하는 동안 지금 광고의 콜백이 다음 광고의 귀속으로 찍힌다.
            private DaroAdInfo MakeInfo(string adUnitKey, int? latencyMs, string? mediationPlatform, string? adNetwork) =>
                new DaroAdInfo(DaroAdFormat.Rewarded, adUnitKey, latencyMs, mediationPlatform, adNetwork);

            public void onAdLoaded(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitKey, info));
            }

            public void onAdFailedToLoad(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitKey, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitKey, err));
            }

            public void onAdShown(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitKey, info));
            }

            public void onAdFailedToShow(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitKey, err));
            }

            public void onAdClicked(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitKey, info));
            }

            public void onAdImpression(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitKey, info));
            }

            public void onAdDismissed(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitKey, info));
            }

            public void onEarnedReward(
                string adUnitKey, string rewardType, int rewardAmount, int latencyMs,
                string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info   = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                var reward = new DaroRewardItem(rewardAmount, rewardType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onEarnedReward?.Invoke(adUnitKey, info, reward));
            }

            public void onAdRevenuePaid(
                string adUnitKey, long valueMicros, string currencyCode, int precisionType,
                string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info    = MakeInfo(adUnitKey, null, mediationPlatform, adNetwork);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitKey, info, revenue));
            }
        }

        // Banner-specific proxy. Standalone (not subclassing
        // DaroAdCallbackProxy) because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and break JNI dispatch.
        private sealed class DaroBannerCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitKey;
            private readonly DaroAndroidPlatform _platform;

            internal DaroBannerCallbackProxy(string adUnitKey, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroBannerCallback")
            {
                _adUnitKey = adUnitKey;
                _platform = platform;
            }

            // 귀속은 이벤트마다 Kotlin 이 실어 온다. 여기서 기억하지 않는다 — 기억하면 다음 광고를
            // 미리 로드하는 동안 지금 광고의 콜백이 다음 광고의 귀속으로 찍힌다.
            // 배너의 generation 은 C# LoadBanner/destroy 만 올리고 네이티브 auto-refresh 는 안 올린다 —
            // generation 별 보관도 결국 유닛 단위 필드와 같았다. 이벤트별 귀속이면 키가 필요 없다.
            private DaroAdInfo MakeInfo(string adUnitKey, int? latencyMs, string? mediationPlatform, string? adNetwork) =>
                new DaroAdInfo(DaroAdFormat.Banner, adUnitKey, latencyMs, mediationPlatform, adNetwork);

            private bool IsCurrent(string adUnitKey, int generation) =>
                _platform.IsCurrentBannerCallback(adUnitKey, generation, this);

            private void EnqueueIfCurrent(string adUnitKey, int generation, Action deliver)
            {
                // Native callbacks can arrive on worker threads. Keep all
                // Dictionary-backed generation/proxy checks on Unity main.
                if (_platform._disposed) return;
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (!IsCurrent(adUnitKey, generation)) return;
                    deliver();
                });
            }

            public void onAdLoaded(string adUnitKey, int generation, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdLoaded?.Invoke(adUnitKey, info));
            }

            public void onAdFailedToLoad(
                string adUnitKey, int generation, int errorCode, string errorMessage, int latencyMs)
            {
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitKey, errorCode);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdFailedToLoad?.Invoke(adUnitKey, err));
            }

            public void onAdPreloaded(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (!_platform.IsCurrentBannerProxy(adUnitKey, this)) return;
                    _platform._onAdPreloaded?.Invoke(adUnitKey, info);
                });
            }

            public void onAdFailedToPreload(string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitKey, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (!_platform.IsCurrentBannerProxy(adUnitKey, this)) return;
                    _platform._onAdFailedToPreload?.Invoke(adUnitKey, err);
                });
            }

            public void onAdImpression(string adUnitKey, int generation, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdImpression?.Invoke(adUnitKey, info));
            }

            public void onAdClicked(string adUnitKey, int generation, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdClicked?.Invoke(adUnitKey, info));
            }

            public void onAdHidden(string adUnitKey, int generation, string? mediationPlatform, string? adNetwork)
            {
                var info = MakeInfo(adUnitKey, null, mediationPlatform, adNetwork);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdHidden?.Invoke(adUnitKey, info));
            }

            public void onAdRevenuePaid(
                string adUnitKey, int generation, long valueMicros, string currencyCode, int precisionType,
                string? mediationPlatform, string? adNetwork)
            {
                var info    = MakeInfo(adUnitKey, null, mediationPlatform, adNetwork);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                EnqueueIfCurrent(adUnitKey, generation,
                    () => _platform._onAdRevenuePaid?.Invoke(adUnitKey, info, revenue));
            }
        }

        // Light Popup 7-method proxy. Standalone (not subclassing
        // DaroAdCallbackProxy) because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and break JNI dispatch for IDaroLightPopupCallback
        // methods. Same interface-registration reason as DaroRewardedCallbackProxy and
        // DaroBannerCallbackProxy.
        private sealed class DaroLightPopupCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitKey;
            private readonly DaroAndroidPlatform _platform;

            internal DaroLightPopupCallbackProxy(string adUnitKey, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroLightPopupCallback")
            {
                _adUnitKey = adUnitKey;
                _platform = platform;
            }

            // 귀속은 이벤트마다 Kotlin 이 실어 온다. 여기서 기억하지 않는다 — 기억하면 다음 광고를
            // 미리 로드하는 동안 지금 광고의 콜백이 다음 광고의 귀속으로 찍힌다.
            private DaroAdInfo MakeInfo(string adUnitKey, int? latencyMs, string? mediationPlatform, string? adNetwork) =>
                new DaroAdInfo(DaroAdFormat.LightPopup, adUnitKey, latencyMs, mediationPlatform, adNetwork);

            public void onAdLoaded(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitKey, info));
            }

            public void onAdFailedToLoad(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitKey, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitKey, err));
            }

            public void onAdShown(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitKey, info));
            }

            // errorCode is always -1 from Kotlin (DaroAdDisplayFailError has
            // no int code). Stored as rawCode for diagnostic continuity even
            // though it conveys no extra info on Android.
            public void onAdFailedToShow(
                string adUnitKey, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitKey, err));
            }

            public void onAdDismissed(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitKey, info));
            }

            public void onAdClicked(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitKey, info));
            }

            public void onAdImpression(string adUnitKey, int latencyMs, string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitKey, latencyMs, mediationPlatform, adNetwork);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitKey, info));
            }

            public void onAdRevenuePaid(
                string adUnitKey, long valueMicros, string currencyCode, int precisionType,
                string? mediationPlatform, string? adNetwork)
            {
                if (_platform._disposed) return;
                var info    = MakeInfo(adUnitKey, null, mediationPlatform, adNetwork);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitKey, info, revenue));
            }
        }

        // ── Light Popup ad operations ────────────────────────────────────
        // Cannot use the generic CreateAdObject helper — Kotlin ctor takes
        // 36 params beyond adUnitKey (9×4 ARGB ints + closeButtonText),
        // and the proxy needs interface name IDaroLightPopupCallback rather than
        // IDaroAdCallback.
        public void CreateLightPopup(string adUnitKey, DaroLightPopupAdOptions? options)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].CreateLightPopup adUnit='{adUnitKey}'");
            // Native-first dispose ordering for any stale instance.
            DestroyAdObject(adUnitKey);

            var proxy = new DaroLightPopupCallbackProxy(adUnitKey, this);
            bool useNativeDefaults = options == null;
            options ??= new DaroLightPopupAdOptions();

            // JNI ctor: adUnitKey + 9 colors × 4 channels (A,R,G,B order
            // per field) + closeButtonText + useNativeDefaults. Color32 byte
            // (0–255 unsigned) cast to int for JNI — no sign-extension risk because
            // C# `byte` is unsigned, so `(int)(byte)0xB2 == 178`, never negative.
            var adObj = new AndroidJavaObject(
                "so.daro.unity.DaroUnityLightPopupAd",
                adUnitKey,
                (int)options.BackgroundColor.a,            (int)options.BackgroundColor.r,            (int)options.BackgroundColor.g,            (int)options.BackgroundColor.b,
                (int)options.ContainerColor.a,             (int)options.ContainerColor.r,             (int)options.ContainerColor.g,             (int)options.ContainerColor.b,
                (int)options.AdMarkLabelTextColor.a,       (int)options.AdMarkLabelTextColor.r,       (int)options.AdMarkLabelTextColor.g,       (int)options.AdMarkLabelTextColor.b,
                (int)options.AdMarkLabelBackgroundColor.a, (int)options.AdMarkLabelBackgroundColor.r, (int)options.AdMarkLabelBackgroundColor.g, (int)options.AdMarkLabelBackgroundColor.b,
                (int)options.TitleColor.a,                 (int)options.TitleColor.r,                 (int)options.TitleColor.g,                 (int)options.TitleColor.b,
                (int)options.BodyColor.a,                  (int)options.BodyColor.r,                  (int)options.BodyColor.g,                  (int)options.BodyColor.b,
                (int)options.CtaBackgroundColor.a,         (int)options.CtaBackgroundColor.r,         (int)options.CtaBackgroundColor.g,         (int)options.CtaBackgroundColor.b,
                (int)options.CtaTextColor.a,               (int)options.CtaTextColor.r,               (int)options.CtaTextColor.g,               (int)options.CtaTextColor.b,
                (int)options.CloseButtonColor.a,           (int)options.CloseButtonColor.r,           (int)options.CloseButtonColor.g,           (int)options.CloseButtonColor.b,
                options.CloseButtonText ?? "Close",
                useNativeDefaults
            );

            _proxies[adUnitKey]   = proxy;
            _adObjects[adUnitKey] = adObj;
        }

        public void LoadLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].LoadLightPopup adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)} proxy={_proxies.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitKey, out var proxy)) return;
            adObj.Call("load", _activity, proxy);
        }

        public bool IsLightPopupReady(string adUnitKey)
        {
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return false;
            return adObj.Call<bool>("isReady");
        }

        // DaroLightPopupAdImpl.show(activity: Activity) strictly requires Activity
        // (not just Context) for the underlying Dialog(activity) construction.
        public void ShowLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].ShowLightPopup adUnit='{adUnitKey}' adObj={_adObjects.ContainsKey(adUnitKey)}");
            if (!_adObjects.TryGetValue(adUnitKey, out var adObj)) return;
            adObj.Call("show", _activity);
        }

        public void DestroyLightPopup(string adUnitKey)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].DestroyLightPopup adUnit='{adUnitKey}'");
            DestroyAdObject(adUnitKey);
        }

        // ── Native ad (instance-owned) ──────────────────────────────
        // Each DaroNativeAd gets its own DaroAndroidNativeAdHandle (per-instance
        // AndroidJavaObject + AndroidJavaProxy). Native ad does NOT use
        // _adObjects / _proxies dicts — multi-instance for the same adUnitKey
        // is supported.
        public INativeAdHandle CreateNativeAdHandle(string adUnitKey, INativeAdEventSink sink)
        {
            DaroLog.Verbose("Native", $"Platform[Android].CreateNativeAdHandle adUnit='{adUnitKey}'");
            return new DaroAndroidNativeAdHandle(adUnitKey, sink, _activity);
        }
    }
}

#endif
