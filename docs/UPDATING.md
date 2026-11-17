# 업데이트 배포와 받는 방법

## 받는 분

항상 사용하는 최신 버전 주소:

**https://github.com/ksw6599/fan-rgb-studio/releases/latest**

ZIP 바로 다운로드 주소:

**https://github.com/ksw6599/fan-rgb-studio/releases/latest/download/FanRgbStudio-win-x64.zip**

1. 트레이 팬 아이콘 우클릭 → **완전히 종료**. X만 누르면 실행 파일이 사용 중입니다.
2. 새 ZIP을 받고 기존 앱 폴더를 복사해 백업합니다.
3. 새 ZIP 전체를 **기존과 같은 앱 폴더에 덮어씁니다**. EXE만 바꾸지 않습니다.
4. 관리자 권한으로 다시 실행합니다. 시작 작업이 등록되어 있다면 폴더 경로를 유지해야 다음 로그인에도 새 버전이 실행됩니다.

설정은 `%LOCALAPPDATA%\SunuongFanStudio`에 있어 앱 폴더와 분리됩니다. 이 폴더를 지우지 않으면 팬 곡선과 RGB 설정을 보존합니다. 다른 사람의 설정 폴더를 덮어쓰지 마세요. 팬 정책은 PC·장치가 일치할 때 복원됩니다.

현재 앱에는 새 버전 알림·다운로드·파일 교체를 수행하는 자동 업데이터가 없습니다. GitHub 릴리스 발행 자동화와 사용자 PC의 자동 업데이트는 서로 다른 기능입니다.

## 배포하는 분

간단하게는 Codex에 **“팬 프로그램 수정하고 버전 올려서 GitHub 릴리스까지 배포해 줘”**라고 요청하면 됩니다.

직접 배포할 때:

1. 코드를 수정합니다.
2. `FanRgbStudio.csproj`의 `<Version>`을 올립니다. 예: `1.6.0` → `1.6.1`.
3. `CHANGELOG.md`에도 반영하고 `main`에 커밋합니다. 화면 버전은 프로젝트 버전에서 자동 표시됩니다.
4. **Build and test** 검사 결과를 확인합니다.
5. 저장소 → **Actions → Publish release → Run workflow**에서 `main`으로 실행합니다.
6. 빌드·검사 성공 후 새 태그·릴리스, ZIP, SHA256SUMS가 생성됩니다. 같은 버전이 이미 있으면 실패하므로 버전을 올려야 합니다.
7. Releases의 새 ZIP을 확인합니다. 받는 분에게는 위의 고정 링크를 다시 보내면 됩니다.

이전 릴리스는 보존합니다. 문제가 생기면 이전 Releases ZIP이나 백업한 앱 폴더로 돌아갈 수 있습니다.

`scripts/Build-Release.ps1`은 의존 파일 검증, 고정 NuGet 복원, Release 빌드, 하드웨어에 쓰지 않는 검사, ZIP·SHA256 생성을 수행합니다. 실물 확인은 장치가 있는 PC에서 따로 해야 합니다.
