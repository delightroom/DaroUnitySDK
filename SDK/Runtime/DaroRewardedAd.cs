#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Rewarded ad instance.
    /// Mirrors <see cref="DaroInterstitialAd"/> with an extra
    /// <see cref="OnEarnedReward"/> event and <see cref="SetCustomData"/> method.
    /// </summary>
    public sealed class DaroRewardedAd : IDisposable
    {
        public string  AdUnitKey  { get; }

        public event Action<DaroAdInfo>?                 OnAdLoaded;
        public event Action<DaroAdLoadError>?            OnAdFailedToLoad;
        public event Action<DaroAdInfo>?                 OnAdShown;
        public event Action<DaroAdDisplayError>?         OnAdFailedToShow;
        public event Action<DaroAdInfo>?                 OnAdClicked;
        public event Action<DaroAdInfo>?                 OnAdImpression;
        public event Action<DaroAdInfo>?                 OnAdDismissed;
        public event Action<DaroAdInfo, DaroRewardItem>? OnEarnedReward;

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

        private long _registryGeneration;

        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Construct a rewarded ad instance bound to <paramref name="adUnitKey"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitKey"/> is null, empty, or whitespace.
        /// </exception>
        public DaroRewardedAd(string adUnitKey)
        {
            if (string.IsNullOrWhiteSpace(adUnitKey))
            {
                throw new ArgumentException(
                    "adUnitKey must be a non-empty, non-whitespace string.",
                    nameof(adUnitKey));
            }

            AdUnitKey  = adUnitKey;

            _registryGeneration = DaroAdInstanceRegistry.CreateAndRegister(
                DaroAdFormat.Rewarded, AdUnitKey, this,
                () => DaroPlatform.Current.CreateRewarded(AdUnitKey));
            DaroLog.Verbose("Rewarded", $"ctor adUnit='{AdUnitKey}'");
        }

        /// <summary>
        /// Start loading a rewarded ad. note: no-op if already loading.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroRewardedAd));
            if (!AdLoadPreconditions.TryCheck(AdUnitKey, out var preconditionError))
            {
                DaroLog.Warn("Rewarded",
                    $"Load short-circuited adUnit='{AdUnitKey}' code={preconditionError!.Code} ({preconditionError.Message})");
                FireOnAdFailedToLoad(preconditionError);
                return;
            }
            DaroLog.Verbose("Rewarded", $"Load adUnit='{AdUnitKey}'");
            DaroPlatform.Current.LoadRewarded(AdUnitKey);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready. Never throws.
        /// Returns <c>false</c> on a disposed instance.
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsRewardedReady(AdUnitKey);
        }

        /// <summary>
        /// Show a previously loaded rewarded ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c>.
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroRewardedAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitKey}");
            }

            DaroLog.Verbose("Rewarded", $"Show adUnit='{AdUnitKey}'");
            DaroPlatform.Current.ShowRewarded(AdUnitKey);
        }

        /// <summary>
        /// Attach opaque string custom data forwarded to the rewarded
        /// impression. Typical use: server-side verification correlation.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void SetCustomData(string customData)
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroRewardedAd));
            if (customData == null) throw new ArgumentNullException(nameof(customData));
            DaroLog.Verbose("Rewarded", $"SetCustomData adUnit='{AdUnitKey}' len={customData.Length}");
            DaroPlatform.Current.SetRewardedCustomData(AdUnitKey, customData);
        }

        /// <summary>
        /// Idempotent dispose. Never throws.
        /// </summary>
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        ~DaroRewardedAd()
        {
            Dispose(disposing: false);
        }

        private void Dispose(bool disposing)
        {
            if (_disposed) return;
            _disposed = true;

            if (disposing)
            {
                DaroLog.Verbose("Rewarded", $"Dispose adUnit='{AdUnitKey}'");

                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdShown        = null;
                OnAdFailedToShow = null;
                OnAdClicked      = null;
                OnAdImpression   = null;
                OnAdDismissed    = null;
                OnEarnedReward   = null;
                OnAdRevenuePaid  = null;
            }

            try
            {
                DaroFinalizerRelease.RunPlatformRelease(disposing, platform =>
                    DaroAdInstanceRegistry.ReleasePlatformHandleIfCurrent(
                        DaroAdFormat.Rewarded, AdUnitKey, this, _registryGeneration,
                        () => platform.DestroyRewarded(AdUnitKey)));
            }
            catch (Exception e)
            {
                DaroLog.Warn("Rewarded",
                    $"DestroyRewarded({AdUnitKey}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdLoaded adUnit='{AdUnitKey}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdFailedToLoad adUnit='{AdUnitKey}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdShown adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdFailedToShow adUnit='{AdUnitKey}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdClicked adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdImpression adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdDismissed adUnit='{AdUnitKey}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }

        internal void FireOnEarnedReward(DaroAdInfo info, DaroRewardItem reward)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnEarnedReward adUnit='{AdUnitKey}' reward={reward.Amount} '{reward.RewardType}'");
            SafeEventInvoker.Invoke(OnEarnedReward, info, reward);
        }

        internal void FireOnAdRevenuePaid(DaroAdInfo info, DaroRevenueInfo revenue)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdRevenuePaid adUnit='{AdUnitKey}' value={revenue.Value} {revenue.CurrencyCode}");
            SafeEventInvoker.Invoke(OnAdRevenuePaid, info, revenue);
        }
    }
}
