#nullable enable

using System;
using System.Collections.Generic;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Native ad instance — publisher-renders pattern. Exposes asset payload
    /// (<see cref="Info"/>) so consumers either bind it through
    /// <see cref="DaroNativeAdView"/> (slot path, auto-fill + auto click wire)
    /// or read fields directly into custom UI (raw path).
    /// </summary>
    /// <remarks>
    /// <para>Unlike fullscreen + banner formats (platform-managed dict keyed
    /// by <c>adUnitId</c>), Native is <em>instance-owned</em>: each instance
    /// gets its own <see cref="INativeAdHandle"/>; same <c>adUnitId</c> across
    /// N instances yields N independent native loaders. Natural fit for list
    /// UIs.</para>
    ///
    /// <para>No <c>Show</c> / <c>OnAdShown</c> / <c>OnAdDismissed</c> — the
    /// publisher controls visibility by activating the prefab GameObject.
    /// No <c>OnAdExpired</c>: <c>MaxNativeAdListener</c> has no expiry callback
    /// and daro-m exposes no push signal for it.</para>
    ///
    /// <para><see cref="Info"/>'s <c>Texture2D</c> fields (<c>Icon</c>,
    /// <c>MediaImage</c>) are owned by this instance — <see cref="Dispose"/>
    /// destroys them. Publisher must not retain raw <c>Texture2D</c> references
    /// past Dispose.</para>
    ///
    /// <para>Events fire on the Unity main thread — the handle marshals
    /// (Editor coroutine is already on main; Android handle uses
    /// <c>MainThreadDispatcher.Enqueue</c>).</para>
    /// </remarks>
    public sealed class DaroNativeAd : IDisposable
    {
        // ── Identity ─────────────────────────────────────────────────────
        public string  AdUnitId  { get; }

        /// <summary>Requested corner. Null preserves the platform's existing placement.</summary>
        public DaroAdChoicesPosition? AdChoicesPosition { get; }

        /// <summary>
        /// Pixel dimensions hint the platform shim uses to size the off-screen
        /// host containing the ad's IconImage. MAX's internal Glide image loader
        /// reads <c>imageView.getWidth()/getHeight()</c> at download-decision time
        /// and either downloads at that resolution (good — no waste) or skips the
        /// download entirely (bad — when 0×0). Default 200×200 is sane for typical
        /// native ad icons; publishers using <see cref="DaroNativeAdView"/> can
        /// auto-derive via <c>view.ApplySizeHints(ad)</c> before <see cref="Load"/>.
        /// Raw-path publishers set this property directly.
        /// </summary>
        public Vector2Int IconSize { get; set; } = new Vector2Int(DefaultIconSize, DefaultIconSize);

        private const int DefaultIconSize = 200;

        // ── Load result — valid from OnAdLoaded until Dispose / re-Load ──
        public DaroNativeAdInfo? Info { get; private set; }

        // ── Readiness ────────────────────────────────────────────────────
        public bool IsReady => _loaded && !_disposed;

        // ── Events ───────────────────────────────────────────────────────
        public event Action<ISet<NativeAdAssetType>>? OnNativeAdAssetLoaded;
        public event Action<DaroAdInfo>?      OnAdLoaded;
        public event Action<DaroAdLoadError>? OnAdFailedToLoad;
        public event Action<DaroAdInfo>?      OnAdImpression;
        public event Action<DaroAdInfo>?      OnAdClicked;

        /// <summary>
        /// Fires once per paid impression with the revenue reported by the
        /// mediation layer (ILRD). May lag <see cref="OnAdImpression"/> by a
        /// beat; not every impression is guaranteed a revenue report.
        /// </summary>
        public event Action<DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid;

        internal volatile bool _disposed;
        internal bool IsDisposed => _disposed;

        // Main-thread-only — set in FireOnAdLoaded, read by IsReady. No volatile needed.
        private bool _loaded;

        private INativeAdHandle? _handle;

        // CTA overlay driver — attached on WireCtaButton, detached on
        // UnwireCta / Dispose. Null on raw escape-hatch path. Actual
        // MonoBehaviour body lives in SDK/Runtime/Internal/DaroNativeCtaDriver.cs.
        private DaroNativeCtaDriver? _ctaDriver;
        private DaroNativeAdChoicesDriver? _adChoicesDriver;

        /// <summary>
        /// Slot-view enabled signal — set by <see cref="DaroNativeAdView"/>'s
        /// OnEnable / OnDisable hooks. Driver composite reads this so the
        /// overlay touch gate follows the slot-view's enabled state.
        /// Default <c>true</c> so raw-path (no slot view) publishers see no
        /// effect.
        /// </summary>
        internal bool IsSlotViewActive { get; set; } = true;

        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroNativeAd(string adUnitId) : this(adUnitId, null) { }

        /// <summary>
        /// Creates an ad with AdChoices in the requested corner of its full ad area.
        /// Bind a DaroNativeAdView or call WireAdChoices to provide that area.
        /// </summary>
        public DaroNativeAd(string adUnitId, DaroAdChoicesPosition adChoicesPosition)
            : this(adUnitId, (DaroAdChoicesPosition?)adChoicesPosition) { }

        private DaroNativeAd(string adUnitId, DaroAdChoicesPosition? adChoicesPosition)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            if (adChoicesPosition.HasValue &&
                !Enum.IsDefined(typeof(DaroAdChoicesPosition), adChoicesPosition.Value))
                throw new ArgumentOutOfRangeException(nameof(adChoicesPosition));
            AdChoicesPosition = adChoicesPosition;

            // Sink holds a direct reference to this instance — routing is
            // per-instance, no registry lookup needed.
            var sink = new InstanceSink(this);
            _handle  = DaroPlatform.Current.CreateNativeAdHandle(adUnitId, sink);
            try
            {
                if (adChoicesPosition.HasValue) _handle.ConfigureAdChoices(adChoicesPosition.Value);
            }
            catch
            {
                _handle.Dispose();
                _handle = null;
                throw;
            }
            DaroLog.Verbose("Native", $"ctor adUnit='{AdUnitId}'");
        }

        /// <summary>
        /// Start loading. Post-init failures fire <see cref="OnAdFailedToLoad"/>
        /// rather than throwing.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroNativeAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitId, out var preconditionError))
            {
                DaroLog.Warn("Native",
                    $"Load short-circuited adUnit='{AdUnitId}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            // Clamp to ≥1 — Glide rejects 0-dim views, our shim host needs >0.
            var w = Mathf.Max(1, IconSize.x);
            var h = Mathf.Max(1, IconSize.y);
            DaroLog.Verbose("Native", $"Load adUnit='{AdUnitId}' iconSize={w}x{h}");
            _loaded = false;
            _ctaDriver?.InvalidateSync();
            _adChoicesDriver?.InvalidateSync();
            _handle!.Load(w, h);
        }

        /// <summary>
        /// Mark the publisher UI as visible. On iOS the native overlay is
        /// displayed only after load success and valid CTA geometry. Slot path:
        /// <see cref="DaroNativeAdView"/> calls this from <c>OnEnable</c>.
        /// Raw path: publisher calls this when their UI activates.
        /// No-op after <see cref="Dispose"/>.
        /// </summary>
        public void NotifyVisible()
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"NotifyVisible adUnit='{AdUnitId}'");
            _handle?.NotifyVisible();
        }

        /// <summary>
        /// Mark the ad as hidden. Slot path: <see cref="DaroNativeAdView"/>
        /// calls this from <c>OnDisable</c>. Raw path: publisher calls this
        /// when their UI hides. No-op after <see cref="Dispose"/>.
        /// </summary>
        public void NotifyHidden()
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"NotifyHidden adUnit='{AdUnitId}'");
            _handle?.NotifyHidden();
        }

        /// <summary>
        /// Trigger the SDK click pathway. Slot path: <see cref="DaroNativeAdView"/>
        /// auto-wires this to its <c>CtaButton.onClick</c>. Raw path: publisher
        /// invokes this from their own Button.onClick handler. No-op after
        /// <see cref="Dispose"/>.
        /// </summary>
        public void NotifyClicked()
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"NotifyClicked adUnit='{AdUnitId}'");
            _handle?.NotifyClicked();
        }

        // ── CTA overlay wiring (native click overlay; Editor inert) ─────────
        //
        // iOS overlay is a **single touch consumer** — a real UITouch on the
        // Unity Button visual area is caught by the native UIKit overlay,
        // so publisher's `Button.onClick` does NOT fire on iOS when the
        // overlay is active. Publishers must use <see cref="OnAdClicked"/>
        // event for cross-platform click handling; attaching listeners to
        // the Unity Button.onClick to react to iOS ad clicks will silently
        // miss.

        /// <summary>
        /// Helper-bound CTA wiring (primary raw-path API). SDK takes ownership
        /// of the Button's screen geometry + composite interactability state,
        /// syncing them to the native overlay every <c>LateUpdate</c>.
        /// iOS / Android use that overlay as the real click target. Editor:
        /// no native overlay, click still flows through <see cref="NotifyClicked"/>.
        /// </summary>
        /// <param name="button">Publisher's uGUI CTA Button. Must not be null.
        /// Idempotent on the same Button; auto-unwires the previous Button if
        /// re-called with a different one.</param>
        /// <exception cref="ArgumentNullException">If <paramref name="button"/> is null.</exception>
        /// <exception cref="NotSupportedException">If the Button's ancestor
        /// Canvas is <c>RenderMode.WorldSpace</c>. WorldSpace projection isn't
        /// supported in v1 — accepting it would land publisher with a loaded
        /// ad that can't dispatch clicks (silent broken state). Use
        /// ScreenSpaceOverlay or ScreenSpaceCamera.</exception>
        /// <remarks>
        /// No-op after <see cref="Dispose"/>. The actual driver
        /// MonoBehaviour attach happens in <c>DaroNativeCtaDriver.Attach</c>
        /// (see <c>SDK/Runtime/Internal/DaroNativeCtaDriver.cs</c>).
        /// </remarks>
        public void WireCtaButton(UnityEngine.UI.Button button)
        {
            if (button == null) throw new ArgumentNullException(nameof(button));
            if (_disposed) return;

            // Reject WorldSpace at wire time — keeps the driver's per-frame
            // path simple + fails fast for unsupported render modes.
            var canvas = button.GetComponentInParent<Canvas>();
            if (canvas != null && canvas.renderMode == RenderMode.WorldSpace)
            {
                throw new NotSupportedException(
                    $"WireCtaButton: WorldSpace canvas '{canvas.name}' is not " +
                    "supported in v1 (ScreenSpaceOverlay / ScreenSpaceCamera only). " +
                    "Native click overlay cannot project a WorldSpace rect onto a " +
                    "UIKit hit-test region.");
            }

            // Idempotent on same Button; rewire on a different one.
            if (_ctaDriver != null)
            {
                if (_ctaDriver.Button == button) return;
                UnwireCta();
            }

            DaroLog.Verbose("Native",
                $"WireCtaButton adUnit='{AdUnitId}' button='{button.name}' " +
                $"canvas='{canvas?.name ?? "<none>"}' mode={canvas?.renderMode}");

            _ctaDriver = DaroNativeCtaDriver.Attach(this, button);
        }

        /// <summary>
        /// Detach the helper-bound CTA driver. Idempotent. Calls the platform
        /// handle's <c>ClearCtaScreenRect</c> via the driver's Detach path.
        /// No-op after <see cref="Dispose"/> (Dispose itself unwires first).
        /// </summary>
        public void UnwireCta()
        {
            if (_disposed) return;
            if (_ctaDriver == null) return;

            DaroLog.Verbose("Native", $"UnwireCta adUnit='{AdUnitId}'");
            _ctaDriver.Detach();   // → ClearCtaScreenRect on live handle → disable driver
            _ctaDriver = null;
        }

        /// <summary>
        /// Direct escape hatch (advanced raw-path API). Push a CTA overlay
        /// screen rect + touch-enabled state explicitly. Publisher takes full
        /// ownership of every lifecycle transition: call <see cref="NotifyVisible"/>
        /// and <see cref="NotifyHidden"/> with the UI's visibility, re-call on layout /
        /// interactability change, and <see cref="ClearCtaScreenRect"/> on
        /// teardown. Mis-use produces stale overlays that accept clicks against
        /// hidden / disabled publisher UI.
        /// </summary>
        /// <param name="screenRect">CTA rect in Unity pixel space (origin
        /// bottom-left, no DPI division). Compute via
        /// <c>RectTransformUtility.WorldToScreenPoint</c> over the Button's
        /// world corners.</param>
        /// <param name="touchEnabled">Whether the overlay should receive
        /// <c>UITouch</c>. Publisher composes from their own visibility +
        /// interactability signals.</param>
        /// <remarks>
        /// Most publishers should use <see cref="WireCtaButton"/> instead.
        /// No-op after <see cref="Dispose"/>. Non-finite rect (NaN / Inf
        /// components) → warn + return.
        /// </remarks>
        public void SetCtaScreenRect(Rect screenRect, bool touchEnabled)
        {
            if (_disposed) return;
            if (!IsFiniteRect(screenRect))
            {
                DaroLog.Warn("Native",
                    $"SetCtaScreenRect adUnit='{AdUnitId}' rejected — non-finite rect {screenRect}.");
                return;
            }
            _handle?.SetCtaScreenRect(screenRect, touchEnabled);
        }

        /// <summary>
        /// Direct escape-hatch counterpart — invalidate overlay geometry.
        /// On iOS this hides native UI and disables touch until new geometry
        /// is supplied. Publisher must call on teardown when
        /// using <see cref="SetCtaScreenRect"/>. <see cref="WireCtaButton"/>
        /// users do not need to call this — the helper auto-clears on its
        /// own lifecycle.
        /// </summary>
        public void ClearCtaScreenRect()
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"ClearCtaScreenRect adUnit='{AdUnitId}'");
            _handle?.ClearCtaScreenRect();
        }

        /// <summary>
        /// Tracks the full ad area's screen bounds for AdChoices, independently of the CTA.
        /// ScreenSpaceOverlay and ScreenSpaceCamera canvases are supported.
        /// </summary>
        public void WireAdChoices(RectTransform adArea)
        {
            if (adArea == null) throw new ArgumentNullException(nameof(adArea));
            if (_disposed) return;
            if (!AdChoicesPosition.HasValue)
                throw new InvalidOperationException("Choose an AdChoices position when constructing the ad.");
            var canvas = adArea.GetComponentInParent<Canvas>();
            if (canvas == null || canvas.renderMode == RenderMode.WorldSpace)
                throw new NotSupportedException("AdChoices requires a ScreenSpaceOverlay or ScreenSpaceCamera canvas.");
            if (_adChoicesDriver != null && _adChoicesDriver.AdArea == adArea) return;
            UnwireAdChoices();
            _adChoicesDriver = DaroNativeAdChoicesDriver.Attach(this, adArea);
        }

        /// <summary>Stops tracking the ad area and removes its AdChoices overlay.</summary>
        public void UnwireAdChoices()
        {
            if (_disposed) return;
            _adChoicesDriver?.Detach();
            _adChoicesDriver = null;
        }

        /// <summary>
        /// Raw-path geometry in Unity pixels (bottom-left origin). Update after layout/rotation.
        /// Visibility gates the native overlay; the CTA touch flag is configured separately.
        /// </summary>
        public void SetAdChoicesScreenRect(Rect adScreenRect, bool visible)
        {
            if (_disposed) return;
            if (!AdChoicesPosition.HasValue)
                throw new InvalidOperationException("Choose an AdChoices position when constructing the ad.");
            if (!IsFiniteRect(adScreenRect))
                throw new ArgumentException("Ad area must contain finite coordinates.", nameof(adScreenRect));
            _handle?.SetAdChoicesScreenRect(adScreenRect, visible);
        }

        /// <summary>Hides the AdChoices overlay and clears its full ad area.</summary>
        public void ClearAdChoicesScreenRect()
        {
            if (!_disposed) _handle?.ClearAdChoicesScreenRect();
        }

        private static bool IsFiniteRect(Rect r) =>
            !float.IsNaN(r.x)      && !float.IsInfinity(r.x) &&
            !float.IsNaN(r.y)      && !float.IsInfinity(r.y) &&
            !float.IsNaN(r.width)  && !float.IsInfinity(r.width) &&
            !float.IsNaN(r.height) && !float.IsInfinity(r.height);

        // ── IDisposable ──────────────────────────────────────────────────

        /// <summary>
        /// Idempotent dispose. Releases the platform handle and destroys
        /// owned <c>Texture2D</c> assets. Never throws (IDisposable contract).
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer backstop: attempts to release the handle and owned textures
        /// if the consumer dropped the reference without calling
        /// <see cref="Dispose"/>.
        /// </summary>
        ~DaroNativeAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            var info = Info;
            var handle = _handle;

            // Driver Detach must happen BEFORE _disposed=true — the Detach
            // path calls ClearCtaScreenRect through the still-live handle.
            // Finalizer-path (disposing=false) skips this — driver MonoBehaviour
            // GC may run off main thread.
            if (disposing && _ctaDriver != null)
            {
                try { _ctaDriver.Detach(); }
                catch (Exception e)
                {
                    DaroLog.Warn("Native",
                        $"DaroNativeAd({AdUnitId}) cta driver Detach threw: {e}");
                }
                _ctaDriver = null;
            }

            if (disposing && _adChoicesDriver != null)
            {
                try { _adChoicesDriver.Detach(); }
                catch (Exception e) { DaroLog.Warn("Native", $"AdChoices driver Detach threw: {e}"); }
                _adChoicesDriver = null;
            }

            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("Native", $"Dispose adUnit='{AdUnitId}'");

                OnNativeAdAssetLoaded = null;
                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdImpression   = null;
                OnAdClicked      = null;
                OnAdRevenuePaid  = null;
            }
            Info = null;

            try
            {
                DaroFinalizerRelease.RunRelease(disposing, () =>
                {
                    // Texture2D.Destroy is main-thread-only; RunRelease marshals
                    // the finalizer path and explicit Dispose is a main-thread API.
                    DestroyInfoTextures(info);
                    handle?.Dispose();
                });
            }
            catch (Exception e)
            {
                DaroLog.Warn("Native",
                    $"DaroNativeAd({AdUnitId}) handle Dispose threw: {e}");
            }
            _handle = null;
        }

        // ── Internal Fire* (called from sink on Unity main thread) ───────
        //
        // Native ad uses INativeAdEventSink direct routing instead of the
        // (format, adUnitId) registry pattern other formats use. The Find
        // gate in DaroAdInstanceRegistry does not see native ad callbacks
        // because the registry has no entry for them. To prevent in-flight
        // native callbacks after teardown from dispatching public C# events,
        // each Fire method consults DaroAdInstanceRegistry.IsShuttingDown directly.
        // Without it there is a window between MarkShuttingDown and the
        // Kotlin `@Volatile destroyed` set (via bridge.destroyAll →
        // snapshotLiveNativeAds().forEach { it.destroy() }) where a native
        // callback can still flow through the sink to the publisher.

        internal void FireOnAdLoaded(DaroAdInfo adInfo, DaroNativeAdInfo nativeInfo)
        {
            if (_disposed || DaroAdInstanceRegistry.IsShuttingDown)
            {
                DestroyInfoTextures(nativeInfo);
                return;
            }

            var previousInfo = Info;
            _loaded = true;
            Info    = nativeInfo;
            _ctaDriver?.InvalidateSync();
            _adChoicesDriver?.InvalidateSync();
            DaroLog.Verbose("Native", $"FireOnAdLoaded adUnit='{AdUnitId}' title='{nativeInfo.Title}' cta='{nativeInfo.CallToAction}' icon={(nativeInfo.Icon != null ? "present" : "null")} latency={adInfo.Latency}");
            var assetHandlers = OnNativeAdAssetLoaded?.GetInvocationList();
            if (assetHandlers != null)
            {
                foreach (Action<ISet<NativeAdAssetType>> handler in assetHandlers)
                {
                    if (_disposed || DaroAdInstanceRegistry.IsShuttingDown) break;
                    SafeEventInvoker.Invoke<ISet<NativeAdAssetType>>(handler,
                        nativeInfo.CopyAssetTypes());
                }
            }
            if (!_disposed && ReferenceEquals(Info, nativeInfo) && _loaded)
                SafeEventInvoker.Invoke(OnAdLoaded, adInfo);
            if (!ReferenceEquals(previousInfo, nativeInfo))
                DestroyInfoTextures(previousInfo);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error, bool keepsCurrentAd = false)
        {
            if (_disposed || DaroAdInstanceRegistry.IsShuttingDown) return;
            // Android retains its displayed ad on refresh failure; iOS AdMob may clear it.
            // Explicit Load already resets _loaded when it replaces the native view.
            bool retainDisplayedAd = keepsCurrentAd && _loaded;
            var previousInfo = retainDisplayedAd ? null : Info;
            if (!retainDisplayedAd)
            {
                _loaded = false;
                Info = null;
                _handle?.ClearAdChoicesScreenRect();
                _adChoicesDriver?.InvalidateSync();
            }
            DaroLog.Verbose("Native", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
            DestroyInfoTextures(previousInfo);
        }

        private static void DestroyInfoTextures(DaroNativeAdInfo? info)
        {
            if (info?.Icon != null)
                UnityEngine.Object.Destroy(info.Icon);
            if (info?.MediaImage != null)
                UnityEngine.Object.Destroy(info.MediaImage);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed || DaroAdInstanceRegistry.IsShuttingDown) return;
            DaroLog.Verbose("Native", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed || DaroAdInstanceRegistry.IsShuttingDown) return;
            DaroLog.Verbose("Native", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed || DaroAdInstanceRegistry.IsShuttingDown) return;
            DaroLog.Verbose("Native", $"FireOnAdRevenuePaid adUnit='{AdUnitId}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }

        // Per-instance sink — direct ref to its DaroNativeAd, no registry lookup.
        private sealed class InstanceSink : INativeAdEventSink
        {
            private readonly DaroNativeAd _ad;
            internal InstanceSink(DaroNativeAd ad) => _ad = ad;

            public void OnAdLoaded(DaroAdInfo adInfo, DaroNativeAdInfo nativeInfo) =>
                _ad.FireOnAdLoaded(adInfo, nativeInfo);

            public void OnAdFailedToLoad(DaroAdLoadError error, bool keepsCurrentAd = false) =>
                _ad.FireOnAdFailedToLoad(error, keepsCurrentAd);

            public void OnAdImpression(DaroAdInfo info) =>
                _ad.FireOnAdImpression(info);

            public void OnAdClicked(DaroAdInfo info) =>
                _ad.FireOnAdClicked(info);

            public void OnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue) =>
                _ad.FireOnAdRevenuePaid(info, revenue);
        }
    }
}
