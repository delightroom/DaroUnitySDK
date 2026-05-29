#nullable enable

using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UIElements;

namespace Daro.Devtools.LogOverlay
{
    /// <summary>
    /// Static floating log overlay that renders above every other scene
    /// layer via a dedicated <see cref="UIDocument"/> + <c>PanelSettings</c>
    /// with high <c>sortingOrder</c> (the bundled asset uses 100; the
    /// consumer's other panels are typically 0).
    /// </summary>
    /// <remarks>
    /// <b>Single log source</b>: subscribes to
    /// <see cref="Application.logMessageReceived"/> and filters by prefix —
    /// the SDK's own <c>[Daro:</c> prefix is always included, and the
    /// optional <see cref="consumerLogPrefix"/> (set per scene instance via
    /// the Inspector) lets the publisher route their own structured log
    /// lines through the same overlay. Leave the field empty to show only
    /// SDK lines.
    /// <para>
    /// <b>Success colour preserved</b>: if a publisher log line begins with
    /// the marker <c>[ok] </c> (after the configured prefix), the overlay
    /// strips the marker and applies a success tint. Useful for "happy
    /// path" callbacks (ad shown, reward earned, ...).
    /// </para>
    /// <para>
    /// <b>Draggable + resizable</b>: pointer events on the header move the
    /// panel; the four corner handles resize it. Position + size persist
    /// across Play-mode restarts via <c>PlayerPrefs</c>.
    /// </para>
    /// <para>
    /// <b>Hide / show</b>: the <c>—</c> button collapses to a bubble in the
    /// corner; the bubble's <c>Show Log</c> button restores. Both live in
    /// the same overlay UIDocument so they remain accessible from any
    /// screen of the consumer app.
    /// </para>
    /// </remarks>
    [RequireComponent(typeof(UIDocument))]
    public sealed class LogOverlayController : MonoBehaviour
    {
        [SerializeField]
        [Tooltip("Optional log prefix the consumer app uses for its own structured logs " +
                 "(e.g. \"[MyApp]\"). Leave empty to show only Daro SDK lines.")]
        private string consumerLogPrefix = "";

        private const int    MaxLines  = 300;
        private const string PrefixSdk = "[Daro:";

        // PlayerPrefs keys for multi-select filter toggles (int 0/1 per toggle).
        // "Event" combines SampleAdEvent + SdkPublicEventTrace — both describe
        // the same public ad event from publisher and SDK angles respectively;
        // QA / non-dev users only care about "did the event happen?", not
        // which side emitted the trace. Badge tier (solid vs lighter purple)
        // still tells developers apart in the row visual + detail modal.
        private const string PrefFEvent   = "Daro_LogOverlay_F_Event";
        private const string PrefFSdk     = "Daro_LogOverlay_F_Sdk";
        private const string PrefFMisc    = "Daro_LogOverlay_F_Misc";
        private const string PrefFInfo    = "Daro_LogOverlay_F_Info";
        private const string PrefFSuccess = "Daro_LogOverlay_F_Success";
        private const string PrefFWarn    = "Daro_LogOverlay_F_Warn";
        private const string PrefFError   = "Daro_LogOverlay_F_Error";

        // PlayerPrefs keys for drag-position + resize-size persistence.
        private const string PrefX = "Daro_LogOverlay_X";
        private const string PrefY = "Daro_LogOverlay_Y";
        private const string PrefW = "Daro_LogOverlay_W";
        private const string PrefH = "Daro_LogOverlay_H";

        // Collapsed-bubble (show button) drag position.
        private const string PrefBubbleX = "Daro_LogOverlay_BubbleX";
        private const string PrefBubbleY = "Daro_LogOverlay_BubbleY";

        // Resize clamps — inner content is ScrollView-clipped, so the floor
        // can be tight. Header may overflow at the smallest widths; user is
        // expected to scroll within the log to read content.
        private const float MinWidth  = 100f;
        private const float MinHeight = 60f;

        private enum ResizeCorner { TopLeft, TopRight, BottomLeft, BottomRight }

        private UIDocument _doc = null!;
        private VisualElement? _panel;
        private VisualElement? _bubble;
        private VisualElement? _handle;
        private ScrollView?    _scroll;
        private VisualElement? _content;
        private VisualElement? _resizeTL;
        private VisualElement? _resizeTR;
        private VisualElement? _resizeBL;
        private VisualElement? _resizeBR;

        private VisualElement? _detailModal;
        private VisualElement? _detailBackdrop;
        private Label?         _detailBadge;
        private Label?         _detailTitle;
        private VisualElement? _detailFields;
        private Label?         _detailRaw;

