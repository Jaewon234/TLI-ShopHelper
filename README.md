# 신들의 강복 도우미 (TLI-ShopHelper)

토치라이트 인피니트의 **신들의 강복** 상점에 들어가면 자동으로 켜지고, 나오면 꺼지는 오버레이입니다.
사야 할 칸은 초록 테두리와 `구매 +N결`(결정화했을 때 몇 결정 이득인지)로 표시합니다.
시세 입력 창은 상점 안에서는 게임 화면 왼쪽 아래에 자동으로 뜨고([적용]), 상점 밖에서는 트레이 메뉴에서 엽니다([입력완료] 후 닫힘). 입력칸은 숫자와 소수점만 받습니다 (예: `5.5`, `0.04`).

- 설치 필요 없음: `ShopHelper.exe` 하나 (자체 포함 .NET 8, 단일 파일)
- OCR: 내장 PaddleOCR(PP-OCRv4 ONNX, `src/Core/Models`)만 사용 (PC 마다 결과가 같도록). 숫자는 영어 모델로 한 번 더 읽음
- 게임 메모리를 읽거나 입력을 보내지 않음. 로그 파일(`UE_game.log`)과 화면 캡처만 사용
- 게임은 창 모드 / 테두리 없는 창 모드

## 다운로드
[Releases](../../releases/latest) 에서 `ShopHelper.zip` 을 받아 압축을 풀고 `ShopHelper.exe` 실행.

빌드는 로컬에서 하고(GitHub Actions 미사용), 릴리스에는 zip 만 직접 올립니다.

## 동작
1. `UE_game.log` 끝을 30ms 간격으로 읽어 `OpenView GroceryStoreV3` / `CloseView …_GroceryStoreV3Ctrl` 로 켜고 끔
   (로그 위치는 스팀 레지스트리 → `libraryfolders.vdf` → 실행 중인 게임 exe 순서로 자동 검색)
2. 상점을 처음 열면 화면 전체를 내장 OCR로 읽어 칸 위치를 찾음 (좌표 고정 없음 → 해상도/UI 크기 무관)
3. 그 뒤로는 칸이 있는 줄만 25ms마다 캡처해서 칸별 지문(축소 이미지)을 비교, 바뀐 칸만 처리
4. 한 번 본 칸 그림은 기억해 두었다가 OCR 없이 즉시 결과 표시

## 구조
| 경로 | 내용 |
|---|---|
| `src/Core` | 인식/판정/추적 로직 (Windows 의존 없음, Linux에서도 테스트 가능) |
| `src/App` | Win32 오버레이·트레이, GDI 캡처, PNG 저장, 로그 감시 |
| `tests/CoreTest` | 가짜 화면 + 가짜 OCR 로 Core 테스트 |

## 빌드
```
dotnet run --project tests/CoreTest
dotnet publish src/App/ShopHelper.csproj -c Release -o out
```
Linux에서도 Windows용 exe를 빌드할 수 있습니다 (`EnableWindowsTargeting`, WinForms/WPF 미사용).

## 설정 (ShopHelper.exe 옆 `설정\` 폴더, 쓸 수 없으면 `%AppData%\ShopHelper\`)
- `prices.json`: 시세만 (1개당 결정, `huiseok_price` = 신력의 휘석 1개가 몇 결정). 업데이트로 절대 덮어쓰지 않고, 새 기본 항목은 없는 것만 추가
- `settings.json`: 그 밖의 설정 (`mode`, `check_updates`, `update_repo` …)
- 배포 zip 에는 설정 파일을 넣지 않음. 예전 `config.json` 은 처음 실행 때 두 파일로 나눠 옮김

## 자동 업데이트 (TartarusSolver 방식)
켤 때 + 6시간마다 `update_repo` 의 최신 릴리스를 확인 → `ShopHelper.zip` 을 받아 GitHub 이 주는 SHA-256 으로 확인 →
새 버전을 `--install-update <설치폴더> <pid>` 로 실행 → 이전 버전이 끝나면 설치 폴더에 복사하고 다시 시작.
릴리스는 태그 `vX.Y.Z`, 첨부 파일 이름 `ShopHelper.zip`. **로그인 없이 릴리스를 읽을 수 있어야 하므로 `update_repo` 는 공개 저장소여야 함.**

- `mode`: `resell`(기본, 결정화: 수수료 12.5% 떼고 올림한 실수령 기준) / `use`(직접 사용, 시세 기준)
- `items`: 1개당 결정 시세 (`crystal_rate` = 결정 1개가 몇 휘석, 기본 50). 모르면 `null`. 도박 항목은 `kind: gamble`, `gamble_min`/`gamble_max`(휘석)
- 시세 창: 상점 안 왼쪽 아래 자동 표시 / 트레이 메뉴·더블클릭·Ctrl+Shift+P. 메모장으로 고쳐도 저장 즉시 반영
- 트레이 메뉴에서 기준 전환, 현재 화면 저장(문제 확인용) 가능
