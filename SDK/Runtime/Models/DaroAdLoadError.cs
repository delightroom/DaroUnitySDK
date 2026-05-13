#nullable enable

namespace Daro
{
    /// <summary>
    /// Reported to consumers via <c>OnAdFailedToLoad</c>.
    /// Constructor is <c>internal</c> — only the SDK's platform layer
    /// should mint these; consumer code receives them, never builds them.
    /// </summary>
    public sealed class DaroAdLoadError
    {
        public DaroAdLoadErrorCode Code     { get; }
        public string              Message  { get; }

        /// <summary>
        /// Null for init-phase or otherwise unit-agnostic failures.
        /// </summary>
        public string? AdUnitId { get; }

        /// <summary>
        /// Raw native error code (DaroSDK <c>DaroError.Code.rawValue</c>).
        /// Populated by iOS / Android shims; <c>0</c> on Editor mock.
        /// Useful when <see cref="Code"/> is <see cref="DaroAdLoadErrorCode.Unspecified"/> —
        /// the raw integer lets consumers distinguish "truly unknown" from
        /// "known native failure not yet enumerated in <see cref="DaroAdLoadErrorCode"/>."
        /// </summary>
        public int RawCode { get; }

        internal DaroAdLoadError(DaroAdLoadErrorCode code, string message, string? adUnitId, int rawCode = 0)
        {
            Code     = code;
            Message  = message;
            AdUnitId = adUnitId;
            RawCode  = rawCode;
        }
    }
}