        private VisualElement? _filterPopup;
        private Button?        _filterBtn;
        // Source dim — 3-way: "Ad Event" combines SampleAdEvent + SdkPublicEventTrace.
        private Toggle? _tgEvent;
        private Toggle? _tgSdk;
        private Toggle? _tgMisc;
        // Level dim
        private Toggle? _tgInfo;
        private Toggle? _tgSuccess;
        private Toggle? _tgWarn;
        private Toggle? _tgError;
        // State mirror (one bool per category) — kept in sync with Toggle.value
        // so PassesFilter can read without touching UI elements.
        private bool _fEvent, _fSdk, _fMisc;
        private bool _fInfo, _fSuccess, _fWarn, _fError;

        private bool    _dragging;
        private Vector2 _dragOffset;

        // Bubble drag — distinguishes a tap (restore) from a drag (reposition)
        // via a small movement threshold so the show-button stays tappable.
        private bool    _bubbleDragging;
        private bool    _bubbleMoved;
        private Vector2 _bubbleDragOffset;
        private Vector2 _bubbleDownPos;
        private const float BubbleDragThreshold = 6f;

        private bool         _resizing;
        private ResizeCorner _resizeCorner;
        private Vector2      _resizeStartPointer;
        private Rect         _resizeStartRect;

        // Coalesce auto-scroll into one frame-end tick per Log() burst.
        private bool _scrollScheduled;

        // Queue from logMessageReceived → next OnEnable / next Update pump
        // for the rare case where logs arrive before UI binding has finished.
        // Keeps the first frame's "Initialize" line from being dropped.
        private readonly Queue<(string condition, LogType type)> _pending = new Queue<(string, LogType)>(16);

        // Persist across scene loads so the debug overlay keeps capturing logs
        // through scene transitions; singleton guard drops a duplicate that a
        // newly-loaded scene might carry.
        private static LogOverlayController? _instance;
        private bool _isDuplicate;

        private void Awake()
        {
            if (_instance != null && _instance != this)
            {
                _isDuplicate = true;
                Destroy(gameObject);
                return;
            }
            _instance = this;
            transform.SetParent(null);          // DontDestroyOnLoad requires a root GameObject
            DontDestroyOnLoad(gameObject);
            _doc = GetComponent<UIDocument>();
        }

        private void OnEnable()
        {
            // Destroy() is deferred to end-of-frame, so a duplicate's OnEnable
            // still fires before destruction — bail before touching _doc (null).
            if (_isDuplicate) return;
            BindUi();
            Application.logMessageReceived += OnLogMessage;
            DrainPending();
        }

        private void OnDisable()
        {
            if (_isDuplicate) return;
            Application.logMessageReceived -= OnLogMessage;
            UnbindUi();
        }

        private void OnDestroy()
        {
            if (_instance == this) _instance = null;
        }

