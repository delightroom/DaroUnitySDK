#nullable enable
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Inspector-tunable knobs for the in-Editor mock platform.
    /// Consumed at runtime (in Editor only) by <c>DaroEditorPlatform</c>.
    /// Per native-bridge §5: raw <c>int</c> error-code fields are intentional
    /// so testers can feed unmapped codes through the same
    /// <c>DaroAdErrorCodeMapper</c> path the device platforms use and
    /// verify the <c>Unspecified</c> fallback end-to-end.
    /// </summary>
    [CreateAssetMenu(menuName = "Daro/Editor Settings", fileName = "DaroEditorSettings")]
    public sealed class DaroEditorSettings : ScriptableObject
    {
        [Header("Initialization")]
        [Range(0f, 5f)]   public float initDelaySeconds  = 0.5f;
                          public bool  initShouldSucceed = true;

        [Header("Ad Load")]
        [Range(0f, 10f)]  public float  loadDelaySeconds = 1.0f;
        [Range(0f, 1f)]   public float  loadSuccessRate  = 0.9f;
                          public int    loadErrorCode    = 204;     // noFill (DaroError.Code rawValue)
                          public string loadErrorMessage = "No fill (Editor mock)";

        /// <summary>
        /// Milliseconds. <c>-1</c> surfaces <c>null</c> on <c>DaroAdInfo.Latency</c>
        /// so consumers can exercise the null branch. Positive values surface
        /// as-is in milliseconds (matching Daro's cross-platform contract).
        /// </summary>
        [Range(-1, 5000)] public int loadLatencyMs = 120;

        [Header("Ad Display")]
        [Range(0f, 3f)]   public float  showDelaySeconds  = 0.2f;
        [Range(0f, 1f)]   public float  showSuccessRate   = 1.0f;
                          public int    showErrorCode     = -24;    // fullscreenAdNotReady
                          public string showErrorMessage  = "Ad not ready (Editor mock)";
        [Range(0f, 30f)]  public float  adDurationSeconds = 3.0f;   // time before OnAdDismissed fires

        [Header("Rewarded")]
                          public int    rewardAmount = 10;
                          public string rewardType   = "coins";

        [Header("Revenue (ILRD)")]
        /// <summary>
        /// Mock net revenue per impression, in USD micros (1,000,000 = $1) —
        /// integer so the Editor path exercises the same micros→decimal
        /// conversion as the Android wire. 12,340 micros = $0.01234.
        /// </summary>
                          public long revenueValueMicros   = 12_340;
                          public string revenueCurrencyCode = "USD";
        /// <summary>0=Unknown 1=Estimated 2=PublisherProvided 3=Exact.</summary>
        [Range(0, 3)]     public int  revenuePrecisionType = 3;
    }
}
