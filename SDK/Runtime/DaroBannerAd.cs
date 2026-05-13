#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Banner ad instance — always-on overlay. v1 ships standard sizes (320×50,
    /// 300×250) at 6 gravity-anchored positions; mediation manages auto-refresh
    /// internally. See sketch-banner-android.md §2-3.
    /// </summary>
    /// <remarks>
    /// <para>One instance per <c>adUnitId</c>; duplicate construction destroys +
    /// replaces the prior instance (KU-1, mirrors v1 Interstitial replace rule).
    /// The first instance becomes a stale C# object — caller must not retain it.</para>
    ///
    /// <para>Lifecycle: <c>Load → Show → Hide → Show → Destroy</c> cycle is valid.
    /// <c>Show()</c> requires prior <c>Load()</c> — does NOT implicit-load (KU-4).
    /// <c>Hide()</c> removes the view but keeps the ad loaded; subsequent
    /// <c>Show()</c> re-displays without a network round-trip.</para>
    ///
    /// <para>Ownership: while a <see cref="DaroBannerAd"/> instance is alive,
    /// the underlying native banner view stays attached to the host view tree
    /// (Android: Activity decor; iOS: GLView controller). Switching the host
    /// app's screen / scene does NOT auto-detach it, and mediation auto-refresh
    /// keeps firing impressions on the still-attached view. Consumers must
    /// explicitly call <see cref="Hide"/> (pause + can re-Show later) or
    /// <see cref="Dispose"/> (permanent release) at the lifecycle boundary
    /// where the banner should disappear — typically the screen's back / unload
    /// handler.</para>
    ///
    /// <para>Events: 6 (KU-5). <c>OnAdShown</c> fires synchronously inside
    /// <c>Show()</c> — there is no native callback for view visibility, but the
    /// consumer needs an observable signal that the overlay is live (AdMob Unity
    /// SDK precedent). <c>OnAdFailedToShow</c> 부재 — Banner has no show-failure
    /// concept (C# pre-checks block invalid Show calls). <c>OnAdRefreshed</c>
    /// 부재 — DaroAdViewListener has no refresh callback.</para>
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

        /// <summary>Fires synchronously from <see cref="Show"/> — not from native.</summary>
        public event Action<DaroAdInfo>?      OnAdShown;
        public event Action<DaroAdInfo>?      OnAdClicked;
        public event Action<DaroAdInfo>?      OnAdImpression;
        public event Action<DaroAdInfo>?      OnAdHidden;

        /// <summary>
        /// Disposal flag. <c>volatile</c> mirrors the v1 §4.4 pre-enqueue +
        /// at-drain checks pattern.
        /// </summary>
        internal volatile bool _disposed;

        internal bool IsDisposed => _disposed;

        // _loaded 는 main-thread only access — FireOnAdLoaded(set) / Show()(read) /
        // IsReady()(read) 모두 Unity main thread 에서 실행. volatile 불필요.
        private bool _loaded;

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
            // (KU-1); registry mirrors that by overwriting the mapping.
            DaroPlatform.Current.CreateBanner(AdUnitId, Placement);
            DaroAdInstanceRegistry.Register(DaroAdFormat.Banner, AdUnitId, this);
            DaroLog.Verbose("Banner", $"ctor adUnit='{AdUnitId}' size={Size} position={Position} placement='{Placement}'");
        }

        /// <summary>
        /// Start loading. Size is passed because <c>DaroBannerAdView</c> bakes
        /// it into view construction at native side. Post-init failures fire
        /// <see cref="OnAdFailedToLoad"/> rather than throwing (v1 §4.1).
        /// </summary>
        /// <exception cref="ObjectDisposedException"/>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroBannerAd));
            DaroLog.Verbose("Banner", $"Load adUnit='{AdUnitId}' size={Size}");
            DaroPlatform.Current.LoadBanner(AdUnitId, Size);
        }

        /// <summary>
        /// True if a loaded ad is ready to show. Never throws; false if disposed.
        /// </summary>
        public bool IsReady() => _loaded && !_disposed;

        /// <summary>
        /// Place the banner overlay on screen. Fires <see cref="OnAdShown"/>
        /// synchronously after the platform call (no native show callback exists).
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
            DaroPlatform.Current.ShowBanner(AdUnitId);

            // Sync fire — no native source for OnAdShown on banner.
            SafeEventInvoker.Invoke(OnAdShown,
                new DaroAdInfo(DaroAdFormat.Banner, AdUnitId, latency: null));
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

                DaroAdInstanceRegistry.Unregister(DaroAdFormat.Banner, AdUnitId, this);
            }

            try
            {
                DaroPlatform.Current.DestroyBanner(AdUnitId);
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
            _loaded = true;
            DaroLog.Verbose("Banner", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            _loaded = false;
            DaroLog.Verbose("Banner", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        // No FireOnAdShown — fires from Show() directly.

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Banner", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Banner", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdHidden(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Banner", $"FireOnAdHidden adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdHidden, info);
        }
    }
}
