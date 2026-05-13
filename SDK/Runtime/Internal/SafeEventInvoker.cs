#nullable enable

using System;
using UnityEngine;

namespace Daro.Internal
{
    /// <summary>
    /// Per-handler exception-isolated multicast invocation. Iterates
    /// <see cref="Delegate.GetInvocationList"/> with a try/catch around each handler so
    /// one subscriber's throw does not block subsequent subscribers and does not
    /// propagate out of the dispatcher's drain loop.
    /// </summary>
    /// <remarks>
    /// Used at every multicast event fire site in the SDK
    /// (<see cref="DaroInterstitialAd"/> / <see cref="DaroRewardedAd"/> /
    /// <see cref="DaroAppOpenAd"/> Fire* methods, <see cref="DaroSdk.OnSdkInitialized"/>
    /// post-init invoke, <see cref="DaroAppStateNotifier"/> dispatch). The single
    /// late-subscriber sync invoke at <c>DaroSdk.cs:64</c> stays raw — it's a single
    /// freshly-subscribed delegate, not a multicast chain, so the subscriber should
    /// observe its own throw synchronously on its own thread.
    /// <para>
    /// Logging routes through <see cref="DaroLog.Exception(string, Exception)"/>
    /// (gate-outside, area = "Events") — full stack trace, surfaces as Error
    /// tier independent of <see cref="DaroSdk.LogLevel"/>. Publisher bugs must
    /// always reach the Unity Console.
    /// </para>
    /// <para>
    /// The invoker is policy-agnostic about per-instance disposed state; each Fire*
    /// call site keeps its own <c>if (_disposed) return;</c> guard before calling here.
    /// This matches sketch §D4: dual-layer disposed check, with the instance-level
    /// guard explicit at the call site.
    /// </para>
    /// </remarks>
    internal static class SafeEventInvoker
    {
        /// <summary>
        /// Invoke a parameterless multicast event with per-handler exception isolation.
        /// No-op when <paramref name="ev"/> is null.
        /// </summary>
        internal static void Invoke(Action? ev)
        {
            if (ev == null) return;
            foreach (Action handler in ev.GetInvocationList())
            {
                try
                {
                    handler();
                }
                catch (Exception e)
                {
                    DaroLog.Exception("Events", e);
                }
            }
        }

        /// <summary>
        /// Invoke a 1-arg multicast event with per-handler exception isolation.
        /// No-op when <paramref name="ev"/> is null.
        /// </summary>
        internal static void Invoke<T>(Action<T>? ev, T arg)
        {
            if (ev == null) return;
            foreach (Action<T> handler in ev.GetInvocationList())
            {
                try
                {
                    handler(arg);
                }
                catch (Exception e)
                {
                    DaroLog.Exception("Events", e);
                }
            }
        }

        /// <summary>
        /// Invoke a 2-arg multicast event with per-handler exception isolation.
        /// No-op when <paramref name="ev"/> is null.
        /// </summary>
        internal static void Invoke<T1, T2>(Action<T1, T2>? ev, T1 a, T2 b)
        {
            if (ev == null) return;
            foreach (Action<T1, T2> handler in ev.GetInvocationList())
            {
                try
                {
                    handler(a, b);
                }
                catch (Exception e)
                {
                    DaroLog.Exception("Events", e);
                }
            }
        }
    }
}
