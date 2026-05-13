#nullable enable

namespace Daro.Internal
{
    /// <summary>
    /// Parameter bag passed from <c>DaroSdk</c> static facade to
    /// <see cref="IDaroPlatform.InitializeAsync"/>. Internal — consumer
    /// code sets these values via <c>DaroSdk</c> static properties.
    /// </summary>
    internal sealed class DaroSdkInitParams
    {
        public bool?        HasGdprConsent                    { get; set; }
        public string?      GdprConsentString                 { get; set; }
        public bool?        DoNotSell                         { get; set; }
        public string?      CcpaConsentString                 { get; set; }
        public bool?        IsTaggedForChildDirectedTreatment { get; set; }
        public DaroLogLevel LogLevel                          { get; set; }
    }
}
