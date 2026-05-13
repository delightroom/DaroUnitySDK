#nullable enable

using System;
using System.Diagnostics;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace Daro.Internal
{
    /// <summary>
    /// SDK-wide logging helper. Two tracks:
    /// <list type="bullet">
    ///   <item><description>Consumer track (<see cref="Info"/> / <see cref="Warn"/> /
    ///     <see cref="Error"/>) — gated by <see cref="DaroSdk.LogLevel"/>.</description></item>
    ///   <item><description>Dev track (<see cref="Verbose"/>) —
    ///     <c>[Conditional("DARO_DEV")]</c>, dead-strips at the IL level when
    ///     the define is unset (argument evaluation included).</description></item>
    /// </list>
    /// Plus two gate-outside helpers (<see cref="Exception"/>,
    /// <see cref="WarnFinalizerSafe"/>) that bypass the LogLevel gate by design.
    /// See sketch-log-module.md §A1 for the full rationale.
    /// </summary>
    /// <remarks>
    /// Messages carry a <c>[Daro:&lt;area&gt;]</c> prefix so consumers can filter
    /// the Unity Console / logcat output by SDK area (sketch §A3). Areas are
    /// PascalCase string constants chosen by the caller — see
    /// <c>.claude/rules/logging.md</c> for the canonical area list.
    /// </remarks>
    internal static class DaroLog
    {
        // ── Dev track ────────────────────────────────────────────────────────
        // [Conditional("DARO_DEV")] guarantees the entire call expression — args
        // included — is omitted from IL when DARO_DEV is unset (ECMA-334
        // §22.5.3). Zero release-build cost; no #if at the call site.

        [Conditional("DARO_DEV")]
        internal static void Verbose(string area, string message)
        {
            if (DaroSdk.LogLevel >= DaroLogLevel.Verbose)
                Debug.Log($"[Daro:{area}] {message}");
        }

        // ── Consumer track ───────────────────────────────────────────────────
        // Runtime LogLevel gate. Argument evaluation still happens — callers
        // pay for string interpolation even when the gate is closed. Hot paths
        // should inline-gate (see WarnFinalizerSafe pattern) to skip the alloc.

        internal static void Info(string area, string message)
        {
            if (DaroSdk.LogLevel >= DaroLogLevel.Info)
                Debug.Log($"[Daro:{area}] {message}");
        }

        internal static void Warn(string area, string message)
        {
            if (DaroSdk.LogLevel >= DaroLogLevel.Warn)
                Debug.LogWarning($"[Daro:{area}] {message}");
        }

        internal static void Error(string area, string message)
        {
            if (DaroSdk.LogLevel >= DaroLogLevel.Error)
                Debug.LogError($"[Daro:{area}] {message}");
        }

        // ── Gate-outside helpers (LogLevel-independent) ──────────────────────
        // SDK-internal exception paths must always be visible — muting them
        // when the consumer sets LogLevel.None would let SDK breakage go
        // silent during integration debugging. Confirmed Decision (sketch §3).

        /// <summary>
        /// Logs an unhandled exception from a SafeEventInvoker / dispatcher
        /// catch path. Always emits regardless of <see cref="DaroSdk.LogLevel"/>.
        /// The <paramref name="area"/> argument is currently unused — area
        /// context is included in the stack trace produced by
        /// <see cref="Debug.LogException(System.Exception)"/>. Kept in the
        /// signature for symmetry with the gated helpers and to keep call
        /// sites self-documenting.
        /// </summary>
        internal static void Exception(string area, Exception ex)
        {
            _ = area;
            Debug.LogException(ex);
        }

        /// <summary>
        /// Finalizer-safe variant of <see cref="Warn"/>. The caller MUST
        /// inline-gate (<c>if (DaroSdk.LogLevel &gt;= DaroLogLevel.Warn)</c>)
        /// before invoking — this avoids string interpolation on the GC
        /// thread when the gate is closed and keeps the finalizer-safe
        /// contract by delegating directly to <see cref="Debug.LogWarning(object)"/>,
        /// which Unity documents as safe to call from a finalizer.
        /// Only used by the two known finalizer sites: <c>DaroBannerAd</c>
        /// and <c>DaroInterstitialAd</c>.
        /// </summary>
        internal static void WarnFinalizerSafe(string message) =>
            Debug.LogWarning(message);
    }
}
