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
    /// See sketch §1, §3.1.
    /// </summary>
    /// <remarks>
    /// <para>Threading: the Kotlin shim fires raw callbacks on whatever thread
    /// native delivers (load / show / lifecycle worker threads). Every proxy
    /// method here marshals into <see cref="MainThreadDispatcher"/> before
    /// invoking the C# event slot, so consumer handlers always run on the
    /// Unity main thread. Sketch CD-3.</para>
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
        // the Activity is never recreated on rotation. Sketch §3.3.
        private AndroidJavaObject? _activity;
        private AndroidJavaObject? _application;

        // ── Per-unit Kotlin ad instance refs ─────────────────────────────────
        // Owned for instance-method calls (load / show / isReady / destroy).
        private readonly Dictionary<string, AndroidJavaObject> _adObjects = new();
        private readonly Dictionary<string, int> _bannerGenerations = new();

        // ── Per-unit AndroidJavaProxy strong-refs (JNI GC anchor) ────────────
        // Must outlive every native callback. The Kotlin shim holds the Java
        // reference; this dictionary keeps the C# side alive for as long as
        // the platform lives. Sketch §3.2 (Proxy Ownership Design).
        private readonly Dictionary<string, AndroidJavaProxy> _proxies = new();

        // ── Event slots (set by DaroSdk.WirePlatformEvents, sketch CD-3) ─────
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

        // ── Layer 2 platform-level disposed guard ────────────────────────────
        // Distinct from the Kotlin Layer 1 (`@Volatile destroyed` per ad class).
        // Set in Dispose(); checked in every proxy method BEFORE Enqueue,
        // ensuring a callback that beat Layer 1 still drops before delivering
        // a stale event to consumers. Sketch §4.2.
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

        // ── Banner ad operations ─────────────────────────────────────────────

        public void CreateBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform[Android].CreateBanner adUnit='{adUnitId}'");
            CreateAdObject(adUnitId, DaroAdFormat.Banner,
                "so.daro.unity.DaroUnityBannerAd");
        }

        public void LoadBanner(string adUnitId, DaroBannerSize size)
        {
            DaroLog.Verbose("Banner", $"Platform.LoadBanner adUnit='{adUnitId}' size={size} adObj={_adObjects.ContainsKey(adUnitId)} proxy={_proxies.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitId, out var proxy)) return;
            var generation = NextBannerGeneration(adUnitId);
            // Kotlin shim: load(activity, bannerSizeOrdinal, generation, callback)
            adObj.Call("load", _activity, (int)size, generation, proxy);
        }

        public void ShowBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform.ShowBanner adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            // Kotlin shim's show() takes no args — banner is always-on overlay,
            // doesn't need Activity at show time (already captured at load).
            adObj.Call("show");
        }

        public void HideBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform.HideBanner adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitId, out var proxy)) return;
            adObj.Call("hide", CurrentBannerGeneration(adUnitId), proxy);
        }

        public void DestroyBanner(string adUnitId)
        {
            DaroLog.Verbose("Banner", $"Platform[Android].DestroyBanner adUnit='{adUnitId}'");
            DestroyAdObject(adUnitId);
        }

        public void SetBannerPosition(string adUnitId, DaroBannerPosition position)
        {
            DaroLog.Verbose("Banner", $"Platform.SetBannerPosition adUnit='{adUnitId}' position={position} adObj={_adObjects.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            adObj.Call("setPosition", BannerPositionToGravity(position));
        }

        public bool TryGetBannerScreenRect(string adUnitId, out Rect rect)
        {
            rect = default;
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return false;

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
                    $"Platform[Android].getScreenRectPx threw adUnit='{adUnitId}': {e.Message}");
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
        // JNI traffic on every SetPosition call. Sketch §6.2.
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

            _bridge.CallStatic("initialize",
                _application,
                DaroAndroidEncoding.NullableBoolToTristate(p.HasGdprConsent),
                p.GdprConsentString ?? "",
                DaroAndroidEncoding.NullableBoolToTristate(p.DoNotSell),
                p.CcpaConsentString ?? "",
                DaroAndroidEncoding.NullableBoolToTristate(p.IsTaggedForChildDirectedTreatment),
                DaroAndroidEncoding.LogLevelToDebugMode(p.LogLevel),
                string.Join("\n", p.TestDeviceAdvertisingIdentifiers ?? Array.Empty<string>())
            );

            // Daro.init() is synchronous from the caller's perspective (sketch CD-4)
            // — native defers via ProcessLifecycleOwner internally but returns Unit.
            return Task.CompletedTask;
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
        // LogLevel change so the two layers stay in lockstep — see
        // sketch-log-module.md §A4 / §B3. Supersedes the legacy CD-13
        // "silently ignored" post-init behavior.
        // Requires SDK/Plugins/Android/DaroLog.kt + DaroUnityBridge.setLogLevel
        // (see android-foundation task). Single-PR ship recommended.
        public void SetLogLevel(DaroLogLevel level)
        {
            DaroLog.Verbose("Sdk", $"Platform[Android].SetLogLevel level={level} (raw={(int)level})");
            _bridge.CallStatic("setLogLevel", (int)level);
        }

        // ── Instance lifecycle ───────────────────────────────────────────────

        public void CreateInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].CreateInterstitial adUnit='{adUnitId}'");
            CreateAdObject(adUnitId, DaroAdFormat.Interstitial,
                "so.daro.unity.DaroUnityInterstitialAd");
        }

        public void CreateRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].CreateRewarded adUnit='{adUnitId}'");
            CreateAdObject(adUnitId, DaroAdFormat.Rewarded,
                "so.daro.unity.DaroUnityRewardedAd");
        }

        public void CreateAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].CreateAppOpen adUnit='{adUnitId}'");
            CreateAdObject(adUnitId, DaroAdFormat.AppOpen,
                "so.daro.unity.DaroUnityAppOpenAd");
        }

        private void CreateAdObject(
            string adUnitId, DaroAdFormat format, string kotlinClass)
        {
            // Tear down any stale object for this adUnitId before creating fresh.
            DestroyAdObject(adUnitId);

            AndroidJavaProxy proxy = format switch
            {
                DaroAdFormat.Rewarded => new DaroRewardedCallbackProxy(adUnitId, this),
                DaroAdFormat.Banner   => new DaroBannerCallbackProxy(adUnitId, this),
                _                     => new DaroAdCallbackProxy(adUnitId, format, this),
            };

            // Kotlin ctor signature is `(adUnitId: String)` for all three classes.
            var adObj = new AndroidJavaObject(kotlinClass, adUnitId);

            _proxies[adUnitId]   = proxy;
            _adObjects[adUnitId] = adObj;
        }

        // ── Ad operations ────────────────────────────────────────────────────

        public void LoadInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].LoadInterstitial adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)} proxy={_proxies.ContainsKey(adUnitId)}");
            LoadAd(adUnitId);
        }

        public void LoadRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].LoadRewarded adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)} proxy={_proxies.ContainsKey(adUnitId)}");
            LoadAd(adUnitId);
        }

        private void LoadAd(string adUnitId)
        {
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitId, out var proxy)) return;
            adObj.Call("load", _activity, proxy);
        }

        public void LoadAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].LoadAppOpen adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)} proxy={_proxies.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitId, out var proxy)) return;
            // AppOpen native API takes Application context for load (Builder
            // requirement) and Activity for show. Sketch CD-5.
            adObj.Call("load", _application, proxy);
        }

        // IsReady* family is polled frequently (often every frame from consumer
        // code); we skip dev-track tracing here to avoid console spam.
        public bool IsInterstitialReady(string adUnitId) => IsAdReady(adUnitId);
        public bool IsRewardedReady(string adUnitId)     => IsAdReady(adUnitId);
        public bool IsAppOpenReady(string adUnitId)      => IsAdReady(adUnitId);

        private bool IsAdReady(string adUnitId)
        {
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return false;
            return adObj.Call<bool>("isReady");
        }

        public void ShowInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].ShowInterstitial adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            ShowAd(adUnitId);
        }

        public void ShowRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].ShowRewarded adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            ShowAd(adUnitId);
        }

        public void ShowAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].ShowAppOpen adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            ShowAd(adUnitId);
        }

        private void ShowAd(string adUnitId)
        {
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            adObj.Call("show", _activity);
        }

        public void SetRewardedCustomData(string adUnitId, string customData)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].SetRewardedCustomData adUnit='{adUnitId}' len={customData.Length} adObj={_adObjects.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            adObj.Call("setCustomData", customData);
        }

        // ── Destroy ──────────────────────────────────────────────────────────

        public void DestroyInterstitial(string adUnitId)
        {
            DaroLog.Verbose("Interstitial", $"Platform[Android].DestroyInterstitial adUnit='{adUnitId}'");
            DestroyAdObject(adUnitId);
        }

        public void DestroyRewarded(string adUnitId)
        {
            DaroLog.Verbose("Rewarded", $"Platform[Android].DestroyRewarded adUnit='{adUnitId}'");
            DestroyAdObject(adUnitId);
        }

        public void DestroyAppOpen(string adUnitId)
        {
            DaroLog.Verbose("AppOpen", $"Platform[Android].DestroyAppOpen adUnit='{adUnitId}'");
            DestroyAdObject(adUnitId);
        }

        /// <summary>
        /// Native-first dispose ordering (sketch CD-9): Kotlin sets its
        /// <c>@Volatile destroyed</c> flag inside <c>destroy()</c> before
        /// calling <c>ad.destroy()</c>, so any further worker-thread listener
        /// invocation skips its callback before reaching the C# proxy. Only
        /// then is the JNI proxy ref dropped. <c>AndroidJavaProxy</c> is not
        /// <c>IDisposable</c>; the underlying JNI ref is released when the
        /// proxy is GC'd, so we just drop our last reference here.
        /// </summary>
        private void DestroyAdObject(string adUnitId)
        {
            InvalidateBannerGenerationIfNeeded(adUnitId);
            if (_adObjects.TryGetValue(adUnitId, out var adObj))
            {
                adObj.Call("destroy");   // Kotlin: @Volatile destroyed = true (Layer 1)
                adObj.Dispose();
                _adObjects.Remove(adUnitId);
            }
            _proxies.Remove(adUnitId);
        }

        private int NextBannerGeneration(string adUnitId)
        {
            _bannerGenerations.TryGetValue(adUnitId, out var current);
            var next = current + 1;
            _bannerGenerations[adUnitId] = next;
            return next;
        }

        private int CurrentBannerGeneration(string adUnitId)
        {
            _bannerGenerations.TryGetValue(adUnitId, out var current);
            return current;
        }

        private void InvalidateBannerGenerationIfNeeded(string adUnitId)
        {
            if (_proxies.TryGetValue(adUnitId, out var proxy) && proxy is DaroBannerCallbackProxy)
            {
                NextBannerGeneration(adUnitId);
            }
        }

        private bool IsCurrentBannerCallback(
            string adUnitId,
            int generation,
            DaroBannerCallbackProxy proxy)
        {
            return !_disposed
                && _bannerGenerations.TryGetValue(adUnitId, out var current)
                && current == generation
                && _proxies.TryGetValue(adUnitId, out var currentProxy)
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
        /// Plan deviation 2026-05-14: original plan §3 specified
        /// <c>_bridge.CallStatic("destroyAll")</c> forward-defined for the
        /// android-destroy-all task. Discovery during impl: <see cref="Dispose"/>
        /// already exists with the per-instance iteration + dict clear pattern
        /// the sketch §iOS-shim mirrored (originally orphan — never wired up).
        /// Wrapping it here drops the forward-define coupling — no new Kotlin
        /// method needed for csharp-runtime-hook to ship.
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

            // Native ad per-instance handle pattern (sketch CD-8) bypasses
            // _adObjects, so Dispose() above doesn't reach live native ads.
            // android-destroy-all task adds Kotlin DaroUnityBridge.destroyAll
            // which iterates a static registry of live DaroUnityNativeAd
            // instances. Order: per-instance dispose (above) first, then
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
        // silently (no compile-time error). See sketch §2.2, CD-7.
        // ─────────────────────────────────────────────────────────────────────

        private class DaroAdCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitId;
            private readonly DaroAdFormat        _format;
            private readonly DaroAndroidPlatform _platform;

            internal DaroAdCallbackProxy(
                string adUnitId, DaroAdFormat format, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroAdCallback")
            {
                _adUnitId = adUnitId;
                _format   = format;
                _platform = platform;
            }

            // latencyMs sourced from Kotlin's adInfo.latency / err.latency (millis).
            // Forwarded as-is to C# DaroAdInfo.Latency to match Daro's
            // cross-platform millis contract.
            private DaroAdInfo MakeInfo(string adUnitId, int latencyMs) =>
                new DaroAdInfo(_format, adUnitId, latencyMs);

            public void onAdLoaded(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitId, info));
            }

            public void onAdFailedToLoad(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                // rawCode = 네이티브가 준 값 그대로. shim 이 `DaroError.errorCode`
                // 를 보내므로 이것은 **Daro 코드**다 — `DaroAdLoadError.RawCode`
                // 의 계약("DaroSDK DaroError.Code.rawValue")과 iOS 경로에 맞는다.
                //
                // DARO-1542 전에는 shim 이 deprecated 콜백을 물어 `MaxError.code`
                // 를 그대로 보냈다. 그 시절엔 여기 주석이 "MaxError.code 를 보존한다"
                // 였는데, 그게 계약이 아니라 Android 만의 이탈이었다.
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitId, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitId, err));
            }

            public void onAdShown(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitId, info));
            }

            // errorCode is always -1 from Kotlin (DaroAdDisplayFailError has
            // no int code). Stored as rawCode for diagnostic continuity even
            // though it conveys no extra info on Android. Sketch §4.1.
            public void onAdFailedToShow(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitId, err));
            }

            public void onAdClicked(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitId, info));
            }

            public void onAdImpression(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitId, info));
            }

            public void onAdDismissed(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitId, info));
            }

            // No latency on the revenue path — daro-m's paid event tuple is
            // (valueMicros, currencyCode, precisionType) only.
            public void onAdRevenuePaid(
                string adUnitId, long valueMicros, string currencyCode, int precisionType)
            {
                if (_platform._disposed) return;
                var info    = new DaroAdInfo(_format, adUnitId, latency: null);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitId, info, revenue));
            }
        }

        // Standalone proxy for Rewarded — extends AndroidJavaProxy directly
        // with IDaroRewardedCallback (all 8 methods). NOT subclassing
        // DaroAdCallbackProxy because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and lose `onEarnedReward` JNI dispatch. Sketch CD-7.
        private sealed class DaroRewardedCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitId;
            private readonly DaroAndroidPlatform _platform;

            internal DaroRewardedCallbackProxy(string adUnitId, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroRewardedCallback")
            {
                _adUnitId = adUnitId;
                _platform = platform;
            }

            private DaroAdInfo MakeInfo(string adUnitId, int latencyMs) =>
                new DaroAdInfo(DaroAdFormat.Rewarded, adUnitId, latencyMs);

            public void onAdLoaded(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitId, info));
            }

            public void onAdFailedToLoad(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitId, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitId, err));
            }

            public void onAdShown(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitId, info));
            }

            public void onAdFailedToShow(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitId, err));
            }

            public void onAdClicked(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitId, info));
            }

            public void onAdImpression(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitId, info));
            }

            public void onAdDismissed(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitId, info));
            }

            public void onEarnedReward(
                string adUnitId, string rewardType, int rewardAmount, int latencyMs)
            {
                if (_platform._disposed) return;
                var info   = MakeInfo(adUnitId, latencyMs);
                var reward = new DaroRewardItem(rewardAmount, rewardType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onEarnedReward?.Invoke(adUnitId, info, reward));
            }

            public void onAdRevenuePaid(
                string adUnitId, long valueMicros, string currencyCode, int precisionType)
            {
                if (_platform._disposed) return;
                var info    = new DaroAdInfo(DaroAdFormat.Rewarded, adUnitId, latency: null);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitId, info, revenue));
            }
        }

        // Banner-specific proxy. Standalone (not subclassing
        // DaroAdCallbackProxy) because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and break JNI dispatch. Sketch §6.4 + CD-7 pattern.
        private sealed class DaroBannerCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitId;
            private readonly DaroAndroidPlatform _platform;

            internal DaroBannerCallbackProxy(string adUnitId, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroBannerCallback")
            {
                _adUnitId = adUnitId;
                _platform = platform;
            }

            private DaroAdInfo MakeInfo(string adUnitId, int latencyMs) =>
                new DaroAdInfo(DaroAdFormat.Banner, adUnitId, latencyMs);

            private bool IsCurrent(string adUnitId, int generation) =>
                _platform.IsCurrentBannerCallback(adUnitId, generation, this);

            private void EnqueueIfCurrent(string adUnitId, int generation, Action deliver)
            {
                // Native callbacks can arrive on worker threads. Keep all
                // Dictionary-backed generation/proxy checks on Unity main.
                if (_platform._disposed) return;
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (!IsCurrent(adUnitId, generation)) return;
                    deliver();
                });
            }

            public void onAdLoaded(string adUnitId, int generation, int latencyMs)
            {
                var info = MakeInfo(adUnitId, latencyMs);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdLoaded?.Invoke(adUnitId, info));
            }

            public void onAdFailedToLoad(
                string adUnitId, int generation, int errorCode, string errorMessage, int latencyMs)
            {
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitId, errorCode);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdFailedToLoad?.Invoke(adUnitId, err));
            }

            public void onAdImpression(string adUnitId, int generation, int latencyMs)
            {
                var info = MakeInfo(adUnitId, latencyMs);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdImpression?.Invoke(adUnitId, info));
            }

            public void onAdClicked(string adUnitId, int generation, int latencyMs)
            {
                var info = MakeInfo(adUnitId, latencyMs);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdClicked?.Invoke(adUnitId, info));
            }

            public void onAdHidden(string adUnitId, int generation)
            {
                var info = new DaroAdInfo(DaroAdFormat.Banner, adUnitId, latency: null);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdHidden?.Invoke(adUnitId, info));
            }

            public void onAdRevenuePaid(
                string adUnitId, int generation, long valueMicros, string currencyCode, int precisionType)
            {
                var info    = new DaroAdInfo(DaroAdFormat.Banner, adUnitId, latency: null);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                EnqueueIfCurrent(adUnitId, generation,
                    () => _platform._onAdRevenuePaid?.Invoke(adUnitId, info, revenue));
            }
        }

        // Light Popup 7-method proxy. Standalone (not subclassing
        // DaroAdCallbackProxy) because AndroidJavaProxy stores exactly one
        // interface name at construction; subclassing would register the
        // parent's name and break JNI dispatch for IDaroLightPopupCallback
        // methods. Same CD-7 reason as DaroRewardedCallbackProxy and
        // DaroBannerCallbackProxy.
        private sealed class DaroLightPopupCallbackProxy : AndroidJavaProxy
        {
            private readonly string              _adUnitId;
            private readonly DaroAndroidPlatform _platform;

            internal DaroLightPopupCallbackProxy(string adUnitId, DaroAndroidPlatform platform)
                : base("so.daro.unity.IDaroLightPopupCallback")
            {
                _adUnitId = adUnitId;
                _platform = platform;
            }

            private DaroAdInfo MakeInfo(string adUnitId, int latencyMs) =>
                new DaroAdInfo(DaroAdFormat.LightPopup, adUnitId, latencyMs);

            public void onAdLoaded(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdLoaded?.Invoke(adUnitId, info));
            }

            public void onAdFailedToLoad(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitId, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToLoad?.Invoke(adUnitId, err));
            }

            public void onAdShown(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdShown?.Invoke(adUnitId, info));
            }

            // errorCode is always -1 from Kotlin (DaroAdDisplayFailError has
            // no int code). Stored as rawCode for diagnostic continuity even
            // though it conveys no extra info on Android.
            public void onAdFailedToShow(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_platform._disposed) return;
                var err = new DaroAdDisplayError(
                    DaroAdErrorCodeMapper.ToDisplayErrorCode(errorCode),
                    errorMessage, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdFailedToShow?.Invoke(adUnitId, err));
            }

            public void onAdDismissed(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdDismissed?.Invoke(adUnitId, info));
            }

            public void onAdClicked(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdClicked?.Invoke(adUnitId, info));
            }

            public void onAdImpression(string adUnitId, int latencyMs)
            {
                if (_platform._disposed) return;
                var info = MakeInfo(adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdImpression?.Invoke(adUnitId, info));
            }

            public void onAdRevenuePaid(
                string adUnitId, long valueMicros, string currencyCode, int precisionType)
            {
                if (_platform._disposed) return;
                var info    = new DaroAdInfo(DaroAdFormat.LightPopup, adUnitId, latency: null);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                    _platform._onAdRevenuePaid?.Invoke(adUnitId, info, revenue));
            }
        }

        // ── Light Popup ad operations ────────────────────────────────────
        // Cannot use the generic CreateAdObject helper — Kotlin ctor takes
        // 36 params beyond adUnitId (9×4 ARGB ints + closeButtonText),
        // and the proxy needs interface name IDaroLightPopupCallback rather than
        // IDaroAdCallback. Sketch §Android Bridge / DaroAndroidPlatform additions.
        public void CreateLightPopup(string adUnitId, DaroLightPopupAdOptions options)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].CreateLightPopup adUnit='{adUnitId}'");
            // Native-first dispose ordering for any stale instance (CD-9).
            DestroyAdObject(adUnitId);

            var proxy = new DaroLightPopupCallbackProxy(adUnitId, this);

            // 37-arg ctor: adUnitId + 9 colors × 4 channels (A,R,G,B order
            // per field, matching Kotlin ctor signature) + closeButtonText. Color32 byte
            // (0–255 unsigned) cast to int for JNI — no sign-extension risk because
            // C# `byte` is unsigned, so `(int)(byte)0xB2 == 178`, never negative.
            var adObj = new AndroidJavaObject(
                "so.daro.unity.DaroUnityLightPopupAd",
                adUnitId,
                (int)options.BackgroundColor.a,            (int)options.BackgroundColor.r,            (int)options.BackgroundColor.g,            (int)options.BackgroundColor.b,
                (int)options.ContainerColor.a,             (int)options.ContainerColor.r,             (int)options.ContainerColor.g,             (int)options.ContainerColor.b,
                (int)options.AdMarkLabelTextColor.a,       (int)options.AdMarkLabelTextColor.r,       (int)options.AdMarkLabelTextColor.g,       (int)options.AdMarkLabelTextColor.b,
                (int)options.AdMarkLabelBackgroundColor.a, (int)options.AdMarkLabelBackgroundColor.r, (int)options.AdMarkLabelBackgroundColor.g, (int)options.AdMarkLabelBackgroundColor.b,
                (int)options.TitleColor.a,                 (int)options.TitleColor.r,                 (int)options.TitleColor.g,                 (int)options.TitleColor.b,
                (int)options.BodyColor.a,                  (int)options.BodyColor.r,                  (int)options.BodyColor.g,                  (int)options.BodyColor.b,
                (int)options.CtaBackgroundColor.a,         (int)options.CtaBackgroundColor.r,         (int)options.CtaBackgroundColor.g,         (int)options.CtaBackgroundColor.b,
                (int)options.CtaTextColor.a,               (int)options.CtaTextColor.r,               (int)options.CtaTextColor.g,               (int)options.CtaTextColor.b,
                (int)options.CloseButtonColor.a,           (int)options.CloseButtonColor.r,           (int)options.CloseButtonColor.g,           (int)options.CloseButtonColor.b,
                options.CloseButtonText ?? "Close"
            );

            _proxies[adUnitId]   = proxy;
            _adObjects[adUnitId] = adObj;
        }

        public void LoadLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].LoadLightPopup adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)} proxy={_proxies.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            if (!_proxies.TryGetValue(adUnitId, out var proxy)) return;
            adObj.Call("load", _activity, proxy);
        }

        public bool IsLightPopupReady(string adUnitId)
        {
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return false;
            return adObj.Call<bool>("isReady");
        }

        // DaroLightPopupAdImpl.show(activity: Activity) strictly requires Activity
        // (not just Context) for the underlying Dialog(activity) construction.
        public void ShowLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].ShowLightPopup adUnit='{adUnitId}' adObj={_adObjects.ContainsKey(adUnitId)}");
            if (!_adObjects.TryGetValue(adUnitId, out var adObj)) return;
            adObj.Call("show", _activity);
        }

        public void DestroyLightPopup(string adUnitId)
        {
            DaroLog.Verbose("LightPopup", $"Platform[Android].DestroyLightPopup adUnit='{adUnitId}'");
            DestroyAdObject(adUnitId);
        }

        // ── Native ad (CD-8 instance-owned) ──────────────────────────────
        // Each DaroNativeAd gets its own DaroAndroidNativeAdHandle (per-instance
        // AndroidJavaObject + AndroidJavaProxy). Native ad does NOT use
        // _adObjects / _proxies dicts — multi-instance for the same adUnitId
        // is supported (CD-8). See sketch-native-ad-android.md §5.6.
        public INativeAdHandle CreateNativeAdHandle(string adUnitId, INativeAdEventSink sink)
        {
            DaroLog.Verbose("Native", $"Platform[Android].CreateNativeAdHandle adUnit='{adUnitId}'");
            return new DaroAndroidNativeAdHandle(adUnitId, sink, _activity);
        }
    }
}

#endif
