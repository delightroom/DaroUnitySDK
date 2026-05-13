#nullable enable

using System;
using UnityEngine;
using UnityEngine.UI;

namespace Daro
{
    /// <summary>
    /// Slot-path bridge between a publisher's Unity UI prefab and a
    /// <see cref="DaroNativeAd"/>. Attach to the prefab root, wire the slots
    /// in the Inspector, then call <see cref="Bind"/> after
    /// <see cref="DaroNativeAd.OnAdLoaded"/> fires.
    /// </summary>
    /// <remarks>
    /// <para>All slots are optional — publisher wires only the slots present
    /// in their layout. Slots use legacy Unity UI (<see cref="Text"/>,
    /// <see cref="RawImage"/>, <see cref="Button"/>) for Unity 2019.4
    /// compatibility. <see cref="RawImage"/> over <see cref="Image"/> because
    /// the icon comes as a raw <see cref="Texture2D"/>, not a <c>Sprite</c>.
    /// Publishers needing TextMeshPro / custom widgets should use the raw path
    /// (read <see cref="DaroNativeAd.Info"/> directly into their own UI).</para>
    ///
    /// <para><b>Visibility tracking (lightweight)</b>:
    /// <see cref="OnEnable"/> → <see cref="DaroNativeAd.NotifyVisible"/>;
    /// <see cref="OnDisable"/> → <see cref="DaroNativeAd.NotifyHidden"/>.
    /// v1 Android shim only logs these — MAX billing impression fires
    /// independently via the revenue listener.</para>
    ///
    /// <para><b>Click wiring</b>: <see cref="Bind"/> adds an
    /// <see cref="OnCtaClicked"/> listener on <see cref="CtaButton"/>'s
    /// <c>onClick</c>; <see cref="Unbind"/> removes it.</para>
    /// </remarks>
    [AddComponentMenu("Daro/Native Ad View")]
    public sealed class DaroNativeAdView : MonoBehaviour
    {
        // ── Inspector slots (all optional) ───────────────────────────────
        [SerializeField] public Text?     TitleText;
        [SerializeField] public Text?     BodyText;
        [SerializeField] public RawImage? IconImage;
        [SerializeField] public Button?   CtaButton;
        [SerializeField] public RawImage? MediaContainer;

        // ── Bound state ──────────────────────────────────────────────────
        private DaroNativeAd? _boundAd;

        /// <summary>
        /// Capture the prefab's IconImage RectTransform pixel size and write it
        /// to <see cref="DaroNativeAd.IconSize"/>. Most publishers should call
        /// <see cref="LoadFor(DaroNativeAd)"/> instead, which combines this with
        /// <see cref="DaroNativeAd.Load"/> so the size is guaranteed applied —
        /// this method is exposed for advanced cases where the caller wants to
        /// apply the hint then defer / customize the load.
        /// No-op if either argument is null or this view's <see cref="IconImage"/>
        /// slot is unwired.
        /// </summary>
        public void ApplySizeHints(DaroNativeAd ad)
        {
            if (ad == null || IconImage == null) return;
            var size = IconImage.rectTransform.rect.size;
            ad.IconSize = new Vector2Int(
                Mathf.Max(1, Mathf.RoundToInt(size.x)),
                Mathf.Max(1, Mathf.RoundToInt(size.y))
            );
        }

        /// <summary>
        /// Load <paramref name="ad"/> with this view's IconImage RectTransform
        /// size as the icon-resolution hint. Convenience over
        /// <see cref="ApplySizeHints"/> + <see cref="DaroNativeAd.Load"/> — the
        /// recommended slot-path entry point because the size hint can't be
        /// forgotten. Raw-path publishers (without a <see cref="DaroNativeAdView"/>)
        /// continue to set <see cref="DaroNativeAd.IconSize"/> manually + call
        /// <see cref="DaroNativeAd.Load"/> directly.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="ad"/> is null.</exception>
        public void LoadFor(DaroNativeAd ad)
        {
            if (ad == null) throw new ArgumentNullException(nameof(ad));
            ApplySizeHints(ad);
            ad.Load();
        }

        /// <summary>
        /// Populate slots from <paramref name="ad"/>'s <see cref="DaroNativeAd.Info"/>
        /// and wire CTA click. Calls <see cref="Unbind"/> first if a different ad
        /// is already bound.
        /// </summary>
        /// <exception cref="ArgumentNullException">Thrown if <paramref name="ad"/> is null.</exception>
        /// <exception cref="InvalidOperationException">Thrown if <paramref name="ad"/> is not ready.</exception>
        public void Bind(DaroNativeAd ad)
        {
            if (ad == null) throw new ArgumentNullException(nameof(ad));
            if (!ad.IsReady)
                throw new InvalidOperationException(
                    "Cannot Bind a DaroNativeAd that is not ready.");

            Unbind();

            _boundAd = ad;

            var info = ad.Info!;
            if (TitleText      != null) TitleText.text         = info.Title       ?? string.Empty;
            if (BodyText       != null) BodyText.text          = info.Body        ?? string.Empty;
            if (IconImage      != null) IconImage.texture      = info.Icon;
            if (MediaContainer != null) MediaContainer.texture = info.MediaImage;

            if (CtaButton != null)
            {
                var ctaText = CtaButton.GetComponentInChildren<Text>();
                if (ctaText != null) ctaText.text = info.CallToAction ?? string.Empty;
                CtaButton.onClick.AddListener(OnCtaClicked);
            }
        }

        /// <summary>
        /// Detach the currently-bound ad. Clears slot text / textures and
        /// removes the CTA click listener. Safe to call when nothing is bound.
        /// </summary>
        public void Unbind()
        {
            if (_boundAd == null) return;

            if (CtaButton != null) CtaButton.onClick.RemoveListener(OnCtaClicked);

            if (TitleText      != null) TitleText.text         = string.Empty;
            if (BodyText       != null) BodyText.text          = string.Empty;
            if (IconImage      != null) IconImage.texture      = null;
            if (MediaContainer != null) MediaContainer.texture = null;
            if (CtaButton != null)
            {
                var ct = CtaButton.GetComponentInChildren<Text>();
                if (ct != null) ct.text = string.Empty;
            }

            _boundAd = null;
        }

        // ── Visibility hooks (v1 lightweight) ────────────────────────────

        private void OnEnable()  { _boundAd?.NotifyVisible(); }
        private void OnDisable() { _boundAd?.NotifyHidden();  }

        // Prevent dangling visibility-hidden / click-listener state if the
        // GameObject is destroyed without an explicit Unbind call.
        private void OnDestroy() => Unbind();

        // ── Click ────────────────────────────────────────────────────────

        private void OnCtaClicked() { _boundAd?.NotifyClicked(); }
    }
}
