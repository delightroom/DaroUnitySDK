#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Light Popup ad instance. daro-m wraps the format in a modal Dialog with
    /// an 8-second auto-dismiss timer; from the consumer's perspective the
    /// shape is a fullscreen ad — Load → Show → Dismiss with 7 lifecycle
    /// events, mirroring <see cref="DaroInterstitialAd"/>.
    /// </summary>
    /// <remarks>
    /// One instance per <c>adUnitId</c>; duplicate construction replaces the
    /// prior instance (platform layer destroys + recreates the native handle,
    /// registry overwrites the mapping).
    ///
    /// Color / label customization is supplied via
    /// <see cref="DaroLightPopupAdOptions"/>. Options are baked at construction
    /// time and forwarded to the native shim once — post-construction mutations
    /// of the options object are not propagated.
    /// </remarks>
    public sealed class DaroLightPopupAd : IDisposable
    {
        public string  AdUnitId  { get; }
        public string? Placement { get; }

        public event Action<DaroAdInfo>?         OnAdLoaded;
        public event Action<DaroAdLoadError>?    OnAdFailedToLoad;
        public event Action<DaroAdInfo>?         OnAdShown;
        public event Action<DaroAdDisplayError>? OnAdFailedToShow;
        public event Action<DaroAdInfo>?         OnAdClicked;
        public event Action<DaroAdInfo>?         OnAdImpression;
        public event Action<DaroAdInfo>?         OnAdDismissed;

        /// <summary>
        /// Disposal flag. <c>volatile</c> so the §4.4 pre-enqueue and at-drain
        /// checks read the current value without a lock.
        /// </summary>
        internal volatile bool _disposed;

        /// <summary>
        /// Is this instance disposed? Exposed for the registry / event
        /// routing layer to skip firing against dead instances.
        /// </summary>
        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Construct a Light Popup ad instance bound to <paramref name="adUnitId"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroLightPopupAd(
            string adUnitId,
            DaroLightPopupAdOptions? options = null,
            string? placement = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            Placement = placement;

            // Null options → daro-m defaults via field initializers. Platform
            // impls always receive non-null.
            DaroPlatform.Current.CreateLightPopup(
                AdUnitId, Placement, options ?? new DaroLightPopupAdOptions());
            DaroAdInstanceRegistry.Register(DaroAdFormat.LightPopup, AdUnitId, this);
            DaroLog.Verbose("LightPopup", $"ctor adUnit='{AdUnitId}' placement='{Placement}' optionsProvided={options != null}");
        }

        /// <summary>
        /// Start loading an ad. Post-init failures (e.g. <c>SdkNotReady</c>)
        /// fire <see cref="OnAdFailedToLoad"/> rather than throwing (§4.1).
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroLightPopupAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitId, out var preconditionError))
            {
                DaroLog.Warn("LightPopup",
                    $"Load short-circuited adUnit='{AdUnitId}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("LightPopup", $"Load adUnit='{AdUnitId}'");
            DaroPlatform.Current.LoadLightPopup(AdUnitId);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready to show.
        /// Returns <c>false</c> if this instance has been disposed.
        /// Never throws (§4.1).
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsLightPopupReady(AdUnitId);
        }

        /// <summary>
        /// Show a previously loaded ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c> — show-before-ready
        /// is a call-ordering bug, not a runtime ad-network failure (§4.1).
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroLightPopupAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitId}");
            }

            DaroLog.Verbose("LightPopup", $"Show adUnit='{AdUnitId}'");
            DaroPlatform.Current.ShowLightPopup(AdUnitId);
        }

        /// <summary>
        /// Idempotent dispose (§4.3). Never throws (IDisposable contract +
        /// §4.1). Second and subsequent calls are no-ops.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer backstop (§4.3): if the consumer drops the reference
        /// without calling <see cref="Dispose"/> we still release the native
        /// handle. Event-handler nulling is skipped on the finalizer thread
        /// (unsafe per §4.3).
        /// </summary>
        ~DaroLightPopupAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("LightPopup", $"Dispose adUnit='{AdUnitId}'");

                // Null the event backing fields to release consumer delegate
                // refs. Handlers that are already captured by an in-flight
                // MainThreadDispatcher closure still run to completion — that's
                // the §6.6 reentrancy contract.
                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdShown        = null;
                OnAdFailedToShow = null;
                OnAdClicked      = null;
                OnAdImpression   = null;
                OnAdDismissed    = null;

                DaroAdInstanceRegistry.Unregister(DaroAdFormat.LightPopup, AdUnitId, this);
            }

            try
            {
                DaroPlatform.Current.DestroyLightPopup(AdUnitId);
            }
            catch (Exception e)
            {
                DaroLog.Warn("LightPopup",
                    $"DestroyLightPopup({AdUnitId}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────
        //
        // These methods run on the Unity main thread — they're invoked from a
        // MainThreadDispatcher.Enqueue closure inside the platform layer.
        // Each re-checks `_disposed` at drain time per §4.4's at-drain guard.

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdShown adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdFailedToShow adUnit='{AdUnitId}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdDismissed adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }
    }
}
