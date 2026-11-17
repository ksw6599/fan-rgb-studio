# 개발·검증·구조

## 빌드

Windows PowerShell에서 저장소 폴더를 현재 경로로 열고 실행합니다. .NET 8 SDK와 네트워크 접속이 필요합니다. 일반 사용자는 Desktop Runtime과 Releases ZIP을 사용합니다.

```powershell
git clone https://github.com/ksw6599/fan-rgb-studio.git
cd fan-rgb-studio
pwsh -File .\scripts\Build-Release.ps1
```

출력은 `release-build/app/`와 `release-build/artifacts/`입니다. ZIP 이름을 `FanRgbStudio-win-x64.zip`으로 고정해 최신 다운로드 링크를 유지합니다. 실행 중인 개인용 `dist/`는 바꾸지 않습니다. `build.ps1`은 `dist/`를 출력 대상으로 쓰므로 그 앱을 먼저 완전히 종료해야 합니다.

공식 OpenRGB 1.1 / PawnIO 2.2.0 다운로드의 SHA256과 PawnIO 서명을 확인합니다. 빌드 중에 드라이버를 설치하지 않습니다. NuGet은 `packages.lock.json`과 `--locked-mode`로 고정합니다.

## 검사와 한계

- `--self-test --with-engine`: 보간·고온 보호·설정 저장·RGB TCP/복원 큐·PA120 인코딩. OpenRGB는 `--nodetect`와 별도 포트로 실행하며 실제 장치에 쓰지 않습니다.
- `--tray-smoke-test`: 대화형 Windows에서 WPF 편집창, 트레이와 읽기 전용 센서 worker를 검사합니다. 저장 팬/RGB 정책 자동 적용은 끕니다.
- v1.6.0의 [기능 검사](test-results-v160.txt) 38개와 [WPF·트레이 검사](tray-test-v160.json) 22개가 통과했습니다.
- v1.7.0의 [기능 검사](test-results-v170.txt) 50개와 [WPF·트레이 검사](tray-test-v170.json) 30개가 통과했습니다. RGB 효과 플래그, 방향·속도·팔레트, 응답 제한과 진단의 식별 정보 제외를 검사합니다.
- UI 검사는 별도의 인스턴스·복원 이벤트를 사용하고 운영 앱의 상태 JSON을 덮어쓰지 않습니다.
- GPU Off 후 로그인 시작 작업으로 새 앱·엔진을 실행했고 사용자가 주황색 자동 복원을 확인했습니다. 실제 PC 전원 재부팅 검사는 수행하지 않았습니다.
- CI 성공이 해당 메인보드의 실물 팬 제어 지원을 뜻하지는 않습니다.

## 주요 파일

| 파일 | 역할 |
|---|---|
| `Program.cs` | 실행 인자, 단일 인스턴스, 관리자·백그라운드 시작 |
| `MainWindow.xaml` / `.cs` | WPF 설정 화면, 명령 전달과 저장·복원 |
| `Models.cs` | 센서·명령·설정 모델과 팬 곡선 계산 |
| `CurveEditorRow.cs` / `ManualDuty.cs` | 온도 지점 편집과 수동 숫자 입력 |
| `HardwareWorker.cs` | 별도 프로세스 센서·팬 출력, heartbeat와 기본 제어 복원 |
| `FanProfile.cs` / `FanSession.cs` | 채널별 정책 저장·시작 검증과 이전 인계 호환 |
| `OpenRgbClient.cs` | OpenRGB SDK, 장치·구역 검증과 색상 전송 |
| `RgbWritePlan.cs` / `RgbDiagnostics.cs` | 효과 지원 기능에 맞춘 전송 검증과 식별 정보 없는 RGB 보고서 |
| `RgbCalibration.cs` / `RgbProfiles.cs` | 색상 보정, 장치별 저장과 시작 재시도 큐 |
| `Pa120Display.cs` | 초기화·모델 확인·숫자 인코딩·지속 USB 전송 |
| `TrayController.cs` / `TraySmokeTest.cs` | 트레이 수명과 WPF 검사 |
| `SelfTests.cs` / `DeviceDiagnostics.cs` | 자동 검사 및 명시적 장치 진단 |
| `scripts/` | 의존 파일 검증, 빌드·검사·ZIP·해시 생성 |
| `tools/Enable-Autostart.ps1` | 명시적 실행 시 로그인 시작 작업 등록 |
| `.github/workflows/` | 코드 검증과 요청한 릴리스 발행 |
| `licenses/` / `THIRD-PARTY-NOTICES.txt` | 외부 구성 요소의 라이선스·원본 출처 |

개인 로그·USB 덤프·TRCC 분석 파일·백업·산출물·사용자 설정은 저장소에 포함하지 않습니다.
