#nullable enable

namespace Daro
{
    /// <summary>
    /// Reported to consumers via <c>OnAdFailedToShow</c>.
    /// No AdUnitId — display errors are always delivered alongside a
    /// <see cref="DaroAdInfo"/> that already carries the unit id.
    /// Constructor is <c>internal</c> — only the SDK's platform layer
    /// should mint these.
    /// </summary>
    public sealed class DaroAdDisplayError
    {
        public DaroAdDisplayErrorCode Code    { get; }
        public string                 Message { get; }

        /// <summary>
        /// Raw native error code (DaroSDK <c>DaroError.Code.rawValue</c>).
        /// Populated by iOS / Android shims; <c>0</c> on Editor mock.
        /// Useful when <see cref="Code"/> is <see cref="DaroAdDisplayErrorCode.Unspecified"/>.
        /// </summary>
        public int RawCode { get; }

        internal DaroAdDisplayError(DaroAdDisplayErrorCode code, string message, int rawCode = 0)
        {
            Code    = code;
            Message = message;
            RawCode = rawCode;
        }
    }
}
