# 신들의 강복 도우미 (TLI-ShopHelper)

토치라이트 인피니트의 **신들의 강복** 상점에 들어가면 자동으로 켜지고, 나오면 꺼지는 오버레이입니다.
칸마다 시세와 비교해서 이득(초록) / 손해(빨강) / 시세 없음(회색)을 바로 보여줍니다.

- 설치 필요 없음: `ShopHelper.exe` 하나 (자체 포함 .NET 8, 단일 파일)
- 게임 메모리를 읽거나 입력을 보내지 않음. 로그 파일(`UE_game.log`)과 화면 캡처만 사용
- 게임은 창 모드 / 테두리 없는 창 모드

## 다운로드
[Releases](../../releases/latest) 에서 `ShopHelper.zip` 을 받아 압축을 풀고 `ShopHelper.exe` 실행.

빌드는 로컬에서 하고(GitHub Actions 미사용), 릴리스에는 zip 만 직접 올립니다.

## 동작
1. `UE_game.log` 끝을 30ms 간격으로 읽어 `OpenView GroceryStoreV3` / `CloseView …_GroceryStoreV3Ctrl` 로 켜고 끔
   (로그 위치는 스팀 레지스트리 → `libraryfolders.vdf` → 실행 중인 게임 exe 순서로 자동 검색)
2. 상점을 처음 열면 화면 전체를 Windows 한국어 OCR로 읽어 칸 위치를 찾음 (좌표 고정 없음 → 해상도/UI 크기 무관)
3. 그 뒤로는 칸이 있는 줄만 25ms마다 캡처해서 칸별 지문(축소 이미지)을 비교, 바뀐 칸만 처리
4. 한 번 본 칸 그림은 기억해 두었다가 OCR 없이 즉시 결과 표시

## 구조
| 경로 | 내용 |
|---|---|
| `src/Core` | 인식/판정/추적 로직 (Windows 의존 없음, Linux에서도 테스트 가능) |
| `src/App` | Win32 오버레이·트레이, GDI 캡처, Windows OCR, 로그 감시 |
| `tests/CoreTest` | 가짜 화면 + 가짜 OCR 로 Core 테스트 |

## 빌드
```
dotnet run --project tests/CoreTest
dotnet publish src/App/ShopHelper.csproj -c Release -o out
```
Linux에서도 Windows용 exe를 빌드할 수 있습니다 (`EnableWindowsTargeting`, WinForms/WPF 미사용).

## 설정 (`config.json`, exe 옆에 자동 생성)
- `mode`: `use`(직접 사용, 시세 기준) / `resell`(되팔기: 세금 12.5% 떼고 올림한 실수령 기준)
- `items`: 1개당 휘석 시세. 모르면 `null`. `taxed: false` 는 팔 때 세금 없는 항목(최초의 불꽃 결정)
- 트레이 메뉴에서 기준 전환, 설정 다시 읽기, 현재 화면 저장(문제 확인용) 가능