        private void BindUi()
        {
            var root = _doc.rootVisualElement;
            if (root == null) return;

            _panel  = root.Q<VisualElement>("log-panel");
            _bubble = root.Q<VisualElement>("log-bubble");
            _handle = root.Q<VisualElement>("log-handle");
            _scroll = root.Q<ScrollView>("log-scroll");
            _content = _scroll?.contentContainer;
            _resizeTL = root.Q<VisualElement>("resize-handle-tl");
            _resizeTR = root.Q<VisualElement>("resize-handle-tr");
            _resizeBL = root.Q<VisualElement>("resize-handle-bl");
            _resizeBR = root.Q<VisualElement>("resize-handle-br");

            RestorePersistedRect();

            // Drag wiring on the handle row. We use RegisterCallback rather
            // than a Manipulator subclass — single-instance overlay, no
            // re-use, no extra ceremony required.
            if (_handle != null)
            {
                _handle.RegisterCallback<PointerDownEvent>(OnHandleDown);
                _handle.RegisterCallback<PointerMoveEvent>(OnHandleMove);
                _handle.RegisterCallback<PointerUpEvent>(OnHandleUp);
                _handle.RegisterCallback<PointerCaptureOutEvent>(OnHandleCaptureOut);
            }

            RegisterResizeHandle(_resizeTL, ResizeCorner.TopLeft);
            RegisterResizeHandle(_resizeTR, ResizeCorner.TopRight);
            RegisterResizeHandle(_resizeBL, ResizeCorner.BottomLeft);
            RegisterResizeHandle(_resizeBR, ResizeCorner.BottomRight);

            BindButton(root, "btn-overlay-clear", Clear);
            BindButton(root, "btn-overlay-hide",  Hide);

            // Bubble (collapsed show-button): draggable to reposition; a plain
            // tap (no drag past threshold) restores the panel. The gesture is
            // driven on the bubble CONTAINER (not the inner Button) — a uGUI/UITK
            // Button has a built-in Clickable manipulator that captures the
            // pointer and fights manual capture, so we make the inner button
            // non-pickable and let the container own the whole tap/drag gesture.
            var showBtn = root.Q<Button>("btn-overlay-show");
            if (showBtn != null) showBtn.pickingMode = PickingMode.Ignore;
            if (_bubble != null)
            {
                _bubble.RegisterCallback<PointerDownEvent>(OnBubbleDown);
                _bubble.RegisterCallback<PointerMoveEvent>(OnBubbleMove);
                _bubble.RegisterCallback<PointerUpEvent>(OnBubbleUp);
                _bubble.RegisterCallback<PointerCaptureOutEvent>(OnBubbleCaptureOut);
            }

            // Filter popup — header ▾ trigger opens it; outside click closes.
            // Popup hosts the same multi-select toggles, kept hidden until
            // explicitly requested so the panel doesn't waste space.
            _filterBtn   = root.Q<Button>("btn-overlay-filter");
            _filterPopup = root.Q<VisualElement>("log-filter-popup");
            if (_filterBtn != null) _filterBtn.clicked += ToggleFilterPopup;
            root.RegisterCallback<PointerDownEvent>(OnRootPointerDown, TrickleDown.TrickleDown);

            _tgEvent   = WireFilterToggle(root, "tg-filter-event",   PrefFEvent,   v => _fEvent   = v, ref _fEvent);
            _tgSdk     = WireFilterToggle(root, "tg-filter-sdk",     PrefFSdk,     v => _fSdk     = v, ref _fSdk);
            _tgMisc    = WireFilterToggle(root, "tg-filter-misc",    PrefFMisc,    v => _fMisc    = v, ref _fMisc);
            _tgInfo    = WireFilterToggle(root, "tg-filter-info",    PrefFInfo,    v => _fInfo    = v, ref _fInfo);
            _tgSuccess = WireFilterToggle(root, "tg-filter-success", PrefFSuccess, v => _fSuccess = v, ref _fSuccess);
            _tgWarn    = WireFilterToggle(root, "tg-filter-warn",    PrefFWarn,    v => _fWarn    = v, ref _fWarn);
            _tgError   = WireFilterToggle(root, "tg-filter-error",   PrefFError,   v => _fError   = v, ref _fError);

            // Detail modal — populated + shown on row click.
            _detailModal    = root.Q<VisualElement>("log-detail-modal");
            _detailBackdrop = root.Q<VisualElement>("log-detail-backdrop");
            _detailBadge    = root.Q<Label>("log-detail-badge");
            _detailTitle    = root.Q<Label>("log-detail-title");
            _detailFields   = root.Q<VisualElement>("log-detail-fields");
            _detailRaw      = root.Q<Label>("log-detail-raw");
            _detailBackdrop?.RegisterCallback<ClickEvent>(_ => CloseDetail());
            BindButton(root, "btn-detail-close", CloseDetail);
        }

        private void RestorePersistedRect()
        {
            if (_panel == null) return;

            // Restore width/height first (before position) so worldBound
            // measurements during the drag clamp see the right size.
            if (PlayerPrefs.HasKey(PrefW) && PlayerPrefs.HasKey(PrefH))
            {
                _panel.style.width  = PlayerPrefs.GetFloat(PrefW);
                _panel.style.height = PlayerPrefs.GetFloat(PrefH);
            }

            // Restore last drag position. Default placement (right/bottom) is
            // set in USS; once the user drags, we switch to left/top + clear
            // right/bottom.
            if (PlayerPrefs.HasKey(PrefX) && PlayerPrefs.HasKey(PrefY))
            {
                _panel.style.left   = PlayerPrefs.GetFloat(PrefX);
                _panel.style.top    = PlayerPrefs.GetFloat(PrefY);
                _panel.style.right  = StyleKeyword.Auto;
                _panel.style.bottom = StyleKeyword.Auto;
            }

            // Restore last bubble position (USS default = bottom-right corner).
            if (_bubble != null && PlayerPrefs.HasKey(PrefBubbleX) && PlayerPrefs.HasKey(PrefBubbleY))
            {
                _bubble.style.left   = PlayerPrefs.GetFloat(PrefBubbleX);
                _bubble.style.top    = PlayerPrefs.GetFloat(PrefBubbleY);
                _bubble.style.right  = StyleKeyword.Auto;
                _bubble.style.bottom = StyleKeyword.Auto;
            }
        }

