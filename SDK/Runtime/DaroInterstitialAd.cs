#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Interstitial ad instance. See docs/overview.md for the public API contract.
    /// One instance per <c>adUnitId</c>; duplicate construction replaces the
    /// prior instance (platform layer destroys + recreates the native handle,
    /// registry overwrites the mapping).
    /// </summary>
    public sealed class DaroInterstitialAd : IDisposable
    {
        public string  AdUnitId  { get; }

        public event Action<DaroAdInfo>?         OnAdLoaded;
        public event Action<DaroAdLoadError>?    OnAdFailedToLoad;
        public event Action<DaroAdInfo>?         OnAdShown;
        public event Action<DaroAdDisplayError>? OnAdFailedToShow;
        public event Action<DaroAdInfo>?         OnAdClicked;
        public event Action<DaroAdInfo>?         OnAdImpression;
        public event Action<DaroAdInfo>?         OnAdDismissed;

        /// <summary>
        /// Fires once per paid impression with the net (fee-adjusted) revenue
        /// reported by the mediation layer (ILRD). May lag
        /// <see cref="OnAdImpression"/> by a beat; not every impression is
        /// guaranteed a revenue report.
        /// </summary>
        public event Action<DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid;

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

        private long _registryGeneration;

        /// <summary>
        /// Construct an interstitial ad instance bound to <paramref name="adUnitId"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroInterstitialAd(string adUnitId)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;

            // Platform handles native create + the "replace prior instance"
            // rule (§2.4); registry serializes same-adUnit create/destroy so
            // stale finalizers cannot destroy the new platform state.
            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.Interstitial, AdUnitId, this,
                () => DaroPlatform.Current.CreateInterstitial(AdUnitId));
            DaroLog.Verbose("Interstitial", $"ctor adUnit='{AdUnitId}'");
        }

        /// <summary>
        /// Start loading an ad. No-op silently if already loading (dedupe
        /// happens inside the platform layer per §2.4). Post-init failures
        /// (e.g. <c>SdkNotReady</c>) fire <see cref="OnAdFailedToLoad"/>
        /// rather than throwing (§4.1).
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroInterstitialAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitId, out var preconditionError))
            {
                DaroLog.Warn("Interstitial",
                    $"Load short-circuited adUnit='{AdUnitId}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("Interstitial", $"Load adUnit='{AdUnitId}'");
            DaroPlatform.Current.LoadInterstitial(AdUnitId);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready to show.
        /// Returns <c>false</c> if this instance has been disposed.
        /// Never throws (§4.1).
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsInterstitialReady(AdUnitId);
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
            if (_disposed) throw new ObjectDisposedException(nameof(DaroInterstitialAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitId}");
            }

            DaroLog.Verbose("Interstitial", $"Show adUnit='{AdUnitId}'");
            DaroPlatform.Current.ShowInterstitial(AdUnitId);
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
        ~DaroInterstitialAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("Interstitial", $"Dispose adUnit='{AdUnitId}'");

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
                OnAdRevenuePaid  = null;
            }

            // Destroy the native handle. Wrapped in try/catch because Dispose
            // must not throw (§4.1); platform-level faults are logged and swallowed.
            try
            {
                DaroFinalizerRelease.RunPlatformRelease(disposing, platform =>
                    DaroAdInstanceRegistry.ReleasePlatformHandleIfCurrent(
                        DaroAdFormat.Interstitial, AdUnitId, this, _registryGeneration,
                        () => platform.DestroyInterstitial(AdUnitId)));
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
                        $"[Daro:Interstitial] DestroyInterstitial({AdUnitId}) threw during Dispose: {e}");
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
            DaroLog.Verbose("Interstitial", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdShown adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdFailedToShow adUnit='{AdUnitId}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdDismissed adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed) return;
            DaroLog.Verbose("Interstitial", $"FireOnAdRevenuePaid adUnit='{AdUnitId}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }
    }
}
