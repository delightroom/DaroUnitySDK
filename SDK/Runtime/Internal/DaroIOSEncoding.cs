#nullable enable

namespace Daro.Internal
{
    /// <summary>
    /// Pure encoding helpers used by <c>DaroIOSPlatform</c> when calling into
    /// the native shim. Lives outside the <c>#if UNITY_IOS</c> guard so that
    /// EditMode tests can verify the mapping tables directly without an iOS
    /// build target — the iOS impl is the only consumer in production.
    /// </summary>
    internal static class DaroIOSEncoding
    {
        /// <summary>
        /// C# nullable bool → C int sentinel for the native shim's
        /// <c>DaroUnity_Initialize</c> entry.
        /// </summary>
        /// <returns><c>-1</c> if null, <c>1</c> if true, <c>0</c> if false.</returns>
        internal static int NullableBoolToInt(bool? v) => v == null ? -1 : (v.Value ? 1 : 0);

        // The 5→3 collapse to daro iOS internal `DaroObjCLogLevel` happens in the iOS shim
        // (`DaroUnityCollapseToObjCLogLevel` in `DaroUnityLog.{h,mm}`). The C#
        // boundary now sends the raw `(int)DaroLogLevel` value (0..4) so the
        // shim can gate its own NSLog calls at full granularity AND derive
        // the daro iOS internal level itself.
    }
}