        private void RegisterResizeHandle(VisualElement? h, ResizeCorner corner)
        {
            if (h == null) return;
            // Closures capture `corner` so we don't need 4 distinct methods.
            h.RegisterCallback<PointerDownEvent>(evt => OnResizeDown(evt, h, corner));
            h.RegisterCallback<PointerMoveEvent>(evt => OnResizeMove(evt));
            h.RegisterCallback<PointerUpEvent>(evt => OnResizeUp(evt, h));
            h.RegisterCallback<PointerCaptureOutEvent>(evt => _resizing = false);
        }

        private void UnbindUi()
        {
            if (_handle != null)
            {
                _handle.UnregisterCallback<PointerDownEvent>(OnHandleDown);
                _handle.UnregisterCallback<PointerMoveEvent>(OnHandleMove);
                _handle.UnregisterCallback<PointerUpEvent>(OnHandleUp);
                _handle.UnregisterCallback<PointerCaptureOutEvent>(OnHandleCaptureOut);
            }
            if (_bubble != null)
            {
                _bubble.UnregisterCallback<PointerDownEvent>(OnBubbleDown);
                _bubble.UnregisterCallback<PointerMoveEvent>(OnBubbleMove);
                _bubble.UnregisterCallback<PointerUpEvent>(OnBubbleUp);
                _bubble.UnregisterCallback<PointerCaptureOutEvent>(OnBubbleCaptureOut);
            }
            // Resize handles use lambda callbacks captured at registration —
            // since they're closure-allocated, exact-handle unregister isn't
            // straightforward. Dropping refs is sufficient: the elements are
            // owned by the UIDocument's visual tree, which is torn down on
            // OnDisable anyway.
            _panel = _bubble = _handle = null;
            _resizeTL = _resizeTR = _resizeBL = _resizeBR = null;
            _scroll = null;
            _content = null;
        }

        private static void BindButton(VisualElement root, string name, Action callback)
        {
            var btn = root.Q<Button>(name);
            if (btn != null) btn.clicked += callback;
        }

        // --- Log ingestion ---------------------------------------------------

        private void OnLogMessage(string condition, string stackTrace, LogType type)
        {
            if (!MatchesPrefix(condition)) return;

            if (_content == null)
            {
                _pending.Enqueue((condition, type));
                return;
            }
            AppendLine(condition, type);
        }

        private void DrainPending()
        {
            while (_pending.Count > 0 && _content != null)
            {
                var (cond, type) = _pending.Dequeue();
                AppendLine(cond, type);
            }
        }

        private bool MatchesPrefix(string condition)
        {
            if (condition.Contains(PrefixSdk)) return true;
            if (!string.IsNullOrEmpty(consumerLogPrefix) && condition.Contains(consumerLogPrefix)) return true;
            return false;
        }

        private void AppendLine(string condition, LogType type)
        {
            if (_content == null || _scroll == null) return;

            var entry = LogParser.Parse(condition, type, consumerLogPrefix);
            var row = BuildRow(entry);
            _content.Add(row);

            // FIFO prune. Keeps layout cost bounded.
            while (_content.childCount > MaxLines)
            {
                _content.RemoveAt(0);
            }

            // Coalesce auto-scroll. Multiple AppendLine calls in the same
            // frame schedule a single scroll-to-bottom on frame end.
            if (!_scrollScheduled)
            {
                _scrollScheduled = true;
                _scroll.schedule.Execute(() =>
                {
                    _scrollScheduled = false;
                    if (_scroll != null)
                    {
                        _scroll.verticalScroller.value = _scroll.verticalScroller.highValue;
                    }
                });
            }
        }

        /// <summary>
        /// Compose a clickable row from a parsed <see cref="LogEntry"/>.
        /// Layout: [time] [badge] [main text] [meta]. Click → detail modal.
        /// Severity (LogType) overrides source colour for warn/error.
        /// </summary>
        private VisualElement BuildRow(LogEntry e)
        {
            var row = new VisualElement();
            row.AddToClassList("log-row");
            row.userData = e;
            row.RegisterCallback<ClickEvent>(OnRowClick);
            // Respect filter at construction so freshly-appended rows don't
            // flash before ApplyFilterToRows reaches them on next change.
            if (!PassesFilter(e)) row.style.display = DisplayStyle.None;

            // Severity colour wins over source colour — an SDK error still
            // reads red, an ad-event success still reads green.
            switch (e.Type)
            {
                case LogType.Error:
                case LogType.Exception:
                    row.AddToClassList("log-row--error");
                    break;
                case LogType.Warning:
                    row.AddToClassList("log-row--warn");
                    break;
                default:
                    if (e.IsSuccess) row.AddToClassList("log-row--success");
                    break;
            }

            // Timestamp — muted; let user scan time-of-day at a glance.
            var time = new Label(e.Timestamp.ToString("HH:mm:ss"));
            time.AddToClassList("log-row-time");
            row.Add(time);

            // Badge — source + (for ad events) format. Class modifier drives
            // per-category colour.
            var badge = new Label(BadgeText(e));
            badge.AddToClassList("log-row-badge");
            badge.AddToClassList(BadgeModifier(e));
            row.Add(badge);

            // Main text — event name for ad events, condensed headline for
            // anything else. Headline strips trailing `key=value` clauses so
            // the row stays scannable; full text lives in the detail modal.
            var mainText = e.Source == LogSource.SampleAdEvent && e.Event != null
                ? e.Event
                : Headline(e.Message);
            var main = new Label(mainText);
            main.AddToClassList("log-row-text");
            row.Add(main);

            // Trailing meta — latency for success/info ad events, error code
            // for failures. Hidden if neither applies.
            var meta = TrailingMeta(e);
            if (!string.IsNullOrEmpty(meta))
            {
                var m = new Label(meta);
                m.AddToClassList("log-row-meta");
                row.Add(m);
            }

            return row;
        }

