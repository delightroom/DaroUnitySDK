#nullable enable

using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Short-circuit gate for <c>Daro*Ad.Load()</c>. Catches the two cheap
    /// failure cases — SDK not initialized, no network reachable — and lets
    /// the SDK synthesize an immediate <c>OnAdFailedToLoad</c> instead of
    /// delegating to native, where the underlying mediation can spend up to
    /// several minutes on internal retries before reporting the same failure
    /// (mediation-internal retry budget can turn a single offline Load into
    /// a multi-minute wait).
    ///
    /// Gate fires uniformly across all ad formats (Interstitial / Rewarded /
    /// AppOpen / LightPopup / Banner) and on every platform (Android / iOS /
    /// Editor mock). The check is intentionally coarse — it does not probe
    /// real reachability, only the OS-reported network-interface state.
    /// Captive portals or DNS failures are out of scope here and continue to
    /// surface via native callbacks as before.
    /// </summary>
    internal static class AdLoadPreconditions
    {
        /// <summary>
        /// Returns <c>true</c> when Load() should proceed to the native
        /// platform; <c>false</c> when the caller should synthesize an
        /// immediate <c>OnAdFailedToLoad</c> with <paramref name="error"/>.
        /// </summary>
        internal static bool TryCheck(string adUnitId, out DaroAdLoadError? error)
        {
            if (!DaroSdk.IsInitialized)
            {
                error = new DaroAdLoadError(
                    DaroAdLoadErrorCode.NotInitialized,
                    "Daro SDK is not initialized. Await DaroSdk.InitializeAsync() before Load().",
                    adUnitId);
                return false;
            }

            if (Application.internetReachability == NetworkReachability.NotReachable)
            {
                error = new DaroAdLoadError(
                    DaroAdLoadErrorCode.NoNetwork,
                    "No network reachable (Application.internetReachability == NotReachable).",
                    adUnitId);
                return false;
            }

            error = null;
            return true;
        }
    }
}
