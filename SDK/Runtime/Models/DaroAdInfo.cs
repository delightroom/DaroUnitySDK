#nullable enable

namespace Daro
{
    /// <summary>
    /// Mirrors DaroObjCAdInfo (iOS) / the Kotlin shim's onAdLoaded payload (Android).
    /// Fields are exactly those the native SDKs expose publicly.
    /// </summary>
    public sealed class DaroAdInfo
    {
        public DaroAdFormat AdFormat { get; }
        public string       AdUnitId { get; }

        /// <summary>
        /// Load latency in milliseconds; <c>null</c> if the native side
        /// did not report a value. Matches Daro's cross-platform contract
        /// (Android: <c>MaxAd.requestLatencyMillis</c>; iOS: <c>MAAd.requestLatency * 1000</c>).
        /// </summary>
        public double? Latency { get; }

        /// <summary>
        /// The mediation that served this ad — <c>"daroa"</c> or <c>"darom"</c>;
        /// <c>null</c> when unknown (e.g. an ad synthesized from a warm cache,
        /// or a native build that predates this field).
        /// </summary>
        // 값은 SDK 공개 어휘 그대로다 — 래퍼에서 매핑하지 않는다 (DARO-1683, RN 선례 DARO-1615).
        // 래퍼가 다시 매핑하면 SDK 가 어휘를 바꿀 때 그쪽이 조용히 낡는다 — Flutter iOS 가
        // 정확히 그렇게 낡았다 (DARO-1684).
        public string? MediationPlatform { get; }

        /// <summary>
        /// Vendor name of the winning ad network as reported by the mediation
        /// (not normalized by Daro); <c>null</c> when unknown.
        /// </summary>
        public string? AdNetwork { get; }

        // 두 인자를 기본값 null 로 두는 이유: 기존 호출부(배너 런타임의 shown 합성 등)와 테스트가
        // 세 인자로 부른다. 소스 호환을 깨지 않고 표면만 넓힌다.
        public DaroAdInfo(
            DaroAdFormat adFormat,
            string adUnitId,
            double? latency,
            string? mediationPlatform = null,
            string? adNetwork = null)
        {
            AdFormat          = adFormat;
            AdUnitId          = adUnitId;
            Latency           = latency;
            MediationPlatform = mediationPlatform;
            AdNetwork         = adNetwork;
        }
    }
}
