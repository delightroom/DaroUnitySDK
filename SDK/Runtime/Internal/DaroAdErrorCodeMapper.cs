#nullable enable
using System;

namespace Daro.Internal
{
    /// <summary>
    /// Maps raw native <see cref="int"/> error codes (DaroSDK <c>DaroError.Code.rawValue</c>)
    /// to typed Unity enums. See native-bridge-architecture.md §4.2.
    /// </summary>
    /// <remarks>
    /// <para>Both the Editor mock (<c>DaroEditorPlatform</c>) and the Phase 4 iOS/Android
    /// shims route raw codes through this mapper. Using <see cref="Enum.IsDefined"/>
    /// ensures that codes newly added to DaroSDK — or Android-specific codes that do not
    /// overlap with iOS's numbering — fall back to <c>Unspecified</c> instead of producing
    /// out-of-range enum values that silently break consumer <c>switch</c> statements.</para>
    ///
    /// <para>Consumers never see this mapper directly; they receive typed
    /// <see cref="DaroAdLoadErrorCode"/> / <see cref="DaroAdDisplayErrorCode"/> values
    /// on <c>DaroAdLoadError</c> / <c>DaroAdDisplayError</c> POCOs.</para>
    /// </remarks>
    internal static class DaroAdErrorCodeMapper
    {
        internal static DaroAdLoadErrorCode ToLoadErrorCode(int native) =>
            Enum.IsDefined(typeof(DaroAdLoadErrorCode), native)
                ? (DaroAdLoadErrorCode)native
                : DaroAdLoadErrorCode.Unspecified;

        internal static DaroAdDisplayErrorCode ToDisplayErrorCode(int native) =>
            Enum.IsDefined(typeof(DaroAdDisplayErrorCode), native)
                ? (DaroAdDisplayErrorCode)native
                : DaroAdDisplayErrorCode.Unspecified;
    }
}
