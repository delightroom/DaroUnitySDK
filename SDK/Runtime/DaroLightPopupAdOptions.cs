#nullable enable
using UnityEngine;

namespace Daro
{
    /// <summary>
    /// Color + label customization for <see cref="DaroLightPopupAd"/>. Mirrors
    /// daro-m's <c>droom.daro.core.model.DaroLightPopupAdOptions</c> data class
    /// — defaults are byte-for-byte identical to daro-m's hex defaults so a
    /// consumer who supplies <c>null</c> options sees the same modal as a
    /// platform-native consumer.
    /// </summary>
    /// <remarks>
    /// <para>Class (not struct) + field initializers — sketch decision §3.
    /// Struct zero-init would yield <c>(0,0,0,0)</c> on every field, i.e. fully
    /// transparent black, silently overriding the daro-m default palette. The
    /// <c>class</c> + field initializer combination guarantees daro-m defaults
    /// even if the consumer never assigns a single field.</para>
    ///
    /// <para>Selective override uses C# object-initializer syntax:
    /// <code>new DaroLightPopupAdOptions { TitleColor = Color.red }</code>.
    /// Untouched fields retain the daro-m default.</para>
    ///
    /// <para>Color encoding is <see cref="Color32"/> (RGBA byte) — Unity-idiomatic.
    /// Conversion to Android <c>android.graphics.Color</c> ARGB int happens
    /// inside the Kotlin shim (<c>DaroUnityLightPopupAd.kt</c>), not here.</para>
    /// </remarks>
    public sealed class DaroLightPopupAdOptions
    {
        /// <summary>Modal scrim. Default <c>#B2121416</c> — semi-transparent dark dimmer (alpha 0xB2).</summary>
        public Color32 BackgroundColor            = new Color32(0x12, 0x14, 0x16, 0xB2);

        /// <summary>Popup container fill. Default <c>#121416</c>.</summary>
        public Color32 ContainerColor             = new Color32(0x12, 0x14, 0x16, 0xFF);

        /// <summary>"AD" marker label text color. Default <c>#F7FAFF</c>.</summary>
        public Color32 AdMarkLabelTextColor       = new Color32(0xF7, 0xFA, 0xFF, 0xFF);

        /// <summary>"AD" marker label background. Default <c>#3E434F</c>.</summary>
        public Color32 AdMarkLabelBackgroundColor = new Color32(0x3E, 0x43, 0x4F, 0xFF);

        /// <summary>Ad title text color. Default <c>#F7FAFF</c>.</summary>
        public Color32 TitleColor                 = new Color32(0xF7, 0xFA, 0xFF, 0xFF);

        /// <summary>Ad body text color. Default <c>#B6BECC</c>.</summary>
        public Color32 BodyColor                  = new Color32(0xB6, 0xBE, 0xCC, 0xFF);

        /// <summary>CTA button background. Default <c>#EB2640</c>.</summary>
        public Color32 CtaBackgroundColor         = new Color32(0xEB, 0x26, 0x40, 0xFF);

        /// <summary>CTA button text color. Default <c>#FFFFFF</c>.</summary>
        public Color32 CtaTextColor               = new Color32(0xFF, 0xFF, 0xFF, 0xFF);

        /// <summary>Close button icon + text tint. Default <c>#F7FAFF</c>.</summary>
        public Color32 CloseButtonColor           = new Color32(0xF7, 0xFA, 0xFF, 0xFF);

        /// <summary>Close button label. Default <c>"Close"</c>.</summary>
        public string  CloseButtonText            = "Close";
    }
}
