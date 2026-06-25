#nullable enable

namespace Daro.Internal
{
    /// <summary>
    /// Pure encoding helpers used by <c>DaroAndroidPlatform</c> when calling
    /// into the Kotlin shim. Lives outside the <c>#if UNITY_ANDROID</c> guard
    /// so that EditMode tests can verify the mapping tables directly without
    /// an Android build target — the Android impl is the only consumer in
    /// production.
    /// </summary>
    internal static class DaroAndroidEncoding
    {
        /// <summary>
        /// C# nullable bool → JNI int sentinel for the Kotlin shim's
        /// <c>DaroUnityBridge.initialize</c> entry. Sketch CD-6.
        /// </summary>
        /// <returns><c>-1</c> if null, <c>1</c> if true, <c>0</c> if false.</returns>
        /// <remarks>
        /// JNI cannot pass nullable Kotlin types, so privacy flags use a
        /// tristate int. The shim guards <c>if (flag &gt;= 0) Daro.grantXxx(...)</c>
        /// so a <c>-1</c> sentinel preserves the native default (no grant /
        /// reject call) when consumer leaves the C# property unset.
        /// </remarks>
        internal static int NullableBoolToTristate(bool? v) => v == null ? -1 : (v.Value ? 1 : 0);

        /// <summary>
        /// C# <see cref="DaroLogLevel"/> → Daro Android SDK <c>SDKConfig.setDebugMode</c>
        /// boolean. Sketch CD-13.
        /// </summary>
        /// <remarks>
        /// <para>The Daro Android SDK exposes only a binary debug toggle —
        /// <c>SDKConfig.Builder().setDebugMode(boolean)</c>. There is no
        /// <c>setLogLevel</c> equivalent (verified via daro-m:1.3.12 sources).
        /// C#'s 5-step enum is compressed to a boolean: any non-<c>None</c>
        /// level enables debug mode, on the principle that a consumer asking
        /// for any logging at all should receive the native debug stream.</para>
        ///
        /// <para>Out-of-range cast values fall back to <c>false</c> (quiet) —
        /// defensive against future enum extensions that propagate a value
        /// the mapper has not yet been updated for.</para>
        /// </remarks>
        internal static bool LogLevelToDebugMode(DaroLogLevel level) => level switch
        {
            DaroLogLevel.None    => false,
            DaroLogLevel.Error   => true,
            DaroLogLevel.Warn    => true,
            DaroLogLevel.Info    => true,
            DaroLogLevel.Verbose => true,
            _                    => false,
        };
    }
}
