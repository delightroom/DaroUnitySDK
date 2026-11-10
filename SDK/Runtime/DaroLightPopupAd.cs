#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Light Popup ad instance rendered and managed by the native SDK.
    /// From the consumer's perspective the shape is a fullscreen ad — Load → Show → Dismiss with 7 lifecycle
    /// events, mirroring <see cref="DaroInterstitialAd"/>.
    /// </summary>
    /// <remarks>
    /// One instance per <c>adUnitKey</c>; duplicate construction replaces the
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
        public string  AdUnitKey  { get; }

        public event Action<DaroAdInfo>?         OnAdLoaded;
        public event Action<DaroAdLoadError>?    OnAdFailedToLoad;
        public event Action<DaroAdInfo>?         OnAdShown;
        public event Action<DaroAdDisplayError>? OnAdFailedToShow;
        public event Action<DaroAdInfo>?         OnAdClicked;
        public event Action<DaroAdInfo>?         OnAdImpression;
        public event Action<DaroAdInfo>?         OnAdDismissed;

        /// <summary>
        /// Fires once per paid impression with the revenue reported by the
        /// mediation layer (ILRD). May lag <see cref="OnAdImpression"/> by a
        /// beat; not every impression is guaranteed a revenue report.
        /// </summary>
        public event Action<DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid;

        /// <summary>
        /// Disposal flag. <c>volatile</c> so the pre-enqueue and at-drain
        /// checks read the current value without a lock.
        /// </summary>
        internal volatile bool _disposed;

        /// <summary>
        /// Is this instance disposed? Exposed for the registry / event
        /// routing layer to skip firing against dead instances.
        /// </summary>
        internal bool IsDisposed => _disposed;

        private long _registryGeneration;

        /// <summary>
        /// Construct a Light Popup ad instance bound to <paramref name="adUnitKey"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitKey"/> is null, empty, or whitespace.
        /// </exception>
        public DaroLightPopupAd(
            string adUnitKey,
            DaroLightPopupAdOptions? options = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitKey))
            {
                throw new ArgumentException(
                    "adUnitKey must be a non-empty, non-whitespace string.",
                    nameof(adUnitKey));
            }

            AdUnitKey  = adUnitKey;

            // Keep null so native SDKs retain their current default appearance.
            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.LightPopup,
                AdUnitKey,
                this,
                () => DaroPlatform.Current.CreateLightPopup(
                    AdUnitKey, options));
            DaroLog.Verbose("LightPopup", $"ctor adUnit='{AdUnitKey}' optionsProvided={options != null}");
        }

        /// <summary>
        /// Start loading an ad. Post-init failures (e.g. <c>SdkNotReady</c>)
        /// fire <see cref="OnAdFailedToLoad"/> rather than throwing.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroLightPopupAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitKey, out var preconditionError))
            {
                DaroLog.Warn("LightPopup",
                    $"Load short-circuited adUnit='{AdUnitKey}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("LightPopup", $"Load adUnit='{AdUnitKey}'");
            DaroPlatform.Current.LoadLightPopup(AdUnitKey);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready to show.
        /// Returns <c>false</c> if this instance has been disposed.
        /// Never throws.
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsLightPopupReady(AdUnitKey);
        }

        /// <summary>
        /// Show a previously loaded ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c> — show-before-ready
        /// is a call-ordering bug, not a runtime ad-network failure.
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroLightPopupAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitKey}");
            }

            DaroLog.Verbose("LightPopup", $"Show adUnit='{AdUnitKey}'");
            DaroPlatform.Current.ShowLightPopup(AdUnitKey);
        }

        /// <summary>
        /// Idempotent dispose. Never throws (IDisposable contract). Second and subsequent calls are no-ops.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        /// <summary>
        /// Finalizer backstop: if the consumer drops the reference
        /// without calling <see cref="Dispose"/> we still release the native
        /// handle. Event-handler nulling is skipped on the finalizer thread
        /// (unsafe in finalizers).
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
                DaroLog.Verbose("LightPopup", $"Dispose adUnit='{AdUnitKey}'");

                // Null the event backing fields to release consumer delegate
                // refs. Handlers that are already captured by an in-flight
                // MainThreadDispatcher closure still run to completion — that's
                // the reentrancy contract.
                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdShown        = null;
                OnAdFailedToShow = null;
                OnAdClicked      = null;
                OnAdImpression   = null;
                OnAdDismissed    = null;
                OnAdRevenuePaid  = null;
            }

            try
            {
                DaroFinalizerRelease.RunPlatformRelease(disposing, platform =>
                    DaroAdInstanceRegistry.ReleasePlatformHandleIfCurrent(
                        DaroAdFormat.LightPopup,
                        AdUnitKey,
                        this,
                        _registryGeneration,
                        () => platform.DestroyLightPopup(AdUnitKey)));
            }
            catch (Exception e)
            {
                DaroLog.Warn("LightPopup",
                    $"DestroyLightPopup({AdUnitKey}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────
        //
        // These methods run on the Unity main thread — they're invoked from a
        // MainThreadDispatcher.Enqueue closure inside the platform layer.
        // Each re-checks `_disposed` at drain time.

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdLoaded adUnit='{AdUnitKey}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdFailedToLoad adUnit='{AdUnitKey}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdShown adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdFailedToShow adUnit='{AdUnitKey}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdClicked adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdImpression adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdRevenuePaid adUnit='{AdUnitKey}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("LightPopup", $"FireOnAdDismissed adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }
    }
}
