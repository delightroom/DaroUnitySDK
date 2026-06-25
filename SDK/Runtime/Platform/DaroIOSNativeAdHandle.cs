#nullable enable
#if UNITY_IOS && !UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;
using AOT;
using UnityEngine;
using UnityEngine.Scripting;

namespace Daro.Internal
{
    /// <summary>
    /// iOS implementation of <see cref="INativeAdHandle"/>. One handle per
    /// <see cref="Daro.DaroNativeAd"/> instance — multi-instance permitted
    /// (CD-1, CD-8). Each handle owns:
    /// <list type="bullet">
    ///   <item>a unique <see cref="_handleId"/> (monotonic int) routing key</item>
    ///   <item>a slot in the static <see cref="s_handles"/> dictionary for inbound callback dispatch</item>
    ///   <item>a peer entry on the native side keyed by the same id (<c>s_nativeAds[@handleId]</c> in DaroUnityNativeAd.mm)</item>
    /// </list>
    /// </summary>
    /// <remarks>
    /// <para>Multi-instance: Android JNI returns per-instance
    /// <c>AndroidJavaObject</c> as the natural routing key
    /// (<see cref="DaroAndroidNativeAdHandle"/>); iOS PInvoke can't, so an
    /// explicit monotonic int id is allocated by C# and threaded through every
    /// extern call. Native shim mirrors with <c>NSDictionary&lt;NSNumber*, DaroUnityNativeAdEntry*&gt;</c>.</para>
    ///
    /// <para>Threading layers (CD-9 — mirrors
    /// <see cref="DaroAndroidNativeAdHandle"/>'s pattern but inverted at
    /// Layer 2 since iOS doesn't need MainThreadDispatcher):
    /// <list type="bullet">
    ///   <item>ObjC Layer 1 (<c>BOOL destroyed</c> on
    ///   <c>DaroUnityNativeAdEntry</c>): set inside the shim's
    ///   <c>DaroUnity_NativeAd_Destroy</c> after entry lookup; every delegate
    ///   path short-circuits.</item>
    ///   <item>C# Layer 2 (<see cref="_disposed"/>): set by
    ///   <see cref="Dispose"/> BEFORE the PInvoke into Destroy fires; the
    ///   static dispatcher checks it after dictionary lookup so beat-Layer-1
    ///   callbacks still don't deliver to a stale sink.</item>
    /// </list></para>
    ///
    /// <para><b>No <see cref="MainThreadDispatcher.Enqueue"/></b> — unlike
    /// Android (where JNI callbacks fire on the daro-m worker thread and
    /// <c>Texture2D</c> construction must defer to the main thread),
    /// <c>DaroUnityBridge.mm</c>'s emit path runs on the main queue
    /// (sketch CD-7 + <c>DaroUnityBridge.mm:14-17</c>). The static dispatcher
    /// builds <see cref="Texture2D"/> inline.</para>
    ///
    /// <para>Stripping defense: <c>SDK/Runtime/link.xml</c> already preserves
    /// the <c>Daro.Internal</c> namespace. <see cref="OnNativeAdEvent"/> +
    /// <see cref="BuildTexture"/> additionally carry
    /// <see cref="PreserveAttribute"/> at the call site (mirrors
    /// <see cref="DaroIOSPlatform.OnNativeEvent"/> belt-and-suspenders posture
    /// at <c>DaroIOSPlatform.cs:205</c>).</para>
    /// </remarks>
    internal sealed class DaroIOSNativeAdHandle : INativeAdHandle
    {
        // ── instance state ─────────────────────────────────────────────────
        private readonly string             _adUnitId;
        private readonly INativeAdEventSink _sink;
        private readonly int                _handleId;

        // Layer-2 guard. volatile read; single-writer (Dispose) + many-reader
        // (static dispatcher), all on Unity main thread.
        private volatile bool _disposed;

        // ── static routing table ───────────────────────────────────────────
        private static readonly Dictionary<int, DaroIOSNativeAdHandle> s_handles = new();
        private static readonly object s_lock = new();
        private static int s_nextId;   // accessed via Interlocked.Increment

        // ── ctor ───────────────────────────────────────────────────────────
        internal DaroIOSNativeAdHandle(string adUnitId, string? placement, INativeAdEventSink sink)
        {
            _adUnitId = adUnitId;
            _sink     = sink;
            _handleId = Interlocked.Increment(ref s_nextId);

            // Register BEFORE Create — defensive against a future shim variant
            // emitting synchronously from Create. Lock is short-lived; no hot path.
            lock (s_lock) { s_handles[_handleId] = this; }

            EnsureNativeAdCallbackRegistered();
            DaroUnity_NativeAd_Create(_handleId, adUnitId, placement);
        }