        private static string BadgeText(LogEntry e) => e.Source switch
        {
            LogSource.SampleAdEvent       => e.Area,            // "Banner", "Interstitial", ...
            LogSource.SdkPublicEventTrace => e.Area,            // same area name, lighter shade
            LogSource.SdkInternal         => e.Area,            // muted shade
            LogSource.SampleMisc          => "App",
            _                             => "?",
        };

        private static string BadgeModifier(LogEntry e) => e.Source switch
        {
            LogSource.SampleAdEvent       => "log-row-badge--ad",     // solid brand purple — consumer-facing flow
            LogSource.SdkPublicEventTrace => "log-row-badge--sdk-pub",// lighter brand purple — same event, SDK side
            LogSource.SdkInternal         => "log-row-badge--sdk",    // muted purple — pure plumbing
            LogSource.SampleMisc          => "log-row-badge--misc",
            _                             => "log-row-badge--misc",
        };

        private static string TrailingMeta(LogEntry e)
        {
            if (e.ErrorCode.HasValue) return $"code={e.ErrorCode.Value}";
            if (e.Latency.HasValue)   return $"{e.Latency.Value}ms";
            return "";
        }

        /// <summary>
        /// Strip trailing <c>key=value</c> clauses from a SDK / misc message
        /// so the row main text stays scannable. Cuts at the space before
        /// the first <c>=</c>: e.g. <c>"FireOnAdLoaded adUnit='x'
        /// latency=234"</c> → <c>"FireOnAdLoaded"</c>, <c>"Banner.load JNI
        /// entry adUnit='x'"</c> → <c>"Banner.load JNI entry"</c>. Messages
        /// without an <c>=</c> are returned unchanged.
        /// </summary>
        private static string Headline(string message)
        {
            if (string.IsNullOrEmpty(message)) return message;
            var eq = message.IndexOf('=');
            if (eq <= 0) return message;
            var sp = message.LastIndexOf(' ', eq);
            return sp > 0 ? message.Substring(0, sp) : message;
        }

        private void OnRowClick(ClickEvent evt)
        {
            // userData is the LogEntry stored when the row was built — pulled
            // back out here rather than parsing the visible labels.
            var row = evt.currentTarget as VisualElement;
            var entry = row?.userData as LogEntry;
            if (entry == null) return;
            OpenDetail(entry);
        }

        // --- Detail modal ---------------------------------------------------

        private void OpenDetail(LogEntry e)
        {
            if (_detailModal == null) return;

            if (_detailBadge != null)
            {
                _detailBadge.text = BadgeText(e);
                // Reset and re-apply badge modifier so colour matches the row.
                _detailBadge.RemoveFromClassList("log-row-badge--ad");
                _detailBadge.RemoveFromClassList("log-row-badge--sdk-pub");
                _detailBadge.RemoveFromClassList("log-row-badge--sdk");
                _detailBadge.RemoveFromClassList("log-row-badge--misc");
                _detailBadge.AddToClassList(BadgeModifier(e));
            }
            if (_detailTitle != null)
            {
                _detailTitle.text = e.Source switch
                {
                    LogSource.SampleAdEvent       when e.Event != null => e.Event,
                    LogSource.SampleMisc                               => "App",
                    LogSource.SdkPublicEventTrace                      => "SDK · event trace",
                    LogSource.SdkInternal                              => "SDK · internal",
                    _                                                  => "Log",
                };
            }
            if (_detailFields != null)
            {
                _detailFields.Clear();
                AddField(_detailFields, "Time",     e.Timestamp.ToString("HH:mm:ss.fff"));
                AddField(_detailFields, "Source",   e.Source.ToString());
                if (!string.IsNullOrEmpty(e.Area))         AddField(_detailFields, "Area",     e.Area);
                if (!string.IsNullOrEmpty(e.Event))        AddField(_detailFields, "Event",    e.Event!);
                if (e.Latency.HasValue)                    AddField(_detailFields, "Latency",  $"{e.Latency.Value}ms");
                if (e.ErrorCode.HasValue)                  AddField(_detailFields, "ErrorCode",e.ErrorCode.Value.ToString());
                if (!string.IsNullOrEmpty(e.AdUnitId))     AddField(_detailFields, "AdUnitId", e.AdUnitId!);
                if (!string.IsNullOrEmpty(e.ErrorMessage)) AddField(_detailFields, "ErrorMsg", e.ErrorMessage!);
                AddField(_detailFields, "Type", e.Type.ToString());
            }
            if (_detailRaw != null)
            {
                _detailRaw.text = e.RawCondition;
            }
            _detailModal.style.display = DisplayStyle.Flex;
        }

