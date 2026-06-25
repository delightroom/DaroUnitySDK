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
    /// <c>OnAdRevenuePaid</c>. The value is net revenue — the daro native
    /// SDK applies the server-provided fee rate before the value crosses
    /// the bridge; the Unity layer never re-adjusts it.
    /// </summary>
    /// <remarks>
    /// <see cref="Value"/> is <c>decimal</c> end-to-end: Android reports
    /// integer micros (exact ÷ 1,000,000), iOS reports an
    /// <c>NSDecimalNumber</c> serialized as a decimal string — neither
    /// path routes the amount through binary floating point.
    /// </remarks>
    public sealed class DaroRevenueInfo
    {
        /// <summary>Net revenue for this impression, in <see cref="CurrencyCode"/> units.</summary>
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
        /// Build from Android's wire encoding: integer micros (1,000,000
        /// micros = 1 currency unit) + AdValue-style precision code.
        /// Unknown precision codes degrade to <see cref="DaroRevenuePrecision.Unknown"/>.
        /// </summary>
        internal static DaroRevenueInfo FromMicros(long valueMicros, string currencyCode, int precisionCode)
            => new DaroRevenueInfo(valueMicros / 1_000_000m, currencyCode, MapPrecision(precisionCode));

        /// <summary>
        /// Build from iOS's wire encoding: NSDecimalNumber rendered as an
        /// invariant decimal string. Unparseable strings degrade to 0 —
        /// a dropped payload must not turn into a dispatch-loop throw.
        /// </summary>
        internal static DaroRevenueInfo FromDecimalString(string? value, string currencyCode, int precisionCode)
        {
            decimal parsed = 0m;
            if (value != null)
            {
                decimal.TryParse(
                    value,
                    System.Globalization.NumberStyles.Number,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out parsed);
            }
            return new DaroRevenueInfo(parsed, currencyCode, MapPrecision(precisionCode));
        }

        private static DaroRevenuePrecision MapPrecision(int code)
            => code >= (int)DaroRevenuePrecision.Unknown && code <= (int)DaroRevenuePrecision.Exact
                ? (DaroRevenuePrecision)code
                : DaroRevenuePrecision.Unknown;
    }
}
