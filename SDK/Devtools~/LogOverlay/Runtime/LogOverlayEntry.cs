#nullable enable

using System;
using UnityEngine;

namespace Daro.Devtools.LogOverlay
{
    /// <summary>
    /// Source category for a parsed log entry. Drives badge colour + which
    /// fields are meaningful. The <c>Sdk*</c> split distinguishes traces of
    /// public events firing (<see cref="SdkPublicEventTrace"/> — same event
    /// the consumer subscribes to) from pure SDK plumbing
    /// (<see cref="SdkInternal"/> — ctor/Load/Show/Hide/Dispose/JNI/dispatcher).
    /// QA cares about the former; developers reach for the latter on demand.
    /// </summary>
    internal enum LogSource
    {
        SampleAdEvent,        // "[<ConsumerPrefix>] Banner ← onAdLoaded  (...)"
        SampleMisc,           // "[<ConsumerPrefix>] AppState → Background"
        SdkPublicEventTrace,  // "[Daro:Banner] FireOnAdLoaded adUnit='...'"
        SdkInternal,          // "[Daro:Banner] Banner.load JNI entry adUnit='...'"
        Unknown,              // Anything that slipped past the prefix filter
    }

    /// <summary>
    /// Parsed, structured representation of a single log line for the
    /// LogOverlay. The raw <see cref="RawCondition"/> stays available for
    /// the detail modal so a developer can see exactly what the SDK /
    /// consumer emitted.
    /// </summary>
    internal sealed class LogEntry
    {
        public DateTime  Timestamp;
        public LogType   Type;          // Unity LogType from logMessageReceived
        public LogSource Source;
        public string    Area = "";     // SampleAdEvent: format; Sdk: area; others: ""
        public string?   Event;         // SampleAdEvent only: e.g. "onAdLoaded"
        public bool      IsSuccess;     // [ok] marker — Success-coloured rows
        public int?      Latency;       // ms, from (latency=...ms)
        public int?      ErrorCode;     // from [code / native msg: "..."]
        public string?   AdUnitId;      // from (adUnitId=...)
        public string?   ErrorMessage;  // from [... / native msg: "..."]
        public string    Message = "";  // cleaned message (prefix + marker stripped)
        public string    RawCondition = "";
    }

    /// <summary>
    /// Stateless parser for <see cref="LogEntry"/>. Recognises:
    /// (1) the SDK's own <c>[Daro:&lt;Area&gt;] ...</c> prefix used by
    /// <c>DaroLog</c>, always; (2) an optional consumer prefix (passed
    /// per-call) that the publisher's structured logger writes — when
    /// followed by the ad-event shape <c>"&lt;Format&gt; ← &lt;Event&gt;
    /// &lt;trailing&gt;"</c>, fields are extracted; otherwise the body is
    /// kept verbatim under <see cref="LogSource.SampleMisc"/>.
    /// </summary>
    /// <remarks>
    /// Conservative — unknown shapes fall back to <see cref="LogSource.Unknown"/>
    /// with the whole condition as <see cref="LogEntry.Message"/>. Never
    /// throws so a malformed line can't kill the overlay.
    /// </remarks>
    internal static class LogParser
    {
        private const string PrefixSdk        = "[Daro:";
        private const string SuccessMarker    = "[ok] ";
        private const string AdEventSeparator = " ← ";
        private const string FieldSeparator   = "  "; // two-space gap before trailing block
        private const string NativeMsgIntro   = " / native msg: ";

        public static LogEntry Parse(string condition, LogType type, string? consumerPrefix)
        {
            var e = new LogEntry
            {
                Timestamp    = DateTime.Now,
                Type         = type,
                RawCondition = condition,
            };

            // Consumer prefix is bracketed + followed by a space — e.g. "[MyApp] ".
            // Match the full "<prefix> " form so a stray prefix substring inside
            // a body doesn't trigger consumer-parse on a non-consumer line.
            string? consumerHeader = string.IsNullOrEmpty(consumerPrefix)
                ? null
                : consumerPrefix + " ";

            if (consumerHeader != null && condition.StartsWith(consumerHeader, StringComparison.Ordinal))
            {
                ParseSample(condition.Substring(consumerHeader.Length), e);
            }
            else if (condition.StartsWith(PrefixSdk, StringComparison.Ordinal))
            {
                ParseSdk(condition, e);
            }
            else
            {
                e.Source  = LogSource.Unknown;
                e.Message = condition;
            }

            return e;
        }