        private static void AddField(VisualElement parent, string key, string value)
        {
            var row = new VisualElement();
            row.AddToClassList("log-detail-field");
            var k = new Label(key);
            k.AddToClassList("log-detail-field-key");
            var v = new Label(value);
            v.AddToClassList("log-detail-field-value");
            row.Add(k);
            row.Add(v);
            parent.Add(row);
        }

        public void CloseDetail()
        {
            if (_detailModal != null) _detailModal.style.display = DisplayStyle.None;
        }

        // --- Filter ---------------------------------------------------------

        private delegate void FilterStateSetter(bool value);

        /// <summary>
        /// Wires a single filter toggle: pulls initial state from PlayerPrefs
        /// (default ON), reflects it onto the local state mirror, and pushes
        /// changes back to both state + PlayerPrefs on every user click. The
        /// `ref` mirror lets the caller bind a setter expression without a
        /// closure box.
        /// </summary>
        private Toggle? WireFilterToggle(VisualElement root, string name, string prefKey, FilterStateSetter setter, ref bool stateMirror)
        {
            var tg = root.Q<Toggle>(name);
            var initial = PlayerPrefs.GetInt(prefKey, 1) != 0;
            stateMirror = initial;
            if (tg == null) return null;
            tg.SetValueWithoutNotify(initial);
            tg.RegisterValueChangedCallback(evt =>
            {
                setter(evt.newValue);
                PlayerPrefs.SetInt(prefKey, evt.newValue ? 1 : 0);
                ApplyFilterToRows();
            });
            return tg;
        }

        private bool PassesFilter(LogEntry e)
        {
            // Source dim — 3-way visible categories (Event / SDK / App)
            // backed by the 4-source parser model. SampleAdEvent and
            // SdkPublicEventTrace both describe the same public ad event
            // firing from two sides, so a single "Event" toggle gates both.
            // Detail modal still exposes the precise parser Source for
            // developers who need the distinction. Unknown always passes
            // (safety: never silently drop something the parser didn't
            // recognise).
            bool srcOk = e.Source switch
            {
                LogSource.SampleAdEvent       => _fEvent,
                LogSource.SdkPublicEventTrace => _fEvent,
                LogSource.SdkInternal         => _fSdk,
                LogSource.SampleMisc          => _fMisc,
                _                             => true,
            };
            if (!srcOk) return false;

            // Level dim — map LogType + IsSuccess to one of four toggles.
            bool lvlOk = e.Type switch
            {
                LogType.Error or LogType.Exception => _fError,
                LogType.Warning                    => _fWarn,
                _                                  => e.IsSuccess ? _fSuccess : _fInfo,
            };
            return lvlOk;
        }

        private void ApplyFilterToRows()
        {
            if (_content == null) return;
            for (int i = 0; i < _content.childCount; i++)
            {
                var row = _content[i];
                if (row.userData is LogEntry entry)
                {
                    row.style.display = PassesFilter(entry) ? DisplayStyle.Flex : DisplayStyle.None;
                }
            }
        }

        private void ToggleFilterPopup()
        {
            if (_filterPopup == null) return;
            var visible = _filterPopup.resolvedStyle.display == DisplayStyle.Flex;
            _filterPopup.style.display = visible ? DisplayStyle.None : DisplayStyle.Flex;
        }

        private void OnRootPointerDown(PointerDownEvent evt)
        {
            // Close the filter popup on any click outside the popup itself or
            // its trigger button. TrickleDown lets us see the down before the
            // popup's own children consume it.
            if (_filterPopup == null) return;
            if (_filterPopup.resolvedStyle.display != DisplayStyle.Flex) return;
            var target = evt.target as VisualElement;
            while (target != null)
            {
                if (target == _filterPopup || target == _filterBtn) return;
                target = target.parent;
            }
            _filterPopup.style.display = DisplayStyle.None;
        }

