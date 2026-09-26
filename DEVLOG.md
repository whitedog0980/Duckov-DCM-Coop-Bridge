# 개발 기록 (DEVLOG)

Duckov Custom Model(DCM)과 Escape From Duckov COOP Mod를 같이 쓸 때, 멀티플레이 상대에게는 커스텀 모델이 적용되지 않는 문제를 해결하기까지의 과정입니다. 개발은 Claude(AI)와 함께 진행했습니다.

---

## 0. 문제

- DCM은 AI마다 모델을 따로 지정할 수 있지만, **멀티플레이 상대 플레이어는 어떤 항목에도 속하지 않았습니다.**
- "모든 AI"에 모델을 지정해도 상대 플레이어는 바뀌지 않았습니다.
- 목표는 **멀티플레이 상대를 지정할 수 있는 새 항목을 추가하는 것**이었습니다.

## 1. 원인 분석 (두 모드의 소스 읽기)

두 모드 모두 GitHub에 소스가 공개되어 있어서 직접 읽어 봤습니다.

**DCM이 모델을 붙이는 곳은 세 군데뿐이었습니다.**
- `LevelManager.MainCharacter` → "캐릭터"
- `LevelManager.PetCharacter` → "펫"
- `AICharacterController.Init` Harmony postfix → "AI" / "모든 AI"

**COOP의 원격 플레이어는 여기에 걸리지 않습니다.**
- 원격 플레이어는 `CharacterCreator.CreateCharacter`로 직접 만들어지고 AI 컨트롤러가 없습니다.
- 그래서 DCM의 `ModelHandler`가 **아예 붙지 않았고**, "모든 AI"도 소용이 없었습니다.

**두 모드 모두 확장용 공개 API를 가지고 있었습니다.**
- DCM: `ModelTargetTypeRegistry.RegisterTargetType("extension:...")` – UI 대상 목록에 자동으로 추가됨
- DCM: `ModelManager.InitializeModelHandler(character, targetTypeId)`
- COOP: `ModApiEvents.PlayerSpawned(cmc, playerId, isLocal)` – 원격 플레이어 생성 이벤트
- COOP: `ModNetworkApi` – 모드용 네트워크 채널

→ 결론: **두 모드를 고치지 않고, 둘을 이어 주는 별도 모드를 만든다.**

## 2. 설계에서 고른 것들

- **리플렉션으로 연결:** DCM은 자체 로더가 DLL을 `Assembly.Load(bytes)`로 올립니다. 컴파일 타임에 직접 참조하면 같은 DLL이 두 번 로드되어 설정과 레지스트리가 둘로 갈라질 위험이 있습니다. 그래서 이미 초기화된 DCM 어셈블리를 찾아 리플렉션으로 호출합니다. COOP 쪽 타입(`ModMessageContext`, `NetDataWriter`)은 `System.Linq.Expressions`로 델리게이트를 런타임에 만들어 씁니다.
- **로드 순서와 무관하게 동작:** 두 모드가 준비될 때까지 1초마다 다시 확인합니다.
- **이벤트와 주기 스캔을 함께 사용:** 스폰 이벤트를 놓쳐도 1초마다 COOP의 원격 플레이어 목록을 확인합니다.

## 3. v1 – "멀티 플레이어" 항목 하나

- `extension:CoopRemotePlayer` 대상 타입을 등록했습니다. 호환 타입을 `built-in:Character`로 두어 플레이어용 모델이 그대로 목록에 뜹니다.
- 원격 플레이어가 생기면 `InitializeModelHandler`를 호출하고, 이어서 `UpdateModelPriorityList`로 모델을 입힙니다.
- **걷기 애니메이션 문제:** 원격 플레이어는 이동 컴포넌트가 꺼져 있고, COOP가 원본 Animator에 `MoveSpeed/MoveDirX/MoveDirY/Dashing`을 직접 씁니다. DCM은 이 값을 읽지 않아서 모델이 미끄러집니다. `AnimatorParameterUpdaterManager.UpdateAll`에 postfix를 걸어 원본 Animator의 값과 위치 변화로 계산한 속도를 커스텀 애니메이터로 넘겨 해결했습니다.

