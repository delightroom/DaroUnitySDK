#nullable enable
using System;

namespace Daro
{
    /// <summary>
    /// Thrown (via faulted Task) from <c>DaroSdk.InitializeAsync()</c>
    /// when the native DaroSDK fails initialization. <see cref="DaroErrorCode"/>
    /// carries the raw native code so callers can log/report it without a
    /// translation table.
    /// </summary>
    public sealed class DaroSdkInitException : Exception
    {
        public int DaroErrorCode { get; }

        public DaroSdkInitException(string message, int daroErrorCode)
            : base(message)
        {
            DaroErrorCode = daroErrorCode;
        }
    }
}
