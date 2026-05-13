#nullable enable

using System;
using Daro.Internal;

namespace Daro
{
    /// <summary>
    /// Static event surface for app background/foreground transitions.
    /// See docs/overview.md for the public API contract. Typical use: trigger
    /// <c>DaroAppOpenAd.Show()</c> on foreground return.
    /// </summary>
    /// <remarks>
    /// Backed by the hidden <c>MainThreadDispatcher</c> GameObject (§3.3): its
    /// <c>OnApplicationPause</c> / <c>OnApplicationFocus</c> Unity callbacks
    /// call <see cref="NotifyStateChanged"/> on the main thread. No second
    /// SDK-owned MonoBehaviour.
    /// <para>
    /// Late subscribers see no replay — if initial state matters, subscribe
    /// during <c>Awake</c> / <c>Start</c> before any pause/resume can fire.
    /// </para>
    /// </remarks>
    public static class DaroAppStateNotifier
    {
        public enum AppState
        {
            Background,
            Foreground,
        }

        /// <summary>
        /// Fires on the Unity main thread when the app transitions between
        /// foreground and background. Duplicate consecutive transitions are
        /// suppressed (see <see cref="NotifyStateChanged"/>).
        /// </summary>
        public static event Action<AppState>? OnAppStateChanged;

        /// <summary>
        /// Current app state. Defaults to <see cref="AppState.Foreground"/>.
        /// </summary>
        public static AppState CurrentState { get; private set; } = AppState.Foreground;

        /// <summary>
        /// Called by <c>MainThreadDispatcher.OnApplicationPause</c> /
        /// <c>OnApplicationFocus</c> on the Unity main thread. De-duplicates
        /// redundant transitions so overlapping pause+focus signals collapse
        /// to a single event.
        /// </summary>
        internal static void NotifyStateChanged(AppState newState)
        {
            if (newState == CurrentState) return;
            CurrentState = newState;
            SafeEventInvoker.Invoke(OnAppStateChanged, newState);
        }

        /// <summary>
        /// Clears subscribers and resets <see cref="CurrentState"/> to
        /// <see cref="AppState.Foreground"/>. Called from
        /// <c>DaroRuntimeBoot.Reset</c> on play-mode enter / build startup (§6.4).
        /// </summary>
        internal static void ResetStatics()
        {
            OnAppStateChanged = null;
            CurrentState = AppState.Foreground;
        }
    }
}