        private static void ParseSample(string body, LogEntry e)
        {
            // Strip the optional [ok] success marker that publishers may
            // prepend to denote a happy-path callback.
            if (body.StartsWith(SuccessMarker, StringComparison.Ordinal))
            {
                e.IsSuccess = true;
                body = body.Substring(SuccessMarker.Length);
            }

            // Ad-event shape — "<Format> ← <Event>  <trailing>". Optional
            // convention the publisher can adopt to get structured ad-event
            // rows; non-conforming lines fall through to SampleMisc.
            var arrowIdx = body.IndexOf(AdEventSeparator, StringComparison.Ordinal);
            if (arrowIdx > 0)
            {
                e.Source = LogSource.SampleAdEvent;
                e.Area   = body.Substring(0, arrowIdx);

                var afterArrow = body.Substring(arrowIdx + AdEventSeparator.Length);
                var sepIdx     = afterArrow.IndexOf(FieldSeparator, StringComparison.Ordinal);
                if (sepIdx > 0)
                {
                    e.Event = afterArrow.Substring(0, sepIdx);
                    var trailing = afterArrow.Substring(sepIdx + FieldSeparator.Length);
                    ParseTrailing(trailing, e);
                }
                else
                {
                    // No trailing — e.g. "Banner ← onAdLoaded" with no fields.
                    e.Event = afterArrow;
                }
                e.Message = body;
                return;
            }

            // Misc consumer line — anything not matching ad-event shape.
            e.Source  = LogSource.SampleMisc;
            e.Message = body;
        }

        private static void ParseSdk(string condition, LogEntry e)
        {
            // Expected shape: [Daro:<Area>] <message>
            var close = condition.IndexOf(']');
            if (close <= PrefixSdk.Length)
            {
                e.Source  = LogSource.Unknown;
                e.Message = condition;
                return;
            }
            e.Area    = condition.Substring(PrefixSdk.Length, close - PrefixSdk.Length);
            e.Message = condition.Substring(close + 1).TrimStart();
            // Distinguish public-event traces ("FireOn<Event> adUnit=...") from
            // pure plumbing (ctor / Load / Show / Dispose / JNI / dispatcher).
            // The former corresponds 1:1 with a consumer-facing event firing —
            // QA cares about these. The latter is developer debug context.
            e.Source = e.Message.StartsWith("FireOn", StringComparison.Ordinal)
                ? LogSource.SdkPublicEventTrace
                : LogSource.SdkInternal;
        }

        private static void ParseTrailing(string trailing, LogEntry e)
        {
            // FormatInfo: "(format=Banner, adUnitId=xxx, latency=234ms)" or latency=null
            if (trailing.Length > 1 && trailing[0] == '(' && trailing[trailing.Length - 1] == ')')
            {
                var inner = trailing.Substring(1, trailing.Length - 2);
                foreach (var part in inner.Split(new[] { ", " }, StringSplitOptions.None))
                {
                    var eq = part.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = part.Substring(0, eq);
                    var val = part.Substring(eq + 1);
                    if (key == "adUnitId")
                    {
                        e.AdUnitId = val;
                    }
                    else if (key == "latency" && val.EndsWith("ms", StringComparison.Ordinal))
                    {
                        if (int.TryParse(val.Substring(0, val.Length - 2), out var ms))
                            e.Latency = ms;
                    }
                }
                return;
            }

            // FormatErr: [<code> / native msg: "<message>"]
            if (trailing.Length > 1 && trailing[0] == '[' && trailing[trailing.Length - 1] == ']')
            {
                var inner    = trailing.Substring(1, trailing.Length - 2);
                var slashIdx = inner.IndexOf(NativeMsgIntro, StringComparison.Ordinal);
                if (slashIdx > 0)
                {
                    var codeStr = inner.Substring(0, slashIdx);
                    if (int.TryParse(codeStr, out var code)) e.ErrorCode = code;
                    var msgPart = inner.Substring(slashIdx + NativeMsgIntro.Length);
                    if (msgPart.Length > 1 && msgPart[0] == '"' && msgPart[msgPart.Length - 1] == '"')
                    {
                        e.ErrorMessage = msgPart.Substring(1, msgPart.Length - 2);
                    }
                }
            }
        }
    }
}
