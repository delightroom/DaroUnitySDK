#nullable enable

namespace Daro
{
    /// <summary>
    /// Precision of a reported ad revenue value. Integer codes match
    /// AdMob's <c>AdValue.PrecisionType</c>, which daro adopted as the
    /// cross-mediation wire encoding (MAX string precisions are mapped
    /// to these codes inside the daro native SDKs).
    /// </summary>
    public enum DaroRevenuePrecision
    {
        Unknown           = 0,
        Estimated         = 1,
        PublisherProvided = 2,
        Exact             = 3,
    }

    /// <summary>
    /// Per-impression revenue payload (ILRD) delivered with
    /// <c>OnAdRevenuePaid</c>. The amount comes from the native SDK and the
    /// Unity layer passes it through unchanged.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/> is <c>decimal</c> end-to-end. Both platforms report
    /// integer micros and the wrapper divides by 1,000,000 — the amount never
    /// routes through binary floating point.
    /// </remarks>
    public sealed class DaroRevenueInfo
    {
        /// <summary>Revenue for this impression, in <see cref="CurrencyCode"/> units.</summary>
        public decimal Value { get; }

        /// <summary>ISO 4217 currency code. MAX mediation always reports <c>"USD"</c>.</summary>
        public string CurrencyCode { get; }

        /// <summary>How precise <see cref="Value"/> is.</summary>
        public DaroRevenuePrecision Precision { get; }

        public DaroRevenueInfo(decimal value, string currencyCode, DaroRevenuePrecision precision)
        {
            Value        = value;
            CurrencyCode = currencyCode;
            Precision    = precision;
        }

        /// <summary>
        /// Build from the native wire encoding: integer micros (1,000,000
        /// micros = 1 currency unit) + AdValue-style precision code.
        /// Unknown precision codes degrade to <see cref="DaroRevenuePrecision.Unknown"/>.
        /// </summary>
        internal static DaroRevenueInfo FromMicros(long valueMicros, string currencyCode, int precisionCode)
            => new DaroRevenueInfo(valueMicros / 1_000_000m, currencyCode, MapPrecision(precisionCode));

        private static DaroRevenuePrecision MapPrecision(int code)
            => code >= (int)DaroRevenuePrecision.Unknown && code <= (int)DaroRevenuePrecision.Exact
                ? (DaroRevenuePrecision)code
                : DaroRevenuePrecision.Unknown;
    }
}
