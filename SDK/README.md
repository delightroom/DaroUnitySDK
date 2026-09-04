# Daro Unity SDK

[English](README.md) | [한국어](README.ko.md)

Daro ad mediation adapter for Unity. This package is the MAX (AppLovin) mediation variant and supports Android and iOS.

## Installation

The recommended installation path is the bootstrap installer.

1. Download `DaroPackageInstaller.unitypackage` from the Daro website or from this repository's GitHub Releases.
2. Import it into your Unity project.
3. The installer patches `Packages/manifest.json` with the OpenUPM scoped registry, EDM4U, and `so.daro.unity`.
4. After installation, open **Daro > Integration Manager** from the Unity Editor menu bar to validate project setup. `Daro` is a top-level menu, not an entry under `Assets`.

## Requirements

- Unity 2021.3 LTS or newer
- Android Build Support and/or iOS Build Support for your target platforms
- EDM4U, installed by the bootstrap installer

## Dependencies

- Android uses a prebuilt Unity wrapper AAR plus native mediation dependencies resolved by EDM4U.
- iOS dependencies are resolved through EDM4U/CocoaPods during Xcode project generation.

## Supported ad formats

- Interstitial
- Rewarded
- App Open
- Banner
- Native
- Light Popup

## Documentation

- [Documentation index](https://github.com/delightroom/DaroUnitySDK/tree/main/SDK/Documentation~)
- [Integration guide](https://github.com/delightroom/DaroUnitySDK/blob/main/SDK/Documentation~/integration.md)
- [API reference](https://github.com/delightroom/DaroUnitySDK/blob/main/SDK/Documentation~/api-reference.md)
