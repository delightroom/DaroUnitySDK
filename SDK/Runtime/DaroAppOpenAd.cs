#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// App Open ad instance. See docs/overview.md for the public API contract.
    /// Identical event set to <see cref="DaroInterstitialAd"/>; no reward event.
    /// Typical use: show on foreground return via
    /// <see cref="DaroAppStateNotifier.OnAppStateChanged"/>.
    /// </summary>
    public sealed class DaroAppOpenAd : IDisposable
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
        /// Fires once per paid impression with the net (fee-adjusted) revenue
        /// reported by the mediation layer (ILRD). May lag
        /// <see cref="OnAdImpression"/> by a beat; not every impression is
        /// guaranteed a revenue report.
        /// </summary>
        public event Action<DaroAdInfo, DaroRevenueInfo>? OnAdRevenuePaid;

        internal volatile bool _disposed;

        private long _registryGeneration;

        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Construct an app open ad instance bound to <paramref name="adUnitId"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroAppOpenAd(string adUnitId, string? placement = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            Placement = placement;

            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.AppOpen, AdUnitId, this,
                () => DaroPlatform.Current.CreateAppOpen(AdUnitId, Placement));
            DaroLog.Verbose("AppOpen", $"ctor adUnit='{AdUnitId}' placement='{Placement}'");
        }

        /// <summary>
        /// Start loading an ad. §2.4 dedupe: no-op if already loading.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroAppOpenAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitId, out var preconditionError))
            {
                DaroLog.Warn("AppOpen",
                    $"Load short-circuited adUnit='{AdUnitId}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("AppOpen", $"Load adUnit='{AdUnitId}'");
            DaroPlatform.Current.LoadAppOpen(AdUnitId);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready. Never throws (§4.1).
        /// Returns <c>false</c> on a disposed instance.
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsAppOpenReady(AdUnitId);
        }

        /// <summary>
        /// Show a previously loaded app open ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c> (§4.1).
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroAppOpenAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitId}");
            }

            DaroLog.Verbose("AppOpen", $"Show adUnit='{AdUnitId}'");
            DaroPlatform.Current.ShowAppOpen(AdUnitId);
        }

        /// <summary>
        /// Idempotent dispose (§4.3). Never throws.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~DaroAppOpenAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("AppOpen", $"Dispose adUnit='{AdUnitId}'");

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
                        DaroAdFormat.AppOpen, AdUnitId, this, _registryGeneration,
                        () => platform.DestroyAppOpen(AdUnitId)));
            }
            catch (Exception e)
            {
                DaroLog.Warn("AppOpen",
                    $"DestroyAppOpen({AdUnitId}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdShown adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdFailedToShow adUnit='{AdUnitId}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdDismissed adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdRevenuePaid adUnit='{AdUnitId}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }
    }
}
