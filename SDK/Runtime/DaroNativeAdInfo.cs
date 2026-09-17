#nullable enable
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Daro
{
    public enum NativeAdAssetType
    {
        Title, // 광고 제목
        Body, // 광고 본문
        Icon, // 광고 아이콘
        Media, // 광고 이미지 또는 동영상
        CallToAction, // 행동 유도 버튼 문구
        Advertiser // 광고주명
    }

    /// <summary>
    /// Native ad asset payload populated by the platform handle and exposed
    /// to the publisher via <see cref="DaroNativeAd.Info"/>. Bind through
    /// <see cref="DaroNativeAdView"/> (slot path) or read fields directly
    /// (raw path, custom layout).
    /// </summary>
    /// <remarks>
    /// All fields nullable — not every ad supplies every slot. Field set
    /// matches daro-m's <c>DaroNativeAdBinder</c> exposed surface (5 view IDs:
    /// title / body / cta / icon / mediaGroup); advertiser / star-rating are
    /// absent because daro-m doesn't surface them through its public binder.
    /// <see cref="MediaImage"/> is always <c>null</c> on Android v1
    /// (image-only scope; video deferred).
    /// </remarks>
    public sealed class DaroNativeAdInfo
    {
        private readonly ReadOnlyAssetSet _assetTypes;

        /// <summary>
        /// Immutable asset snapshot reused by every read. Mutating operations
        /// throw <see cref="NotSupportedException"/>; copy it to a HashSet to edit.
        /// </summary>
        public ISet<NativeAdAssetType> AssetTypes => _assetTypes;

        public string?    Title        { get; }
        public string?    Body         { get; }
        public string?    CallToAction { get; }
        public Texture2D? Icon         { get; }
        public Texture2D? MediaImage   { get; }

        /// <summary>
        /// Whether the native CTA is connected to the current ad view.
        /// When <c>false</c> (iOS only — Android/Editor always <c>true</c>),
        /// the overlay disables touches because its CTA is detached.
        /// This flag does not guarantee that a mediation network will
        /// report a click; it is independent of gesture recognizer placement.
        /// Publishers can hide an ad whose CTA is disconnected:
        /// <code>if (!ad.Info.IsCtaInteractive) view.gameObject.SetActive(false);</code>
        /// </summary>
        public bool       IsCtaInteractive { get; }

        public DaroNativeAdInfo(
            string?    title,
            string?    body,
            string?    callToAction,
            Texture2D? icon,
            Texture2D? mediaImage,
            bool       isCtaInteractive = true)
            : this(title, body, callToAction, icon, mediaImage, Array.Empty<NativeAdAssetType>(), isCtaInteractive)
        {
        }

        public DaroNativeAdInfo(
            string? title,
            string? body,
            string? callToAction,
            Texture2D? icon,
            Texture2D? mediaImage,
            IEnumerable<NativeAdAssetType> assetTypes,
            bool isCtaInteractive = true)
        {
            _assetTypes = new ReadOnlyAssetSet(assetTypes ?? throw new ArgumentNullException(nameof(assetTypes)));
            Title            = title;
            Body             = body;
            CallToAction     = callToAction;
            Icon             = icon;
            MediaImage       = mediaImage;
            IsCtaInteractive = isCtaInteractive;
        }

        internal HashSet<NativeAdAssetType> CopyAssetTypes() => _assetTypes.Copy();

        // Unity 2021.3 has no standard read-only set wrapper. Keep the owned
        // HashSet private so consumers cannot cast the snapshot to a mutable set.
        private sealed class ReadOnlyAssetSet : ISet<NativeAdAssetType>
        {
            private readonly HashSet<NativeAdAssetType> _items;

            internal ReadOnlyAssetSet(IEnumerable<NativeAdAssetType> items)
            {
                _items = new HashSet<NativeAdAssetType>(items);
            }

            // Copy the HashSet directly to avoid boxing its enumerator through ISet.
            internal HashSet<NativeAdAssetType> Copy() => new HashSet<NativeAdAssetType>(_items);

            public int Count => _items.Count;
            public bool IsReadOnly => true;
            public bool Contains(NativeAdAssetType item) => _items.Contains(item);
            public bool IsSubsetOf(IEnumerable<NativeAdAssetType> other) => _items.IsSubsetOf(other);
            public bool IsSupersetOf(IEnumerable<NativeAdAssetType> other) => _items.IsSupersetOf(other);
            public bool IsProperSubsetOf(IEnumerable<NativeAdAssetType> other) => _items.IsProperSubsetOf(other);
            public bool IsProperSupersetOf(IEnumerable<NativeAdAssetType> other) => _items.IsProperSupersetOf(other);
            public bool Overlaps(IEnumerable<NativeAdAssetType> other) => _items.Overlaps(other);
            public bool SetEquals(IEnumerable<NativeAdAssetType> other) => _items.SetEquals(other);
            public void CopyTo(NativeAdAssetType[] array, int arrayIndex) => _items.CopyTo(array, arrayIndex);
            public IEnumerator<NativeAdAssetType> GetEnumerator() => _items.GetEnumerator();
            IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

            bool ISet<NativeAdAssetType>.Add(NativeAdAssetType item) => throw ReadOnlyError();
            void ICollection<NativeAdAssetType>.Add(NativeAdAssetType item) => throw ReadOnlyError();
            bool ICollection<NativeAdAssetType>.Remove(NativeAdAssetType item) => throw ReadOnlyError();
            void ICollection<NativeAdAssetType>.Clear() => throw ReadOnlyError();
            void ISet<NativeAdAssetType>.ExceptWith(IEnumerable<NativeAdAssetType> other) => throw ReadOnlyError();
            void ISet<NativeAdAssetType>.IntersectWith(IEnumerable<NativeAdAssetType> other) => throw ReadOnlyError();
            void ISet<NativeAdAssetType>.SymmetricExceptWith(IEnumerable<NativeAdAssetType> other) => throw ReadOnlyError();
            void ISet<NativeAdAssetType>.UnionWith(IEnumerable<NativeAdAssetType> other) => throw ReadOnlyError();

            private static NotSupportedException ReadOnlyError() =>
                new NotSupportedException("Native ad asset snapshots are read-only.");
        }
    }
}

namespace Daro.Internal
{
    internal static class NativeAdAssetTypes
    {
        internal static NativeAdAssetType[] Parse(string? value)
        {
            var result = new List<NativeAdAssetType>();
            foreach (var name in (value ?? string.Empty).Split(','))
            {
                NativeAdAssetType asset;
                switch (name)
                {
                    case "TITLE": case "title": asset = NativeAdAssetType.Title; break;
                    case "BODY": case "body": asset = NativeAdAssetType.Body; break;
                    case "ICON": case "icon": asset = NativeAdAssetType.Icon; break;
                    case "MEDIA": case "media": asset = NativeAdAssetType.Media; break;
                    case "CALL_TO_ACTION": case "callToAction": asset = NativeAdAssetType.CallToAction; break;
                    case "ADVERTISER": case "advertiser": asset = NativeAdAssetType.Advertiser; break;
                    default: continue;
                }
                if (!result.Contains(asset)) result.Add(asset);
            }
            return result.ToArray();
        }
    }
}
