#nullable enable

using System;
using Daro.Internal;
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Rewarded ad instance. See docs/overview.md for the public API contract.
    /// Mirrors <see cref="DaroInterstitialAd"/> with an extra
    /// <see cref="OnEarnedReward"/> event and <see cref="SetCustomData"/> method.
    /// </summary>
    public sealed class DaroRewardedAd : IDisposable
    {
        public string  AdUnitId  { get; }
        public string? Placement { get; }

        public event Action<DaroAdInfo>?                 OnAdLoaded;
        public event Action<DaroAdLoadError>?            OnAdFailedToLoad;
        public event Action<DaroAdInfo>?                 OnAdShown;
        public event Action<DaroAdDisplayError>?         OnAdFailedToShow;
        public event Action<DaroAdInfo>?                 OnAdClicked;
        public event Action<DaroAdInfo>?                 OnAdImpression;
        public event Action<DaroAdInfo>?                 OnAdDismissed;
        public event Action<DaroAdInfo, DaroRewardItem>? OnEarnedReward;

        /// <summary>
        /// Disposal flag. <c>volatile</c> so the §4.4 pre-enqueue and at-drain
        /// checks read the current value without a lock.
        /// </summary>
        internal volatile bool _disposed;

        internal bool IsDisposed => _disposed;

        /// <summary>
        /// Construct a rewarded ad instance bound to <paramref name="adUnitId"/>.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// Thrown if <paramref name="adUnitId"/> is null, empty, or whitespace.
        /// </exception>
        public DaroRewardedAd(string adUnitId, string? placement = null)
        {
            if (string.IsNullOrWhiteSpace(adUnitId))
            {
                throw new ArgumentException(
                    "adUnitId must be a non-empty, non-whitespace string.",
                    nameof(adUnitId));
            }

            AdUnitId  = adUnitId;
            Placement = placement;

            DaroPlatform.Current.CreateRewarded(AdUnitId, Placement);
            DaroAdInstanceRegistry.Register(DaroAdFormat.Rewarded, AdUnitId, this);
            DaroLog.Verbose("Rewarded", $"ctor adUnit='{AdUnitId}' placement='{Placement}'");
        }

        /// <summary>
        /// Start loading a rewarded ad. §2.4 note: no-op if already loading.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        public void Load()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroRewardedAd));
            DaroLog.Verbose("Rewarded", $"Load adUnit='{AdUnitId}'");
            DaroPlatform.Current.LoadRewarded(AdUnitId);
        }

        /// <summary>
        /// Query whether a previously loaded ad is ready. Never throws (§4.1).
        /// Returns <c>false</c> on a disposed instance.
        /// </summary>
        public bool IsReady()
        {
            if (_disposed) return false;
            return DaroPlatform.Current.IsRewardedReady(AdUnitId);
        }

        /// <summary>
        /// Show a previously loaded rewarded ad.
        /// </summary>
        /// <exception cref="ObjectDisposedException">
        /// Thrown when the instance has been disposed.
        /// </exception>
        /// <exception cref="InvalidOperationException">
        /// Thrown when <see cref="IsReady"/> is <c>false</c> (§4.1).
        /// </exception>
        public void Show()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(DaroRewardedAd));

            if (!IsReady())
            {
                throw new InvalidOperationException($"Ad not ready: {AdUnitId}");
            }

            DaroLog.Verbose("Rewarded", $"Show adUnit='{AdUnitId}'");
            DaroPlatform.Current.ShowRewarded(AdUnitId);
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
            DaroLog.Verbose("Rewarded", $"SetCustomData adUnit='{AdUnitId}' len={customData.Length}");
            DaroPlatform.Current.SetRewardedCustomData(AdUnitId, customData);
        }

        /// <summary>
        /// Idempotent dispose (§4.3). Never throws.
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
                DaroLog.Verbose("Rewarded", $"Dispose adUnit='{AdUnitId}'");

                OnAdLoaded       = null;
                OnAdFailedToLoad = null;
                OnAdShown        = null;
                OnAdFailedToShow = null;
                OnAdClicked      = null;
                OnAdImpression   = null;
                OnAdDismissed    = null;
                OnEarnedReward   = null;

                DaroAdInstanceRegistry.Unregister(DaroAdFormat.Rewarded, AdUnitId, this);
            }

            try
            {
                DaroPlatform.Current.DestroyRewarded(AdUnitId);
            }
            catch (Exception e)
            {
                DaroLog.Warn("Rewarded",
                    $"DestroyRewarded({AdUnitId}) threw during Dispose: {e}");
            }
        }

        // ── Internal event dispatch (called from DaroSdk event plumbing) ─────

        internal void FireOnAdLoaded(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdLoaded adUnit='{AdUnitId}' latency={info.Latency}");
            SafeEventInvoker.Invoke(OnAdLoaded, info);
        }

        internal void FireOnAdFailedToLoad(DaroAdLoadError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdFailedToLoad adUnit='{AdUnitId}' code={error.Code} raw={error.RawCode}");
            SafeEventInvoker.Invoke(OnAdFailedToLoad, error);
        }

        internal void FireOnAdShown(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdShown adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdShown, info);
        }

        internal void FireOnAdFailedToShow(DaroAdDisplayError error)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdFailedToShow adUnit='{AdUnitId}' code={error.Code}");
            SafeEventInvoker.Invoke(OnAdFailedToShow, error);
        }

        internal void FireOnAdClicked(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdClicked adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdClicked, info);
        }

        internal void FireOnAdImpression(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdImpression adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdImpression, info);
        }

        internal void FireOnAdDismissed(DaroAdInfo info)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnAdDismissed adUnit='{AdUnitId}'");
            SafeEventInvoker.Invoke(OnAdDismissed, info);
        }

        internal void FireOnEarnedReward(DaroAdInfo info, DaroRewardItem reward)
        {
            if (_disposed) return;
            DaroLog.Verbose("Rewarded", $"FireOnEarnedReward adUnit='{AdUnitId}' reward={reward.Amount} '{reward.RewardType}'");
            SafeEventInvoker.Invoke(OnEarnedReward, info, reward);
        }
    }
}
