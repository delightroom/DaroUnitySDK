#nullable enable

using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Hidden <see cref="MonoBehaviour"/> that drains a thread-safe action queue
    /// on the Unity main thread. See docs/features/event-handler.md.
    /// </summary>
    /// <remarks>
    /// Native callbacks arrive on unspecified background threads. They enqueue
    /// closures via <see cref="Enqueue(Action)"/>; <see cref="Update"/> drains
    /// and invokes them on the Unity main thread so consumer handlers can
    /// safely touch Unity APIs.
    ///
    /// <para>Also hosts the Unity lifecycle hooks for
    /// <see cref="DaroAppStateNotifier"/> (§2.6) — the same hidden GameObject
    /// doubles as the app-state source to avoid a second SDK-owned GameObject.</para>
    ///
    /// <para>Static state resets via <see cref="ResetStatics"/>, called from
    /// <see cref="DaroRuntimeBoot.Reset"/> on every play-mode enter / build startup
    /// (§6.4) so stale references from a prior domain do not leak into a fresh run.</para>
    /// </remarks>
    internal sealed class MainThreadDispatcher : MonoBehaviour
    {
        private const string GameObjectName = "[DaroSDK]MainThreadDispatcher";

        private static MainThreadDispatcher? _instance;
        private static volatile bool _isShuttingDown;
        private static int _mainThreadId;

        // MAX-pattern queue: lock + Queue<T> + volatile empty-flag + reusable
        // drain buffer (study §2.1 + §4.7). Chosen over ConcurrentQueue<T> for:
        //   1. predictable BCL behavior — no Unity Mono / IL2CPP surprises
        //      around segment-based ConcurrentQueue<T> internals
        //   2. snapshot-and-drain naturally bounds per-frame work to items
        //      present at lock acquisition (resolves features/native-bridge.md
        //      "알려진 제약 — Update() 드레인 unbounded")
        //   3. exact mirror of MAX MaxEventExecutor pattern
        // Race-safety + deadlock-impossibility analysis: study §4.7 — single
        // lock, no nested locks inside critical section, publisher code runs
        // outside the lock.
        private readonly Queue<Action> _queue = new Queue<Action>(64);
        private readonly object _queueLock = new object();
        private volatile bool _queueEmpty = true;
        private readonly List<Action> _drainBuffer = new List<Action>(64);

        /// <summary>
        /// Idempotently creates the hidden dispatcher GameObject. Must be called
        /// on the Unity main thread (Unity's GameObject constructor is main-thread-only).
        /// Typically invoked from <c>DaroSdk.InitializeAsync</c>.
        /// </summary>
        internal static void EnsureCreated()
        {
            if (_instance != null) return;

            // Defensive: under "Enter Play Mode Settings" with both Domain Reload
            // and Scene Reload disabled, a prior session's hidden GameObject can
            // survive into the next play session even after ResetStatics wiped
            // _instance. Find and destroy any stale dispatcher before creating a
            // fresh one to avoid doubled instances.
            var stale = FindFirstObjectByType<MainThreadDispatcher>(FindObjectsInactive.Include);
            if (stale != null)
            {
                DestroyImmediate(stale.gameObject);
            }

            var go = new GameObject(GameObjectName);
            DontDestroyOnLoad(go);
            go.hideFlags = HideFlags.HideAndDontSave;
            _instance = go.AddComponent<MainThreadDispatcher>();

            // EnsureCreated is contracted to be called on the Unity main thread
            // (GameObject construction enforces this — we'd have thrown above
            // otherwise). Capture the id so other code can branch on main vs
            // worker without a Unity API call.
            _mainThreadId = Thread.CurrentThread.ManagedThreadId;
        }

        /// <summary>
        /// True iff the current thread is the Unity main thread, captured during
        /// <see cref="EnsureCreated"/>. Returns false before the dispatcher is
        /// created — callers that may run pre-init must treat that as off-main.
        /// </summary>
        internal static bool IsMainThread()
        {
            return _mainThreadId != 0
                && Thread.CurrentThread.ManagedThreadId == _mainThreadId;
        }

        /// <summary>
        /// Queue an action to run on the Unity main thread on the next Update tick.
        /// Thread-safe. Silently no-ops when the action is null, the dispatcher has
        /// not been created yet, or the app is shutting down (§6.3) — the latter
        /// prevents callback closures from firing against partially-torn-down state.
        /// </summary>
        internal static void Enqueue(Action action)
        {
            // Capture the instance once so a concurrent ResetStatics nulling
            // _instance between the null-check and the lock acquisition can't
            // NRE us — the existing dispatcher (and its lock + queue) stays
            // usable until GC reclaims it.
            var inst = _instance;
            if (action == null || inst == null || _isShuttingDown) return;
            lock (inst._queueLock)
            {
                inst._queue.Enqueue(action);
                inst._queueEmpty = false;
            }
        }

        /// <summary>
        /// Start a coroutine on the hidden dispatcher GameObject. Used by the
        /// Editor mock platform — and by any SDK-internal
        /// code that needs a coroutine host without the consumer having to
        /// supply one. Noop if <see cref="EnsureCreated"/> has not been called
        /// or the dispatcher is shutting down.
        /// </summary>
        /// <remarks>
        /// Named <c>RunCoroutine</c> rather than <c>StartCoroutine</c> to avoid
        /// name-clashing with <see cref="MonoBehaviour.StartCoroutine(IEnumerator)"/>
        /// (C# CS0176: instance-vs-static lookup through an instance reference
        /// is disallowed when both names resolve).
        /// </remarks>
        internal static void RunCoroutine(IEnumerator coroutine)
        {
            if (coroutine == null || _instance == null || _isShuttingDown) return;
            _instance.StartCoroutine(coroutine);
        }

        private void Update()
        {
            // Volatile early-return — when the queue is empty (the common case
            // since ad events are infrequent), skip the lock entirely. Matches
            // MAX MaxEventExecutor.Update fast-path (study §2.1.3 detail 1).
            if (_queueEmpty) return;

            // Snapshot under lock — drain queue into the reusable buffer, then
            // execute outside the lock. Two consequences:
            //   * publisher handlers (potentially long-running) don't block
            //     worker-thread Enqueue (study §4.7.5)
            //   * per-frame work is bounded to items present at lock acquisition
            //     — items enqueued during foreach go to next frame
            lock (_queueLock)
            {
                _drainBuffer.AddRange(_queue);
                _queue.Clear();
                _queueEmpty = true;
            }

            // Outer drain guard — study §2.5 "3중 try/catch" layer 1.
            // SafeEventInvoker (innermost layer) catches publisher handler
            // throws inside Fire* methods, but anything unexpected escaping
            // upstream (routing bugs, raw worker-late-subscriber enqueue,
            // future code paths) must not kill the drain loop. Surface via
            // DaroLog.Exception (gate-outside, always emits) and keep
            // draining the rest of the buffer.
            foreach (var action in _drainBuffer)
            {
                try
                {
                    action();
                }
                catch (Exception e)
                {
                    DaroLog.Exception("Dispatcher", e);
                }
            }

            // List<T>.Clear keeps the internal array — buffer is reused next
            // frame without reallocation. Only grows on peak burst.
            _drainBuffer.Clear();
        }

        private void OnApplicationQuit()
        {
            _isShuttingDown = true;
        }

        // ── Unity lifecycle forwards for DaroAppStateNotifier (§2.6) ─────────
        //
        // OnApplicationPause is the canonical Background signal across platforms.
        // OnApplicationFocus is platform-dependent: on Android, modal Dialog focus
        // loss (Light Popup) raises focus(false) without an Activity onPause —
        // mapping that to Background mis-represents the OS lifecycle. We therefore
        // ignore focus on Android and keep it as a Background source on other
        // platforms (Editor + iOS, per appstate-meaning-narrowing sprint).
        //
        // Mapping helpers are exposed for EditMode regression tests.

        internal static DaroAppStateNotifier.AppState? MapPauseToState(bool paused) =>
            paused
                ? DaroAppStateNotifier.AppState.Background
                : DaroAppStateNotifier.AppState.Foreground;

        internal static DaroAppStateNotifier.AppState? MapFocusToState(bool focused, RuntimePlatform platform)
        {
            if (platform == RuntimePlatform.Android) return null;
            return focused
                ? DaroAppStateNotifier.AppState.Foreground
                : DaroAppStateNotifier.AppState.Background;
        }

        private void OnApplicationPause(bool paused)
        {
            var state = MapPauseToState(paused);
            if (state.HasValue) DaroAppStateNotifier.NotifyStateChanged(state.Value);
        }

        private void OnApplicationFocus(bool focused)
        {
            var state = MapFocusToState(focused, Application.platform);
            if (state.HasValue) DaroAppStateNotifier.NotifyStateChanged(state.Value);
        }

        /// <summary>
        /// Clears static references. Called from <see cref="DaroRuntimeBoot.Reset"/>
        /// on play-mode enter / build startup (§6.4). Safe to invoke repeatedly.
        /// Does not destroy any existing GameObject — Unity itself tears scene
        /// objects down between play sessions; this method only wipes the C# references.
        /// </summary>
        internal static void ResetStatics()
        {
            _instance = null;
            _isShuttingDown = false;
            _mainThreadId = 0;
        }
    }
}