        // --- Public commands -------------------------------------------------

        public void Clear()
        {
            _content?.Clear();
        }

        public void Hide()
        {
            if (_panel != null)  _panel.style.display  = DisplayStyle.None;
            if (_bubble != null) _bubble.style.display = DisplayStyle.Flex;
        }

        public void Show()
        {
            if (_panel != null)  _panel.style.display  = DisplayStyle.Flex;
            if (_bubble != null) _bubble.style.display = DisplayStyle.None;
        }

        // --- Drag handler ---------------------------------------------------

        private void OnHandleDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _panel == null || _handle == null) return;
            _dragging = true;
            // Offset from panel top-left in panel-space coordinates so the
            // grabbed point stays under the cursor while moving.
            _dragOffset = (Vector2)evt.position - _panel.worldBound.position;
            _handle.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnHandleMove(PointerMoveEvent evt)
        {
            if (!_dragging || _panel == null) return;

            var target = (Vector2)evt.position - _dragOffset;

            // Clamp inside parent (= log-root, full screen). Use resolvedStyle
            // because style.* may still be Auto on first frame.
            var parent = _panel.parent;
            if (parent != null)
            {
                var maxX = Mathf.Max(0f, parent.resolvedStyle.width  - _panel.resolvedStyle.width);
                var maxY = Mathf.Max(0f, parent.resolvedStyle.height - _panel.resolvedStyle.height);
                target.x = Mathf.Clamp(target.x, 0f, maxX);
                target.y = Mathf.Clamp(target.y, 0f, maxY);
            }

            _panel.style.left   = target.x;
            _panel.style.top    = target.y;
            _panel.style.right  = StyleKeyword.Auto;
            _panel.style.bottom = StyleKeyword.Auto;
        }

        private void OnHandleUp(PointerUpEvent evt)
        {
            if (!_dragging || _handle == null) return;
            _dragging = false;
            _handle.ReleasePointer(evt.pointerId);
            PersistPosition();
        }

        private void OnHandleCaptureOut(PointerCaptureOutEvent evt)
        {
            // Safety net — if the pointer capture is stolen (panel re-build,
            // hot reload, etc.) we still need to end the drag state.
            _dragging = false;
        }

        // --- Bubble drag (tap restores; drag repositions) -------------------

