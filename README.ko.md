# Daro Unity SDK

[English](README.md) | [한국어](README.ko.md)

Daro Unity SDK는 Unity용 Daro 광고 미디에이션 어댑터입니다. 이 패키지는 MAX(AppLovin) 미디에이션 variant이며 Android와 iOS를 지원합니다.

## 설치

권장 설치 경로는 bootstrap installer입니다.

1. Daro 홈페이지 또는 이 저장소의 GitHub Releases에서 `DaroPackageInstaller.unitypackage`를 다운로드합니다.
2. Unity 프로젝트에 import합니다.
3. installer가 `Packages/manifest.json`에 OpenUPM scoped registry, EDM4U, `so.daro.unity` 의존성을 추가합니다.
4. 설치 후 **Assets > Daro > Integration Manager**를 열어 프로젝트 설정을 검증합니다.

## 요구사항

- Unity 2019.4 LTS 이상
- 타깃 플랫폼에 맞는 Android Build Support 및/또는 iOS Build Support
- EDM4U. Bootstrap installer가 자동으로 설치합니다.

## 의존성

- Android는 prebuilt Unity wrapper AAR와 EDM4U가 해소하는 native mediation dependency를 사용합니다.
- iOS dependency는 Xcode project 생성 시 EDM4U/CocoaPods를 통해 해소됩니다.

## 지원 광고 포맷

- Interstitial
- Rewarded
- App Open
- Banner
- Native
- Light Popup

## 문서

- [문서 인덱스](https://github.com/delightroom/DaroUnitySDK/tree/main/SDK/Documentation~)
- [Integration guide](https://github.com/delightroom/DaroUnitySDK/blob/main/SDK/Documentation~/integration.md)
- [API reference](https://github.com/delightroom/DaroUnitySDK/blob/main/SDK/Documentation~/api-reference.md)
