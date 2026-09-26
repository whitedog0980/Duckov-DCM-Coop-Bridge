# DCM Coop Bridge

**Duckov Custom Model(DCM)** 과 **Escape From Duckov COOP Mod** 를 함께 쓸 때, 멀티플레이 상대(원격 플레이어)에게도 커스텀 모델을 입힐 수 있게 해 주는 연결용 모드입니다. 두 모드는 수정하지 않고, 양쪽의 공개 확장 API만 사용합니다.

- **Steam 창작마당:** https://steamcommunity.com/sharedfiles/filedetails/?id=3808206951
- **필수 모드:** [HarmonyLib](https://steamcommunity.com/workshop/filedetails/?id=3589088839), [Duckov Custom Model](https://steamcommunity.com/sharedfiles/filedetails/?id=3600560151), [Escape From Duckov COOP Mod](https://steamcommunity.com/sharedfiles/filedetails/?id=3591341282)
- **개발 과정:** [DEVLOG.md](DEVLOG.md)

## 왜 원래는 안 되는가

| DCM이 모델을 붙이는 시점 | 원격 플레이어는? |
| --- | --- |
| `LevelManager.MainCharacter` → "캐릭터" | ✗ 원격 플레이어는 MainCharacter가 아님 |
| `LevelManager.PetCharacter` → "펫" | ✗ |
| `AICharacterController.Init` 에 Harmony 후킹 → "AI" / "모든 AI" | ✗ 원격 플레이어는 `CharacterCreator.CreateCharacter`로 만들어지고 AI 컨트롤러가 없음 |

그래서 원격 플레이어에는 DCM의 `ModelHandler`가 **아예 붙지 않습니다**. "모든 AI"에 모델을 지정해도 바뀌지 않았던 이유가 이것입니다.

## 이 모드가 하는 일

### DCM 대상 목록에 추가되는 항목
| 항목 | 대상 ID | 의미 |
| --- | --- | --- |
| 멀티 플레이어 (전체) | `extension:CoopRemotePlayer` | 모든 원격 플레이어의 기본 모델 |
| 멀티: 닉네임 | `extension:CoopPlayer:<Steam 닉네임>` | 그 플레이어에게만 쓸 모델 (한 번 같이 플레이한 사람부터 생김) |

호환 타입이 `built-in:Character`라서 플레이어용 모델이 그대로 목록에 뜹니다.

### 원격 플레이어에게 입혀지는 모델 (위에서부터, 내 PC에 있는 첫 모델)
1. **내가 "멀티: 닉네임"에 지정한 모델** – 클라이언트가 플레이어별로 직접 지정
2. **그 플레이어가 자기 "캐릭터"에 쓰는 모델** – 네트워크로 모델 ID만 받음. 내게 같은 ModelID 가 없으면 건너뜀
3. **"멀티 플레이어 (전체)" 모델**
4. 없으면 **원래 모델**

DCM 창에서 모델을 바꾸면 바로 반영되고, 상대가 자기 모델을 바꾸면 수 초 안에 반영됩니다.

### 동작 방식 요약
- 원격 플레이어 감지: COOP `ModApiEvents.PlayerSpawned` + 1초 주기로 `NetService`의 원격 플레이어/상태 목록 확인 (Steam 닉네임 포함)
- 모델 적용: `ModelManager.InitializeModelHandler(cmc, "extension:CoopRemotePlayer")`
- 우선순위: DCM `ModelHandler.InitializeModelPriorityList`에 Harmony postfix 로 키 20(플레이어별)·10(동기화)을 추가
- 동기화: COOP `ModNetworkApi` 채널 `dcmcoopbridge:model`. 클라이언트 → 호스트 → 전원. 15초마다 재전송(늦게 들어온 사람 대비), 내 모델이 바뀌면 즉시
- 걷기 애니메이션 보정: `AnimatorParameterUpdaterManager.UpdateAll` postfix 로 원본 Animator 의 `MoveSpeed/MoveDirX/MoveDirY/Dashing` 과 위치 변화로 계산한 속도를 커스텀 애니메이터에 전달
- DCM/COOP DLL 은 컴파일 시 참조하지 않고 리플렉션(+Expression)으로 연결 → 두 모드 업데이트에 덜 민감하고, DLL 중복 로드 문제가 없음

### 설정 / 데이터 파일
`%USERPROFILE%\AppData\LocalLow\<회사>\<게임>\DCMCoopBridge\` (Unity persistentDataPath)
- `settings.txt`: `ShareMyModel`(내 모델 알리기), `UseSyncedModels`(상대가 고른 모델 보기) – 기본 true
- `known_players.txt`: 플레이어별 항목 목록. 줄을 지우고 재시작하면 항목이 사라짐

### 주의
- 식별자는 **Steam 닉네임**입니다. 상대가 닉네임을 바꾸면 새 항목이 생기고, 이전 지정은 새 이름으로 다시 해야 합니다. Steam 을 쓰지 않는 직접 IP 접속에서 닉네임이 비면 "전체" 항목만 적용됩니다.
- 모델 **파일**은 전송하지 않습니다. 같은 모델(같은 ModelID)을 가진 사람끼리만 서로의 모델이 보입니다.
- 동기화는 **양쪽 모두 이 모드를 설치**해야 동작합니다. 설치하지 않은 사람은 1·3·4 순서만 적용됩니다.

## 빌드

필요한 것: .NET SDK 6 이상, 게임 설치 폴더

```powershell
cd DCMCoopBridge
dotnet build -c Release -p:DuckovPath="D:\SteamLibrary\steamapps\common\Escape from Duckov"
```

결과물은 `dist\DCMCoopBridge\` 에 `DCMCoopBridge.dll` + `info.ini` 로 모입니다.

## 설치 / 테스트

1. `dist\DCMCoopBridge` 폴더를 게임의 `Duckov_Data\Mods\` 아래에 복사합니다(창작마당 업로드 전 로컬 테스트).
2. 게임 모드 목록에서 **HarmonyLib → Duckov Custom Model → COOP Mod → DCM Coop Bridge** 를 모두 활성화합니다. 로드 순서는 상관없습니다. 이 모드는 두 모드가 준비될 때까지 1초마다 다시 확인합니다.
3. DCM 설정 창 → 대상 목록 → **멀티 플레이어 (Coop)** → 모델 선택
4. 친구와 접속해서 확인합니다. 로그(`Player.log`)에서 `[DCMCoopBridge]` 줄로 진행 상황을 볼 수 있습니다.
   - `Duckov Custom Model detected` / `COOP Mod detected` / `Registered DCM target type` / `Custom model hooked for remote player ...`

> 모델은 **내 PC의 설정 기준**으로 보입니다. 내가 "멀티 플레이어"에 A 모델을 지정하면, 내 화면에서 다른 플레이어들이 A로 보입니다. 상대방 화면에는 상대방 설정이 적용됩니다.

## 창작마당 업로드

1. 로컬 테스트용 `Duckov_Data\Mods\DCMCoopBridge`에 최신 빌드(`dist\DCMCoopBridge`: dll, info.ini, preview.png)가 있는지 확인합니다.
2. 게임의 모드 메뉴에서 이 로컬 모드를 창작마당에 업로드합니다. 업로드하면 게임이 `info.ini`를 다시 쓰면서 `publishedFileId`를 기록합니다.
3. **업로드 후 `Mods\DCMCoopBridge\info.ini`를 이 프로젝트의 `DCMCoopBridge\info.ini`로 복사해 두세요.** `publishedFileId`가 없으면 다음 업로드가 새 항목으로 올라갑니다.
4. Steam 창작마당 페이지에서 다음 작업을 합니다.
   - "필수 항목 추가/삭제"에서 **HarmonyLib(3589088839), Duckov Custom Model(3600560151), COOP Mod(3591341282)** 를 필수 항목으로 지정합니다.
   - 공개 범위를 설정하고, 설명에 크레딧과 AI 사용 표기를 적습니다.
5. 구독해서 테스트할 때는 로컬 `Mods\DCMCoopBridge` 폴더를 지워서 같은 모드가 두 번 로드되지 않게 하세요.

## 알려진 한계 / 다음 단계

- **달리기(`Running`)·조준 방향·공격 트리거**는 COOP가 원격 플레이어에 무엇을 동기화하느냐에 달려 있습니다. 걷기 외의 애니메이션이 어색하면 `CoopRemotePlayerMarker.DriveCustomAnimator`에 파라미터를 추가하면 됩니다.
- DCM 대상 목록이 이미 열려 있을 때 등록되면, 창을 닫았다 열거나 검색어를 바꿔야 항목이 보일 수 있습니다.

## 파일 구조

| 파일 | 역할 |
| --- | --- |
| `ModBehaviour.cs` | 게임 모드 진입점 (네임스페이스 = `info.ini`의 `name`) |
| `BridgeRunner.cs` | 초기화 재시도, 원격 플레이어 감지·적용·재적용 |
| `DcmApi.cs` | DCM 리플렉션 브리지, 대상 타입 등록, 설정 조회 |
| `CoopApi.cs` | COOP 리플렉션 브리지 (이벤트, 원격 플레이어+닉네임, ModNetworkApi) |
| `ModelSync.cs` | 각자 쓰는 모델 ID 동기화 프로토콜 |
| `PlayerRegistry.cs` | 플레이어별 항목 등록(추가만) |
| `Settings.cs` | settings.txt / known_players.txt |
| `Patches.cs` | DCM 우선순위·애니메이터 Harmony postfix |
| `CoopRemotePlayerMarker.cs` | 원격 플레이어 표시(닉네임) + 애니메이터 파라미터 전달 |
| `preview.png` | 창작마당 썸네일 (256×256) |
| `lib/0Harmony.dll` | 컴파일용 참조(배포하지 않음, 런타임에는 HarmonyLib 모드가 제공). Harmony, MIT – `lib/README.md` 참고 |

## 크레딧 / 라이선스

- Duckov Custom Model: OLC 외 (MIT) – https://github.com/Duckov-Custom-Model/DuckovCustomModel
- Escape From Duckov COOP Mod: Mr.sans and InitLoader's team (수정 AGPL-3.0, 비상업) – https://github.com/Mr-sans-and-InitLoader-s-team/Escape-From-Duckov-Coop-Mod-Preview
  - 이 모드는 COOP 코드를 포함하지 않고 공개 API(`ModApiEvents`, `NetService`)만 호출합니다. 배포할 때 COOP 라이선스의 표기 조건과 비상업 조건을 지켜 주세요.
- Harmony (MIT)