        private void OnBubbleDown(PointerDownEvent evt)
        {
            if (evt.button != 0 || _bubble == null) return;
            _bubbleDragging   = true;
            _bubbleMoved      = false;
            _bubbleDownPos    = evt.position;
            _bubbleDragOffset = (Vector2)evt.position - _bubble.worldBound.position;
            (evt.currentTarget as VisualElement)?.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnBubbleMove(PointerMoveEvent evt)
        {
            if (!_bubbleDragging || _bubble == null) return;

            // Promote to a drag only past the threshold so a small finger
            // wobble during a tap still counts as a tap (→ restore).
            if (!_bubbleMoved &&
                Vector2.Distance(evt.position, _bubbleDownPos) > BubbleDragThreshold)
                _bubbleMoved = true;
            if (!_bubbleMoved) return;

            var target = (Vector2)evt.position - _bubbleDragOffset;
            var parent = _bubble.parent;
            if (parent != null)
            {
                var maxX = Mathf.Max(0f, parent.resolvedStyle.width  - _bubble.resolvedStyle.width);
                var maxY = Mathf.Max(0f, parent.resolvedStyle.height - _bubble.resolvedStyle.height);
                target.x = Mathf.Clamp(target.x, 0f, maxX);
                target.y = Mathf.Clamp(target.y, 0f, maxY);
            }
            _bubble.style.left   = target.x;
            _bubble.style.top    = target.y;
            _bubble.style.right  = StyleKeyword.Auto;
            _bubble.style.bottom = StyleKeyword.Auto;
        }

        private void OnBubbleUp(PointerUpEvent evt)
        {
            if (!_bubbleDragging) return;
            _bubbleDragging = false;
            (evt.currentTarget as VisualElement)?.ReleasePointer(evt.pointerId);
            if (_bubbleMoved) PersistBubblePosition();
            else              Show();   // plain tap → restore panel
            evt.StopPropagation();
        }

        private void OnBubbleCaptureOut(PointerCaptureOutEvent evt) => _bubbleDragging = false;

        private void PersistBubblePosition()
        {
            if (_bubble == null) return;
            var left = _bubble.resolvedStyle.left;
            var top  = _bubble.resolvedStyle.top;
            if (float.IsNaN(left) || float.IsNaN(top)) return;
            PlayerPrefs.SetFloat(PrefBubbleX, left);
            PlayerPrefs.SetFloat(PrefBubbleY, top);
        }

        private void PersistPosition()
        {
            if (_panel == null) return;
            var left = _panel.resolvedStyle.left;
            var top  = _panel.resolvedStyle.top;
            if (float.IsNaN(left) || float.IsNaN(top)) return;
            PlayerPrefs.SetFloat(PrefX, left);
            PlayerPrefs.SetFloat(PrefY, top);
        }

        // --- Resize handler -------------------------------------------------

        private void OnResizeDown(PointerDownEvent evt, VisualElement handle, ResizeCorner corner)
        {
            if (evt.button != 0 || _panel == null) return;
            _resizing = true;
            _resizeCorner = corner;
            _resizeStartPointer = evt.position;
            // Capture the panel's current rect in parent coordinates. worldBound
            // is in the panel's own panel space (= parent space since both
            // share log-root's coordinate system).
            _resizeStartRect = _panel.worldBound;
            // Switch panel to explicit left/top + width/height before mutating
            // so right/bottom defaults don't fight our updates.
            _panel.style.left   = _resizeStartRect.x;
            _panel.style.top    = _resizeStartRect.y;
            _panel.style.width  = _resizeStartRect.width;
            _panel.style.height = _resizeStartRect.height;
            _panel.style.right  = StyleKeyword.Auto;
            _panel.style.bottom = StyleKeyword.Auto;
            handle.CapturePointer(evt.pointerId);
            evt.StopPropagation();
        }

        private void OnResizeMove(PointerMoveEvent evt)
        {
            if (!_resizing || _panel == null) return;

            var delta = (Vector2)evt.position - _resizeStartPointer;
            float left   = _resizeStartRect.x;
            float top    = _resizeStartRect.y;
            float width  = _resizeStartRect.width;
            float height = _resizeStartRect.height;

            switch (_resizeCorner)
            {
                case ResizeCorner.BottomRight:
                    width  += delta.x;
                    height += delta.y;
                    break;
                case ResizeCorner.BottomLeft:
                    left   += delta.x;
                    width  -= delta.x;
                    height += delta.y;
                    break;
                case ResizeCorner.TopRight:
                    top    += delta.y;
                    width  += delta.x;
                    height -= delta.y;
                    break;
                case ResizeCorner.TopLeft:
                    left   += delta.x;
                    top    += delta.y;
                    width  -= delta.x;
                    height -= delta.y;
                    break;
            }

            // Min-size clamp. When the user drags past the min on a corner that
            // also moves the panel's origin (TL/TR/BL on the affected axis),
            // pin the moving edge so the opposite edge stays put visually.
            if (width < MinWidth)
            {
                if (_resizeCorner == ResizeCorner.BottomLeft || _resizeCorner == ResizeCorner.TopLeft)
                {
                    left = _resizeStartRect.xMax - MinWidth;
                }
                width = MinWidth;
            }
            if (height < MinHeight)
            {
                if (_resizeCorner == ResizeCorner.TopLeft || _resizeCorner == ResizeCorner.TopRight)
                {
                    top = _resizeStartRect.yMax - MinHeight;
                }
                height = MinHeight;
            }

            // Parent-bounds clamp.
            var parent = _panel.parent;
            if (parent != null)
            {
                var maxW = parent.resolvedStyle.width;
                var maxH = parent.resolvedStyle.height;
                if (left < 0f)            { width += left; left = 0f; }
                if (top  < 0f)            { height += top; top  = 0f; }
                if (left + width > maxW)  { width  = maxW - left; }
                if (top  + height > maxH) { height = maxH - top; }
            }

            _panel.style.left   = left;
            _panel.style.top    = top;
            _panel.style.width  = width;
            _panel.style.height = height;
        }

        private void OnResizeUp(PointerUpEvent evt, VisualElement handle)
        {
            if (!_resizing) return;
            _resizing = false;
            handle.ReleasePointer(evt.pointerId);
            PersistRect();
        }

        private void PersistRect()
        {
            if (_panel == null) return;
            var left   = _panel.resolvedStyle.left;
            var top    = _panel.resolvedStyle.top;
            var width  = _panel.resolvedStyle.width;
            var height = _panel.resolvedStyle.height;
            if (float.IsNaN(left) || float.IsNaN(top) || float.IsNaN(width) || float.IsNaN(height)) return;
            PlayerPrefs.SetFloat(PrefX, left);
            PlayerPrefs.SetFloat(PrefY, top);
            PlayerPrefs.SetFloat(PrefW, width);
            PlayerPrefs.SetFloat(PrefH, height);
        }
    }
}
