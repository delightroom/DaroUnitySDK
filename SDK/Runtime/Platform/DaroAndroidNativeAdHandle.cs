#nullable enable
#if UNITY_ANDROID && !UNITY_EDITOR
using System;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Android implementation of <see cref="INativeAdHandle"/>. Owns a single
    /// <see cref="AndroidJavaObject"/> wrapping the Kotlin shim's
    /// <c>so.daro.unity.DaroUnityNativeAd</c> + an <see cref="AndroidJavaProxy"/>
    /// implementing <c>so.daro.unity.IDaroNativeAdCallback</c>. One handle per
    /// <see cref="DaroNativeAd"/> instance — multi-instance permitted.
    /// </summary>
    /// <remarks>
    /// <para>Multi-instance: each handle holds *its own* AndroidJavaObject
    /// (separate JNI global ref) and *its own* AndroidJavaProxy (separate JNI
    /// global ref). The Kotlin shim's <c>MaxNativeAdLoader</c> is per-instance
    /// (verified: <c>InternalDaroMNativeSingleLoader.kt:26</c> constructs per
    /// call), so the daro-m / MAX layer permits N concurrent loaders for the
    /// same mediation ad unit ID.</para>
    ///
    /// <para>Threading layers:
    /// <list type="bullet">
    ///   <item>Kotlin Layer 1 (<c>@Volatile destroyed</c> in DaroUnityNativeAd):
    ///   set by <c>destroy()</c>; every callback path short-circuits.</item>
    ///   <item>C# Layer 2 (<see cref="_disposed"/>): set by <see cref="Dispose"/>;
    ///   every proxy method checks BEFORE <see cref="MainThreadDispatcher.Enqueue"/>
    ///   so beat-Layer-1 callbacks still don't deliver to a stale sink.</item>
    /// </list></para>
    ///
    /// <para>Texture2D construction must happen on the Unity main thread —
    /// the proxy enqueues a closure that builds the <see cref="Texture2D"/>
    /// from the ARGB byte[] inside the Enqueue body.</para>
    /// </remarks>
    internal sealed class DaroAndroidNativeAdHandle : INativeAdHandle
    {
        private readonly string             _adUnitId;
        private readonly INativeAdEventSink _sink;
        private readonly AndroidJavaObject? _activity;

        private AndroidJavaObject? _adObject;
        private CallbackProxy?     _proxy;

        // Layer 2 disposed guard. Mirrors DaroAndroidPlatform._disposed pattern.
        private volatile bool _disposed;

        internal DaroAndroidNativeAdHandle(
            string adUnitId,
            string? placement,
            INativeAdEventSink sink,
            AndroidJavaObject? activity)
        {
            _adUnitId = adUnitId;
            _sink     = sink;
            _activity = activity;
            // placement: not used by Kotlin shim v1 — captured by the Kotlin
            // class internally if/when DaroAdInfoManager exposes placement.

            _adObject = new AndroidJavaObject("so.daro.unity.DaroUnityNativeAd", adUnitId);
            _proxy    = new CallbackProxy(this);
        }

        public void Load(int iconWidth, int iconHeight)
        {
            DaroLog.Verbose("Native", $"Handle[Android].Load adUnit='{_adUnitId}' icon={iconWidth}x{iconHeight} disposed={_disposed} adObj={_adObject != null}");
            if (_disposed || _adObject == null || _proxy == null) return;

            if (_activity == null)
            {
                // No activity captured (DaroSdk.InitializeAsync did not run).
                // Surface as load failure rather than throwing — matches how
                // other formats fail when activity is unavailable.
                var err = new DaroAdLoadError(
                    DaroAdLoadErrorCode.NotInitialized,
                    "Activity not available — call DaroSdk.InitializeAsync() before constructing DaroNativeAd.",
                    _adUnitId, rawCode: -1);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_disposed) return;
                    _sink.OnAdFailedToLoad(err);
                });
                return;
            }

            _adObject.Call("load", _activity, _proxy, iconWidth, iconHeight);
        }

        public void NotifyVisible()
        {
            DaroLog.Verbose("Native", $"Handle[Android].NotifyVisible adUnit='{_adUnitId}' disposed={_disposed}");
            if (_disposed || _adObject == null) return;
            _adObject.Call("notifyVisible");
        }

        public void NotifyHidden()
        {
            DaroLog.Verbose("Native", $"Handle[Android].NotifyHidden adUnit='{_adUnitId}' disposed={_disposed}");
            if (_disposed || _adObject == null) return;
            _adObject.Call("notifyHidden");
        }

        public void NotifyClicked()
        {
            DaroLog.Verbose("Native", $"Handle[Android].NotifyClicked adUnit='{_adUnitId}' disposed={_disposed}");
            if (_disposed || _adObject == null || _proxy == null) return;
            _adObject.Call("notifyClicked", _proxy);
        }

        // CTA overlay sync — iOS-only feature. Android click path is
        // View.performClick() through NotifyClicked above; no overlay geometry
        // is needed. Verbose log retained for cross-platform smoke-detection
        // parity if a future Android sprint reuses the channel.
        public void SetCtaScreenRect(UnityEngine.Rect rect, bool touchEnabled)
        {
            DaroLog.Verbose("Native",
                $"Handle[Android].SetCtaScreenRect adUnit='{_adUnitId}' rect={rect} touchEnabled={touchEnabled} (no-op)");
        }

        public void ClearCtaScreenRect()
        {
            DaroLog.Verbose("Native",
                $"Handle[Android].ClearCtaScreenRect adUnit='{_adUnitId}' (no-op)");
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;   // Layer 2 armed — proxy callbacks short-circuit.
            DaroLog.Verbose("Native", $"Handle[Android].Dispose adUnit='{_adUnitId}'");

            try
            {
                _adObject?.Call("destroy");   // Kotlin Layer 1 destroyed = true
                _adObject?.Dispose();
            }
            catch (Exception e)
            {
                DaroLog.Warn("Native",
                    $"DaroAndroidNativeAdHandle({_adUnitId}) destroy threw: {e}");
            }

            _adObject = null;
            // AndroidJavaProxy is not IDisposable — JNI ref releases on GC.
            _proxy    = null;
        }

        // ─────────────────────────────────────────────────────────────────
        // Inner proxy — implements so.daro.unity.IDaroNativeAdCallback via
        // AndroidJavaProxy. Method names MUST match the Kotlin interface
        // exactly (JNI resolves by string at runtime; silent failure on
        // mismatch — no compile-time check).
        // ─────────────────────────────────────────────────────────────────

        private sealed class CallbackProxy : AndroidJavaProxy
        {
            private readonly DaroAndroidNativeAdHandle _parent;

            internal CallbackProxy(DaroAndroidNativeAdHandle parent)
                : base("so.daro.unity.IDaroNativeAdCallback")
            {
                _parent = parent;
            }

            public void onAdLoaded(
                string adUnitId,
                string title, string body, string callToAction,
                byte[] iconPngBytes,
                int latencyMs)
            {
                if (_parent._disposed) return;

                // Build DaroAdInfo on this (worker) thread — pure data, no Unity API.
                var adInfo = new DaroAdInfo(DaroAdFormat.Native, adUnitId, latencyMs);

                // Texture2D construction is main-thread-only — defer to Enqueue.
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_parent._disposed) return;

                    var icon = BuildTexture(iconPngBytes);
                    // Empty string → null: daro-m's bound view had no text;
                    // honor the POCO's nullable contract.
                    var nativeInfo = new DaroNativeAdInfo(
                        title:        string.IsNullOrEmpty(title)        ? null : title,
                        body:         string.IsNullOrEmpty(body)         ? null : body,
                        callToAction: string.IsNullOrEmpty(callToAction) ? null : callToAction,
                        icon:         icon,
                        mediaImage:   null);   // v1 image-only; video deferred

                    _parent._sink.OnAdLoaded(adInfo, nativeInfo);
                });
            }

            public void onAdFailedToLoad(
                string adUnitId, int errorCode, string errorMessage, int latencyMs)
            {
                if (_parent._disposed) return;
                var err = new DaroAdLoadError(
                    DaroAdErrorCodeMapper.ToLoadErrorCode(errorCode),
                    errorMessage, adUnitId, errorCode);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_parent._disposed) return;
                    _parent._sink.OnAdFailedToLoad(err);
                });
            }

            public void onAdImpression(string adUnitId, int latencyMs)
            {
                if (_parent._disposed) return;
                var info = new DaroAdInfo(DaroAdFormat.Native, adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_parent._disposed) return;
                    _parent._sink.OnAdImpression(info);
                });
            }

            public void onAdClicked(string adUnitId, int latencyMs)
            {
                if (_parent._disposed) return;
                var info = new DaroAdInfo(DaroAdFormat.Native, adUnitId, latencyMs);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_parent._disposed) return;
                    _parent._sink.OnAdClicked(info);
                });
            }

            public void onAdRevenuePaid(
                string adUnitId, long valueMicros, string currencyCode, int precisionType)
            {
                if (_parent._disposed) return;
                var info    = new DaroAdInfo(DaroAdFormat.Native, adUnitId, latency: null);
                var revenue = DaroRevenueInfo.FromMicros(valueMicros, currencyCode, precisionType);
                MainThreadDispatcher.Enqueue(() =>
                {
                    if (_parent._disposed) return;
                    _parent._sink.OnAdRevenuePaid(info, revenue);
                });
            }

            // PNG bytes → Texture2D. `LoadImage` decodes PNG header, allocates
            // correct format/dimensions, and uses bottom-left origin so display
            // is upright (no row flipping needed). Avoids the
            // `ARGB_8888`-named-but-RGBA-byte-ordered Android quirk.
            private static Texture2D? BuildTexture(byte[] pngBytes)
            {
                if (pngBytes == null || pngBytes.Length == 0) return null;
                var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
                return tex.LoadImage(pngBytes) ? tex : null;
            }
        }
    }
}

#endif
