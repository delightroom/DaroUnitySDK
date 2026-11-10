#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// App Open ad instance.
    /// Identical event set to <see cref="DaroInterstitialAd"/>; no reward event.
    /// Typical use: show on foreground return via
    /// <see cref="DaroAppStateNotifier.OnAppStateChanged"/>.
    /// </summary>
    public sealed class DaroAppOpenAd : IDisposable
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

        internal volatile bool _disposed;

        private long _registryGeneration;

        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Construct an app open ad instance bound to <paramref name="adUnitKey"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitKey"/> is null, empty, or whitespace.
        /// </exception>
        public DaroAppOpenAd(string adUnitKey)
        {
            if (string.IsNullOrWhiteSpace(adUnitKey))
            {
                throw new ArgumentException(
                    "adUnitKey must be a non-empty, non-whitespace string.",
                    nameof(adUnitKey));
            }

            AdUnitKey  = adUnitKey;

            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.AppOpen, AdUnitKey, this,
                () => DaroPlatform.Current.CreateAppOpen(AdUnitKey));
            DaroLog.Verbose("AppOpen", $"ctor adUnit='{AdUnitKey}'");
        }

        /// <summary>
        /// Start loading an ad. dedupe: no-op if already loading.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroAppOpenAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitKey, out var preconditionError))
            {
                DaroLog.Warn("AppOpen",
                    $"Load short-circuited adUnit='{AdUnitKey}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("AppOpen", $"Load adUnit='{AdUnitKey}'");
            DaroPlatform.Current.LoadAppOpen(AdUnitKey);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready. Never throws.
        /// Returns <c>false</c> on a disposed instance.
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsAppOpenReady(AdUnitKey);
        }

        /// <summary>
        /// Show a previously loaded app open ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c>.
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroAppOpenAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitKey}");
            }

            DaroLog.Verbose("AppOpen", $"Show adUnit='{AdUnitKey}'");
            DaroPlatform.Current.ShowAppOpen(AdUnitKey);
        }

        /// <summary>
        /// Idempotent dispose. Never throws.
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
                DaroLog.Verbose("AppOpen", $"Dispose adUnit='{AdUnitKey}'");

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
                        DaroAdFormat.AppOpen, AdUnitKey, this, _registryGeneration,
                        () => platform.DestroyAppOpen(AdUnitKey)));
            }
            catch (Exception e)
            {
                DaroLog.Warn("AppOpen",
                    $"DestroyAppOpen({AdUnitKey}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdLoaded adUnit='{AdUnitKey}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdFailedToLoad adUnit='{AdUnitKey}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdShown adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdFailedToShow adUnit='{AdUnitKey}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdClicked adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdImpression adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdDismissed adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed) return;
            DaroLog.Verbose("AppOpen", $"FireOnAdRevenuePaid adUnit='{AdUnitKey}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }
    }
}
