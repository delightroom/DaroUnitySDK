#nullable enable

using System;
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
        public string? Placement { get; }

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
        public event Action<DaroAdInfo>?      OnAdLoaded;
        public event Action<DaroAdLoadError>? OnAdFailedToLoad;
        public event Action<DaroAdInfo>?      OnAdImpression;
        public event Action<DaroAdInfo>?      OnAdClicked;

        internal volatile bool _disposed;
        internal bool IsDisposed => _disposed;

        // Main-thread-only — set in FireOnAdLoaded, read by IsReady. No volatile needed.
        private bool _loaded;

        private INativeAdHandle? _handle;

        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroNativeAd(string adUnitId, string? placement = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            Placement = placement;

            // Sink holds a direct reference to this instance — routing is
            // per-instance, no registry lookup needed.
            var sink = new InstanceSink(this);
            _handle  = DaroPlatform.Current.CreateNativeAdHandle(adUnitId, placement, sink);
            DaroLog.Verbose("Native", $"ctor adUnit='{AdUnitId}' placement='{Placement}'");
        }

        /// <summary>
        /// Start loading. Post-init failures fire <see cref="OnAdFailedToLoad"/>
        /// rather than throwing.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroNativeAd));
            // Clamp to ≥1 — Glide rejects 0-dim views, our shim host needs >0.
            var w = Mathf.Max(1, IconSize.x);
            var h = Mathf.Max(1, IconSize.y);
            DaroLog.Verbose("Native", $"Load adUnit='{AdUnitId}' iconSize={w}x{h}");
            _handle!.Load(w, h);
        }

        /// <summary>
        /// Mark the ad as visible (impression signal). Slot path:
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
        /// Finalizer backstop: releases the handle if the consumer dropped
        /// the reference without calling <see cref="Dispose"/>.
        /// </summary>
        ~DaroNativeAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("Native", $"Dispose adUnit='{AdUnitId}'");

                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdImpression   = null;
                OnAdClicked      = null;

                // Texture2D.Destroy is main-thread-only — only safe here in the
                // disposing branch. Finalizer path skips it.
                if (Info?.Icon != null)
                    UnityEngine.Object.Destroy(Info.Icon);
                if (Info?.MediaImage != null)
                    UnityEngine.Object.Destroy(Info.MediaImage);

                Info = null;
            }

            try
            {
                _handle?.Dispose();
            }
            catch (Exception e)
            {
                DaroLog.Warn("Native",
                    $"DaroNativeAd({AdUnitId}) handle Dispose threw: {e}");
            }
            _handle = null;
        }

        // ── Internal Fire* (called from sink on Unity main thread) ───────

        internal void FireOnAdLoaded(DaroAdInfo adInfo, DaroNativeAdInfo nativeInfo)
        {
            if (_disposed) return;
            _loaded = true;
            Info    = nativeInfo;
            DaroLog.Verbose("Native", $"FireOnAdLoaded adUnit='{AdUnitId}' title='{nativeInfo.Title}' cta='{nativeInfo.CallToAction}' icon={(nativeInfo.Icon != null ? "present" : "null")} latency={adInfo.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, adInfo);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            _loaded = false;
            // Clear stale Info from a prior successful load so a failed reload
            // doesn't leave the publisher reading the previous ad's assets.
            Info = null;
            DaroLog.Verbose("Native", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Native", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        // Per-instance sink — direct ref to its DaroNativeAd, no registry lookup.
        private sealed class InstanceSink : INativeAdEventSink
        {
            private readonly DaroNativeAd _ad;
            internal InstanceSink(DaroNativeAd ad) => _ad = ad;

            public void OnAdLoaded(DaroAdInfo adInfo, DaroNativeAdInfo nativeInfo) =>
                _ad.FireOnAdLoaded(adInfo, nativeInfo);

            public void OnAdFailedToLoad(DaroAdLoadError error) =>
                _ad.FireOnAdFailedToLoad(error);

            public void OnAdImpression(DaroAdInfo info) =>
                _ad.FireOnAdImpression(info);

            public void OnAdClicked(DaroAdInfo info) =>
                _ad.FireOnAdClicked(info);
        }
    }
}
