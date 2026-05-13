#nullable enable
using System;

namespace Daro.Internal
{
    /// <summary>
    /// Platform resolver. Selects the <see cref="IDaroPlatform"/> implementation for the
    /// current runtime: <see cref="DaroEditorPlatform"/> under <c>UNITY_EDITOR</c>, and
    /// <c>DaroIOSPlatform</c> / <c>DaroAndroidPlatform</c> on device builds.
    /// See docs/overview.md and docs/features/native-bridge.md.
    /// </summary>
    /// <remarks>
    /// Static cache is cleared via <see cref="ResetStatics"/>, invoked by
    /// <see cref="DaroRuntimeBoot.Reset"/> on play-mode enter / build startup (§6.4).
    /// </remarks>
    internal static class DaroPlatform
    {
        private static IDaroPlatform? _current;

        internal static IDaroPlatform Current
        {
            get
            {
                if (_current != null) return _current;
                _current = CreateForCurrentRuntime();
                return _current;
            }
        }

        private static IDaroPlatform CreateForCurrentRuntime()
        {
#if UNITY_EDITOR
            // All Editor runs (and EditMode/PlayMode tests) use the mock platform.
            return new DaroEditorPlatform();
#elif UNITY_IOS
            return new DaroIOSPlatform();
#elif UNITY_ANDROID
            return new DaroAndroidPlatform();
#else
            throw new NotImplementedException("Unsupported platform");
#endif
        }

        /// <summary>
        /// Clears the cached platform instance. Called from
        /// <see cref="DaroRuntimeBoot.Reset"/> on play-mode enter / build startup (§6.4).
        /// Safe to invoke repeatedly.
        /// </summary>
        internal static void ResetStatics() => _current = null;

        /// <summary>
        /// Test-only override. Installs the given platform instance as
        /// <see cref="Current"/>, bypassing <see cref="CreateForCurrentRuntime"/>.
        /// Used by integration tests (Ad-class PlayMode tests) to inject a
        /// deterministic <see cref="DaroEditorPlatform"/> built from a tuned
        /// <see cref="DaroEditorSettings"/> fixture (loadSuccessRate=1.0, etc.)
        /// so test outcomes are not subject to the default 0.9 success rate.
        /// </summary>
        /// <remarks>
        /// Not exposed via <c>public</c>; relies on <c>InternalsVisibleTo("Daro.Tests")</c>
        /// declared in <c>AssemblyInfo.cs</c>. Production code paths should never
        /// call this — the regular <see cref="Current"/> getter handles resolution.
        /// </remarks>
        internal static void SetCurrent(IDaroPlatform platform) => _current = platform;
    }
}
