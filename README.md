# 선웅 Fan Studio

Windows에서 **팬 속도·온도별 곡선, RGB 조명, PA120 DIGITAL 숫자 표시**를 제어하는 한국어 WPF 앱입니다.

[**최신 버전 다운로드**](https://github.com/ksw6599/fan-rgb-studio/releases/latest) · [처음 사용하기](docs/GETTING_STARTED.md) · [업데이트 방법](docs/UPDATING.md) · [문제 해결](docs/HARDWARE.md) · [RGB 문제 해결](docs/RGB.md)

## 다운로드와 첫 실행

1. Releases에서 **FanRgbStudio-win-x64.zip**을 받고 **전체 압축을 풉니다**.
2. [.NET 8 Windows Desktop Runtime x64](https://dotnet.microsoft.com/download/dotnet/8.0)가 필요합니다.
3. `FanRgbStudio.exe` 우클릭 → **관리자 권한으로 실행**.
4. 앱 왼쪽 **센서 드라이버 설치**로 PawnIO를 설치합니다.
5. 설치 후 **트레이에서 완전히 종료**하고 관리자 권한으로 다시 실행합니다. X는 창만 숨깁니다.

Windows 10/11 x64용이며 실제 장치 검증은 Windows 11에서 수행했습니다. 압축형 배포로, EXE와 DLL·tools 폴더를 함께 유지해야 합니다. 다운로드 ZIP에 드라이버가 포함되어 있어도 자동 설치되지는 않습니다.

## 주요 기능

| 기능 | 동작 |
|---|---|
| 팬 속도 | 저소음·균형·성능, 수동 30~100%, 온도별 사용자 곡선 |
| 사용자 곡선 | 2~12개 온도·출력 지점 편집, 추가·삭제, 보간과 미리보기 |
| 저장·복원 | 채널별 팬 정책, 장치·구역별 RGB 설정을 자동 저장·복원 |
| RGB | 지원 효과별 색상·밝기·속도, 구역·팔레트 보존, 엔진 복구·진단 저장 |
| PA120 DIGITAL | CPU/GPU 온도·사용률 숫자 표시와 지속 USB 전송 |
| 백그라운드 | X·최소화 시 트레이 유지, 명시적 완전 종료 |

팬 설정: **채널 체크 → 프로필에서 사용자 곡선 → 온도·출력 편집 → 팬 설정 적용**.

조명 설정: **RGB 연결 → 장치·구역·효과 선택 → 지원하는 색상·밝기·속도 → 조명 설정 적용**. 자동 색상 효과는 HEX 입력을 비활성화합니다. 멈춘 효과는 **효과 다시 시작**, 응답이 없으면 **RGB 복구**, 문제를 전달하려면 **RGB 진단 저장**을 사용합니다. 다시 검색은 목록만 갱신합니다.

## 하드웨어 지원

LibreHardwareMonitor 0.9.6과 OpenRGB 1.1이 지원하는 제어기에 따라 동작합니다. 실물 확인한 구성은 **ASRock B650M PG Lightning / NCT6799D, Ryzen 5 9600X, MSI RTX 4070 Gaming X Trio, Thermalright PA120 DIGITAL**입니다. 다른 PC의 성공을 보장하지 않습니다.

- 팬 목록은 **제어 채널**입니다. 실제 팬 개수나 물리 위치를 자동 구분하지 않습니다.
- 현재 팬 식별·최소 회전 출력 자동 보정·팬 재검색 버튼은 없습니다.
- 일반 LCD 이미지·영상이나 모든 쿨러 디스플레이를 지원하지 않습니다.
- PawnIO·관리자 권한·연결 방식·보드 제어칩에 따라 팬 목록이 달라집니다. [팬이 안 보일 때](docs/HARDWARE.md)를 먼저 확인하세요.

CPU 온도가 있어야 팬 제어를 시작합니다. CPU·GPU 최고 온도를 사용하며 **85°C 이상은 최대 출력**입니다. 온도/장치/heartbeat 상실 시 앱 소유 채널에 기본 제어 복원을 요청합니다. 펌프 채널은 선택하지 마세요.

## 설정과 로그인 시작

설정은 `%LOCALAPPDATA%\SunuongFanStudio`에 저장됩니다.

- `preferences.json`: 화면 설정·색상 보정·곡선 초안
- `fan-profile.json`: 실제 적용한 채널별 팬 정책
- `rgb-profiles.json`: 장치·구역별 RGB 정책

**기본 제어 복원**은 저장된 팬 정책도 삭제합니다. **완전히 종료**는 물리 제어를 기본값으로 돌리지만 저장 정책은 유지합니다.

`tools/Enable-Autostart.ps1`을 명시적으로 실행하고 관리자 승인하면 현재 폴더의 앱을 로그인 15초 뒤 백그라운드 실행하도록 등록할 수 있습니다. 앱 폴더를 옮기면 다시 등록하세요. 배포 ZIP이 시작 작업을 임의로 등록하지 않습니다.

## 업데이트를 보내려면

받는 분에게는 항상 같은 [최신 릴리스 링크](https://github.com/ksw6599/fan-rgb-studio/releases/latest)를 보내면 됩니다.

**트레이에서 완전히 종료 → 새 ZIP 다운로드 → 기존 폴더 백업 → 같은 폴더에 전체 덮어쓰기 → 다시 실행** 순서입니다. 사용자 설정 폴더는 그대로 유지합니다.

새 버전 배포는 코드·프로젝트 버전·변경 기록을 커밋한 뒤 **Actions → Publish release → Run workflow**로 실행합니다. 성공한 빌드와 기능 검사를 통과한 ZIP·SHA256이 새 버전으로 게시됩니다. [상세 업데이트 절차](docs/UPDATING.md)에 정리했습니다.

현재 사용자 PC에서 자동으로 다운로드·교체하는 업데이터는 없습니다.

## 빌드와 검증

.NET 8 SDK가 있는 Windows PowerShell에서 저장소 폴더를 열고 실행합니다.

```powershell
pwsh -File .\scripts\Build-Release.ps1
```

공식 의존 파일 다운로드 및 SHA256/서명 확인 → 고정 NuGet 복원 → 빌드 → 하드웨어에 쓰지 않는 검사 → ZIP과 SHA256을 생성합니다. 출력은 `release-build/app/`와 `release-build/artifacts/`입니다.

v1.7.0 로컬 검증: **기능 검사 51개 / 실제 WPF·트레이 검사 30개 통과**. RGB 검사는 가상 장치의 TCP 응답과 UI 동작으로 수행했으며 실제 조명을 변경하지 않았습니다. 특정 제보 장치의 실물 검증은 아직 수행하지 않았습니다.

이전 v1.6.0에서는 GPU를 Off로 바꾼 뒤 로그인 시작 작업으로 앱·엔진을 시작해 저장한 주황색 복원을 사용자가 확인했습니다. 실제 PC의 전원 재부팅 검사는 수행하지 않았습니다. CI 결과는 실물 장치 지원 확인과 구분합니다.

[개발 구조·검사 기록](docs/DEVELOPMENT.md) · [변경 기록](CHANGELOG.md)

## 라이선스

이 저장소의 자체 코드는 [MIT](LICENSE)입니다. 외부 구성 요소는 각자 라이선스를 유지합니다. LibreHardwareMonitor(MPL-2.0), 별도 실행하는 OpenRGB(GPL-2.0-or-later), Qt 및 USB 라이브러리, PawnIO의 원본 출처와 라이선스는 [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt)와 [licenses](licenses/)에 있습니다.
