# MuMuAdBlocker — MuMuPlayer 중앙 광고 제거 유틸리티

MuMuPlayer(Windows)의 `com.mumu.store` 가 띄우는 중앙 팝업 광고(`PopupAdWindow`, TYPE_APPLICATION_OVERLAY)를
**ADB AppOps 최소 권한 변경**만으로 차단하는 Windows GUI 도구입니다.

- APK 수정 / Launcher 교체 / Store 삭제 없음
- Windows 관리자 권한 불필요
- 단일 EXE, 설치 불필요

## 사용 방법

1. MuMuPlayer 실행
2. `MuMuAdBlocker.exe` 실행
3. **[자동 찾기]** 으로 ADB 탐색 (실패 시 **[찾아보기...]** 로 `adb.exe` 직접 선택)
4. MuMu 인스턴스 선택 (멀티 인스턴스 지원)
5. **[중앙 광고 제거]** 클릭 → 상태가 "광고 오버레이 차단됨" 인지 확인
6. 이미 떠 있는 광고는 X 버튼으로 닫거나 MuMu를 재시작

ADB 연결이 안 되어 있으면 MuMu 설정 → 기타 → ADB 디버깅에서 포트를 확인한 뒤
**수동 연결**에 `127.0.0.1:포트` 를 입력하고 **[연결]**.

## 원복

**[기본값으로 복원]** 버튼으로 언제든 기본 상태로 되돌릴 수 있습니다.

## 내부 동작 (참고)

```
adb -s <serial> shell cmd appops set --user 0 com.mumu.store SYSTEM_ALERT_WINDOW ignore   # 차단
adb -s <serial> shell cmd appops set --user 0 com.mumu.store SYSTEM_ALERT_WINDOW default  # 원복
adb -s <serial> shell cmd appops get --user 0 com.mumu.store SYSTEM_ALERT_WINDOW          # 상태 확인
```

적용 후에는 반드시 상태를 재조회하여 실제 결과를 검증합니다.
`com.mumu.store` 가 없는 장치는 MuMu로 간주하지 않으며, 휴대폰 등 다른 Android 장치는 건드리지 않습니다.

## 설정 / 로그 위치

- 설정: `%LOCALAPPDATA%\MuMuAdBlocker\settings.json`
- 로그: `%LOCALAPPDATA%\MuMuAdBlocker\logs\`

## 프로젝트 구조

```
/src
    MuMuAdBlocker.csproj
    app.manifest            (asInvoker — 관리자 권한 요구 없음)
    Program.cs
    MainForm.cs             (WinForms GUI)
    Services/
        AdbRunner.cs        (프로세스 실행, ArgumentList, timeout)
        AdbLocator.cs       (ADB 자동 탐색)
        MuMuManager.cs      (장치 탐색, AppOps 조회/설정/검증)
        SettingsStore.cs    (설정 저장/로드)
```