        // ── INativeAdHandle ────────────────────────────────────────────────
        public void Load(int iconWidth, int iconHeight)
        {
            DaroLog.Verbose("Native", $"Handle[iOS].Load adUnit='{_adUnitId}/h{_handleId}' icon={iconWidth}x{iconHeight} disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_Load(_handleId, iconWidth, iconHeight);
        }

        public void NotifyVisible()
        {
            DaroLog.Verbose("Native", $"Handle[iOS].NotifyVisible adUnit='{_adUnitId}/h{_handleId}' disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_NotifyVisible(_handleId);
        }

        public void NotifyHidden()
        {
            DaroLog.Verbose("Native", $"Handle[iOS].NotifyHidden adUnit='{_adUnitId}/h{_handleId}' disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_NotifyHidden(_handleId);
        }

        public void NotifyClicked()
        {
            DaroLog.Verbose("Native", $"Handle[iOS].NotifyClicked adUnit='{_adUnitId}/h{_handleId}' disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_NotifyClicked(_handleId);
        }

        // CTA overlay geometry sync. 4 floats individually (no marshalled
        // struct); Unity pixel-space rect, bottom-left origin, no DPI
        // division. The iOS shim performs UIKit point conversion + Y-flip.
        public void SetCtaScreenRect(UnityEngine.Rect rect, bool touchEnabled)
        {
            DaroLog.Verbose("Native",
                $"Handle[iOS].SetCtaScreenRect adUnit='{_adUnitId}/h{_handleId}' rect={rect} touchEnabled={touchEnabled} disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_SetCtaScreenRect(
                _handleId, rect.x, rect.y, rect.width, rect.height, touchEnabled);
        }

        public void ClearCtaScreenRect()
        {
            DaroLog.Verbose("Native",
                $"Handle[iOS].ClearCtaScreenRect adUnit='{_adUnitId}/h{_handleId}' disposed={_disposed}");
            if (_disposed) return;
            DaroUnity_NativeAd_ClearCtaScreenRect(_handleId);
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;   // Layer-2 armed FIRST (sketch §5.3 invariant)
            DaroLog.Verbose("Native", $"Handle[iOS].Dispose adUnit='{_adUnitId}/h{_handleId}'");

            try
            {
                DaroUnity_NativeAd_Destroy(_handleId);   // ObjC Layer-1 destroyed=YES
            }
            catch (Exception e)
            {
                DaroLog.Warn("Native",
                    $"DaroIOSNativeAdHandle({_adUnitId}/h{_handleId}) destroy threw: {e}");
            }

            // De-register AFTER PInvoke returns — Layer-2 already gates any
            // in-flight callback that resolved the dict before this Remove.
            // The Remove just frees memory.
            lock (s_lock) { s_handles.Remove(_handleId); }
        }

        // ── Static native-ad callback (CD-2 channel; CD-10 strip defense) ──
        private delegate void DaroNativeAdCallbackFn(
            int handleId, string eventJson, IntPtr iconPng, int iconLen);

        private static int s_callbackRegistered;   // 0 = not yet, 1 = done

        private static void EnsureNativeAdCallbackRegistered()
        {
            // Idempotent — first ctor on the process arms the channel.
            if (Interlocked.CompareExchange(ref s_callbackRegistered, 1, 0) == 0)
            {
                DaroUnity_NativeAd_SetCallback(OnNativeAdEvent);
            }
        }

        [Preserve, MonoPInvokeCallback(typeof(DaroNativeAdCallbackFn))]
        private static void OnNativeAdEvent(int handleId, string eventJson, IntPtr iconPng, int iconLen)
        {
            // (1) Resolve handle (lock + Layer-2 guard).
            DaroIOSNativeAdHandle? handle;
            lock (s_lock) { s_handles.TryGetValue(handleId, out handle); }
            if (handle == null) return;          // post-Dispose Remove
            if (handle._disposed) return;        // beat-Layer-1 dispatch path

            // (2) Parse + dispatch. Identical schema to Android proxy
            //     (DaroAdInfo on Native format + DaroAdLoadError mapping +
            //     DaroNativeAdInfo string-empty-to-null). No
            //     MainThreadDispatcher.Enqueue — DaroUnityBridge already
            //     main-queue-marshals every callback.
            string? evt = DaroJsonHelpers.GetJsonString(eventJson, "event");
            if (evt == null) return;             // malformed → silent drop

            string adUnitId = handle._adUnitId;

            switch (evt)
            {
                case "adLoaded":
                {
                    var latency = DaroJsonHelpers.GetJsonDouble(eventJson, "latency");
                    var adInfo  = new DaroAdInfo(DaroAdFormat.Native, adUnitId, latency);

                    Texture2D? icon = BuildTexture(iconPng, iconLen);

                    var title = DaroJsonHelpers.GetJsonString(eventJson, "title");
                    var body  = DaroJsonHelpers.GetJsonString(eventJson, "body");
                    var cta   = DaroJsonHelpers.GetJsonString(eventJson, "callToAction");
                    // false signals unsupported CTA GR wiring; click chain
                    // inactive for this fill. Default true preserves back-compat
                    // for any emitter that drops the field.
                    var isCtaInteractive = DaroJsonHelpers.GetJsonBool(
                        eventJson, "isCtaInteractive", defaultValue: true);

                    var nativeInfo = new DaroNativeAdInfo(
                        title:            string.IsNullOrEmpty(title) ? null : title,
                        body:             string.IsNullOrEmpty(body)  ? null : body,
                        callToAction:     string.IsNullOrEmpty(cta)   ? null : cta,
                        icon:             icon,
                        mediaImage:       null,   // v1 image-only; video deferred
                        isCtaInteractive: isCtaInteractive);

                    Safely(() => handle._sink.OnAdLoaded(adInfo, nativeInfo));
                    break;
                }
                case "adFailedToLoad":
                {
                    var raw = DaroJsonHelpers.GetJsonInt   (eventJson, "errorCode");
                    var msg = DaroJsonHelpers.GetJsonString(eventJson, "errorMessage") ?? string.Empty;
                    var err = new DaroAdLoadError(
                        DaroAdErrorCodeMapper.ToLoadErrorCode(raw), msg, adUnitId, raw);
                    Safely(() => handle._sink.OnAdFailedToLoad(err));
                    break;
                }
                case "adImpression":
                {
                    var info = new DaroAdInfo(
                        DaroAdFormat.Native, adUnitId,
                        DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => handle._sink.OnAdImpression(info));
                    break;
                }
                case "adClicked":
                {
                    var info = new DaroAdInfo(
                        DaroAdFormat.Native, adUnitId,
                        DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    Safely(() => handle._sink.OnAdClicked(info));
                    break;
                }
                case "adRevenuePaid":
                {
                    var info = new DaroAdInfo(
                        DaroAdFormat.Native, adUnitId,
                        DaroJsonHelpers.GetJsonDouble(eventJson, "latency"));
                    var revenue = DaroRevenueInfo.FromDecimalString(
                        DaroJsonHelpers.GetJsonString(eventJson, "value"),
                        DaroJsonHelpers.GetJsonString(eventJson, "currencyCode") ?? "USD",
                        DaroJsonHelpers.GetJsonInt(eventJson, "precisionType"));
                    Safely(() => handle._sink.OnAdRevenuePaid(info, revenue));
                    break;
                }
                // unknown event → silent drop (forward-compat)
            }
        }

        // PNG bytes → Texture2D. `LoadImage` decodes PNG header, allocates
        // correct format/dimensions, handles bottom-left origin so display is
        // upright. Sidesteps the RGBA byte-order + origin pitfalls per memory
        // `feedback_unity_android_image_marshal_via_png.md`. UIImage's PNG
        // representation is self-describing on the same axis, so the same
        // decoder applies — identical to
        // <see cref="DaroAndroidNativeAdHandle"/>'s BuildTexture.
        [Preserve]
        private static Texture2D? BuildTexture(IntPtr pngBytes, int length)
        {
            if (pngBytes == IntPtr.Zero || length <= 0) return null;
            var managed = new byte[length];
            Marshal.Copy(pngBytes, managed, 0, length);
            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, mipChain: false);
            return tex.LoadImage(managed) ? tex : null;
        }

        private static void Safely(Action call)
        {
            try { call(); }
            catch (Exception ex) { DaroLog.Exception("Native", ex); }
        }

        // ── extern C surface (matches DaroUnityNativeAd.mm emit side) ──────
        private const string DLL = "__Internal";

        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_SetCallback(
            DaroNativeAdCallbackFn callback);

        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_Create(
            int handleId, string adUnitId, string? placement);

        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_Load(
            int handleId, int iconWidth, int iconHeight);

        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_NotifyVisible(int handleId);
        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_NotifyHidden (int handleId);
        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_NotifyClicked(int handleId);
        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_Destroy      (int handleId);

        // CTA overlay sync. 4 floats individually + bool; no struct
        // marshalling. Matching signature in SDK/Plugins/iOS/DaroUnityBridgeInternal.h.
        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_SetCtaScreenRect(
            int handleId, float x, float y, float width, float height, bool touchEnabled);
        [DllImport(DLL)] private static extern void DaroUnity_NativeAd_ClearCtaScreenRect(int handleId);
    }
}
#endif
