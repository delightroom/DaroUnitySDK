#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Banner ad instance — native view overlay. v1 ships standard sizes
    /// (320×50, 300×250) at 6 gravity-anchored positions; mediation manages
    /// auto-refresh internally. See sketch-banner-android.md §2-3.
    /// </summary>
    /// <remarks>
    /// <para>One instance per <c>adUnitId</c>; duplicate construction destroys +
    /// replaces the prior instance (KU-1, mirrors v1 Interstitial replace rule).
    /// The first instance becomes a stale C# object — caller must not retain it.</para>
    ///
    /// <para>Lifecycle: <c>Load → Hide → Show → Destroy</c> cycle is valid.
    /// <c>Load()</c> starts loading and, on success, displays the banner by
    /// default. <c>Show()</c> requires prior <c>Load()</c> and is primarily for
    /// re-displaying after <c>Hide()</c>; it does NOT implicit-load (KU-4).
    /// <c>Hide()</c> removes the view but keeps the ad loaded; subsequent
    /// <c>Show()</c> re-displays without a network round-trip.</para>
    ///
    /// <para>Ownership: after a successful <see cref="Load"/>, the underlying
    /// native banner view is attached to the host view tree (Android: Activity
    /// decor; iOS: GLView controller) until <see cref="Hide"/> or
    /// <see cref="Dispose"/>. Switching the host app's screen / scene does NOT
    /// auto-detach it, and mediation auto-refresh keeps firing impressions on
    /// the attached view. Consumers must explicitly call <see cref="Hide"/>
    /// (pause + can re-Show later) or
    /// <see cref="Dispose"/> (permanent release) at the lifecycle boundary
    /// where the banner should disappear — typically the screen's back / unload
    /// handler.</para>
    ///
    /// <para>Events: 6 (KU-5). <c>OnAdShown</c> fires once after a successful
    /// <c>Load()</c> displays the banner, and once after each <c>Hide()</c> →
    /// <c>Show()</c> re-display. There is no native callback for view
    /// visibility, but the consumer needs an observable signal that the overlay
    /// is live. <c>OnAdFailedToShow</c> 부재 — Banner has no show-failure concept
    /// (C# pre-checks block invalid Show calls). <c>OnAdRefreshed</c> 부재 —
    /// DaroAdViewListener has no refresh callback.</para>
    /// </remarks>
    public sealed class DaroBannerAd : IDisposable
    {
        // ── Identity ─────────────────────────────────────────────────────
        public string             AdUnitId  { get; }
        public string?            Placement { get; }
        public DaroBannerSize     Size      { get; }
        public DaroBannerPosition Position  { get; private set; }

        // ── Lifecycle events ──────────────────────────────────────────────
        public event Action<DaroAdInfo>?      OnAdLoaded;
        public event Action<DaroAdLoadError>? OnAdFailedToLoad;

        /// <summary>
        /// Fires after <see cref="Load"/> successfully displays the banner, and
        /// after <see cref="Show"/> re-displays it following <see cref="Hide"/>.
        /// Not sourced from native.
        /// </summary>
        public event Action<DaroAdInfo>?      OnAdShown;
        public event Action<DaroAdInfo>?      OnAdClicked;
        public event Action<DaroAdInfo>?      OnAdImpression;
        public event Action<DaroAdInfo>?      OnAdHidden;

        /// <summary>
        /// Fires once per paid impression with the net (fee-adjusted) revenue
        /// reported by the mediation layer (ILRD). Auto-refresh banners fire
        /// this on every refreshed impression. May lag
        /// <see cref="OnAdImpression"/> by a beat; not every impression is
        /// guaranteed a revenue report.
        /// </summary>
        public event Action<DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid;

        /// <summary>
        /// Disposal flag. <c>volatile</c> mirrors the v1 §4.4 pre-enqueue +
        /// at-drain checks pattern.
        /// </summary>
        internal volatile bool _disposed;

        private long _registryGeneration;

        internal bool IsDisposed => _disposed;

        // Main-thread only access — FireOnAdLoaded(set) / Show()(read) /
        // IsReady()(read) 모두 Unity main thread 에서 실행. volatile 불필요.
        private bool _loaded;
        private bool _visibleIntent;
        private bool _shownReported;
        private bool _dispatchingLoad;
        private bool _hiddenReportable;

        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroBannerAd(
            string adUnitId,
            DaroBannerSize size = DaroBannerSize.Standard,
            DaroBannerPosition position = DaroBannerPosition.BottomCenter,
            string? placement = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            Size      = size;
            Position  = position;
            Placement = placement;

            // Platform handles native create + the "replace prior instance" rule
            // (KU-1); registry serializes same-adUnit create/destroy.
            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.Banner, AdUnitId, this,
                () => DaroPlatform.Current.CreateBanner(AdUnitId, Placement));
            DaroLog.Verbose("Banner", $"ctor adUnit='{AdUnitId}' size={Size} position={Position} placement='{Placement}'");
        }

        /// <summary>
        /// Start loading and display the banner by default once loading
        /// succeeds. Size is passed because <c>DaroBannerAdView</c> bakes it
        /// into view construction at native side. Post-init failures fire
        /// <see cref="OnAdFailedToLoad"/> rather than throwing (v1 §4.1).
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroBannerAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitId, out var preconditionError))
            {
                DaroLog.Warn("Banner",
                    $"Load short-circuited adUnit='{AdUnitId}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("Banner", $"Load adUnit='{AdUnitId}' size={Size}");
            _loaded = false;
            _visibleIntent = true;
            _shownReported = false;
            DaroPlatform.Current.LoadBanner(AdUnitId, Size);
        }

        /// <summary>
        /// True if a loaded ad is ready to show. Never throws; false if disposed.
        /// </summary>
        public bool IsReady() => _loaded && !_disposed;

        /// <summary>
        /// Re-display the banner overlay after <see cref="Hide"/>. If the
        /// banner is already visible, this is a no-op.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c> — call-ordering bug
        /// (v1 §4.1).
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroBannerAd));
            if (!_loaded)
                throw new InvalidOperationException($"Banner ad not ready: {AdUnitId}");

            DaroLog.Verbose("Banner", $"Show adUnit='{AdUnitId}' position={Position}");
            if (_visibleIntent)
            {
                DaroLog.Verbose("Banner", $"Show no-op adUnit='{AdUnitId}' already visible");
                if (!_dispatchingLoad) FireOnAdShownIfNeeded();
                return;
            }

            _visibleIntent = true;
            DaroPlatform.Current.ShowBanner(AdUnitId);
            if (!_dispatchingLoad) FireOnAdShownIfNeeded();
        }

        /// <summary>
        /// Remove the overlay without unloading. <see cref="Show"/> re-displays
        /// without a new <see cref="Load"/>. <see cref="OnAdHidden"/> fires
        /// asynchronously via platform routing.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void Hide()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroBannerAd));
            DaroLog.Verbose("Banner", $"Hide adUnit='{AdUnitId}'");
            _visibleIntent = false;
            _shownReported = false;
            DaroPlatform.Current.HideBanner(AdUnitId);
        }

        /// <summary>
        /// Update banner position. Effective immediately if shown; applied on
        /// next <see cref="Show"/> otherwise.
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void SetPosition(DaroBannerPosition position)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroBannerAd));
            DaroLog.Verbose("Banner", $"SetPosition adUnit='{AdUnitId}' position={position}");
            Position = position;
            DaroPlatform.Current.SetBannerPosition(AdUnitId, position);
        }

        /// <summary>
        /// The native banner's actual on-screen rectangle in Unity screen pixels
        /// (bottom-left origin, same convention as <see cref="Screen.safeArea"/>),
        /// or <c>null</c> if the banner has not laid out yet, is hidden, or the
        /// instance is disposed.
        /// </summary>
        /// <remarks>
        /// <para><b>Timing</b>: the native banner may lay out a frame or two
        /// AFTER <see cref="Load"/> succeeds or <see cref="Show"/> returns, so
        /// this can be <c>null</c> inside an <see cref="OnAdShown"/> handler.
        /// Poll across a few frames until it returns non-null rather than
        /// reading it once on the shown callback.</para>
        ///
        /// <para>This reports the REAL measured footprint — including the
        /// platform's safe-area / system-bar / gesture inset — so a consumer can
        /// reserve layout space that aligns with where the banner actually
        /// renders. Unlike <see cref="Screen.safeArea"/>, the rect accounts for
        /// the Android system-bar / gesture inset that MAX positions the banner
        /// within (the inset Unity's safe area omits).</para>
        ///
        /// <para>Platform divergence is intentional and not normalized: iOS pins
        /// the banner to its nominal size (320×50 / 300×250 pt) regardless of
        /// device, while Android reports the actually-laid-out view rect (e.g.
        /// 728×90 on tablets). Do not assume both platforms return the same
        /// value. The rect can change on rotation / resize — re-query afterward.
        /// In the Editor this returns a non-authoritative nominal rect derived
        /// from <see cref="Screen.safeArea"/>.</para>
        /// </remarks>
        public Rect? GetScreenRect()
        {
            if (_disposed) return null;
            if (!_visibleIntent) return null;
            return DaroPlatform.Current.TryGetBannerScreenRect(AdUnitId, out var rect)
                ? rect
                : (Rect?)null;
        }

        // ── IDisposable ───────────────────────────────────────────────────

        /// <summary>
        /// Idempotent dispose (KU-8 — equivalent to <c>Destroy()</c>). Never throws
        /// (IDisposable contract + v1 §4.1). Second and subsequent calls are no-ops.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer backstop (v1 §4.3): if the consumer drops the reference
        /// without calling <see cref="Dispose"/> we still release the native handle.
        /// </summary>
        ~DaroBannerAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("Banner", $"Dispose adUnit='{AdUnitId}'");

                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdShown        = null;
                OnAdClicked      = null;
                OnAdImpression   = null;
                OnAdHidden       = null;
                OnAdRevenuePaid  = null;
                _visibleIntent   = false;
                _shownReported   = false;
                _dispatchingLoad = false;
                _hiddenReportable = false;
            }

            try
            {
                DaroFinalizerRelease.RunPlatformRelease(disposing, platform =>
                    DaroAdInstanceRegistry.ReleasePlatformHandleIfCurrent(
                        DaroAdFormat.Banner, AdUnitId, this, _registryGeneration,
                        () => platform.DestroyBanner(AdUnitId)));
            }
            catch (Exception e)
            {
                // Finalizer-safe logging: inline gate (volatile DaroLogLevel
                // read is atomic + safe from GC thread) skips string
                // interpolation when silenced. DaroLog.WarnFinalizerSafe
                // delegates to Debug.LogWarning, which Unity documents as
                // finalizer-safe — guarantee preserved by delegation.
                if (DaroSdk.LogLevel >= DaroLogLevel.Warn)
                    DaroLog.WarnFinalizerSafe(
                        $"[Daro:Banner] DestroyBanner({AdUnitId}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────
        //
        // All Fire* run on the Unity main thread — invoked from inside a
        // MainThreadDispatcher.Enqueue closure in the platform layer. Each
        // re-checks _disposed at drain time per v1 §4.4's at-drain guard.

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            bool suppressHiddenReloadEvent = _loaded && !_visibleIntent;
            _loaded = true;
            if (suppressHiddenReloadEvent)
            {
                DaroLog.Verbose("Banner",
                    $"Suppress hidden refresh OnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
                return;
            }
            DaroLog.Verbose("Banner", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            _dispatchingLoad = true;
            try
            {
                SafeEventInvoker.Invoke(OnAdLoaded, info);
            }
            finally
            {
                _dispatchingLoad = false;
            }
            FireOnAdShownIfNeeded();
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            _loaded = false;
            _visibleIntent = false;
            _shownReported = false;
            _dispatchingLoad = false;
            _hiddenReportable = false;
            DaroLog.Verbose("Banner", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        private void FireOnAdShownIfNeeded()
        {
            if (_disposed || !_loaded || !_visibleIntent || _shownReported) return;

            _shownReported = true;
            _hiddenReportable = true;
            DaroLog.Verbose("Banner", $"FireOnAdShown adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdShown,
                new DaroAdInfo(DaroAdFormat.Banner, AdUnitId, latency: null));
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed || !_loaded || !_visibleIntent) return;
            DaroLog.Verbose("Banner", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed || !_loaded || !_visibleIntent) return;
            DaroLog.Verbose("Banner", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed || !_loaded) return;
            DaroLog.Verbose("Banner", $"FireOnAdRevenuePaid adUnit='{AdUnitId}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }

        internal void FireOnAdHidden(DaroAdInfo info)
        {
            if (_disposed || !_hiddenReportable || _visibleIntent) return;
            _hiddenReportable = false;
            DaroLog.Verbose("Banner", $"FireOnAdHidden adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdHidden, info);
        }
    }
}