**개발 환경**
- 클라우드 작업 환경에는 .NET SDK와 게임 DLL이 없어서 빌드할 수 없었습니다.
- 그래서 PC의 작업 폴더 하나를 Claude에게 할당하는 방식으로 진행했습니다.
  1. Claude가 그 폴더의 코드를 직접 수정합니다.
  2. 사용자가 PC에서 `dotnet build`를 실행하고 게임에서 테스트합니다.
  3. 그 결과(오류 메시지나 로그)를 다시 Claude에게 전달합니다.
- 첫 빌드는 경고 0, 오류 0으로 통과했고, 실제 게임에서 원격 플레이어에게 모델이 적용되는 것을 확인했습니다.

## 4. v2 – 플레이어별 지정과 모델 공유

새로 추가한 요구사항은 두 가지입니다.
1. 클라이언트가 **플레이어마다 다른 모델**을 지정할 수 있어야 한다.
2. 각 플레이어가 **자기에게 쓴 모델이 다른 사람 화면에도 보여야 한다.** 같은 모델이 없으면 기본 모델로 표시한다.

**플레이어를 구분하는 값**
- COOP의 playerId는 `IP:포트`라서 세션마다 바뀝니다.
- 호스트와 클라이언트 모두에서 얻을 수 있는 값은 Steam 닉네임뿐이어서 이것을 사용했습니다.

**항목이 계속 바뀌는 문제를 막는 방법**
- 항목은 **추가만** 하고 지우지 않습니다.
- 한 번 본 플레이어는 `known_players.txt`에 기록해 두고, 게임을 시작할 때 모두 등록합니다.
- DCM UI는 새로 고칠 때마다 등록된 항목을 다시 읽고, 설정 파일은 모르는 키도 보존합니다. 그래서 항목을 추가해도 안전합니다.

**모델 우선순위 (DCM `ModelHandler.ModelPriorityList`에 postfix로 키 추가)**

| 키 | 내용 |
| --- | --- |
| 20 | 내가 지정한 "멀티: 닉네임" |
| 10 | 그 플레이어가 자기에게 쓴 모델 (동기화) |
| 0 | "멀티 플레이어 (전체)" |
| – | 원래 모델 |

- DCM은 키가 큰 것부터 **설치되어 있고 호환되는 첫 모델**을 입힙니다. 그래서 "같은 모델이 없으면 다음 순서로 넘어가는" 예외 처리가 따로 코드를 쓰지 않아도 됩니다.

**동기화 (COOP `ModNetworkApi`, 채널 `dcmcoopbridge:model`)**
- 클라이언트가 `A(이름, 모델ID)`를 호스트에 보내면, 호스트가 `E(이름, 모델ID)`를 모두에게 보냅니다.
- 모델 파일은 보내지 않고 ID만 보냅니다.
- 내 모델이 바뀌면 즉시, 그 외에는 15초마다 다시 보냅니다(늦게 들어온 사람 대비).

**결과**
- 세 기능(전체 항목, 플레이어별 항목, 모델 공유)이 모두 실제 멀티플레이에서 동작하는 것을 확인했습니다.

## 5. 배포

- 256×256 `preview.png`를 만들고, `info.ini`에 태그를 넣었습니다. 설명에는 창작마당 규칙에 따른 AI 사용 표기를 넣었습니다.
- 게임 안에서 Steam 창작마당에 업로드했습니다. publishedFileId는 **3808206951**입니다.
- 이후 업데이트가 같은 항목으로 올라가도록 `publishedFileId`를 프로젝트의 `info.ini`에 반영했습니다.

## 6. 남은 과제

- 원격 플레이어의 달리기, 조준 방향, 공격 트리거 애니메이션 보정 (COOP가 무엇을 동기화하는지에 따라 달라짐)
- Steam 닉네임이 바뀌었을 때 이전 지정을 자동으로 옮기는 기능
