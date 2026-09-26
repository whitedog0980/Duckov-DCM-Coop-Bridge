using System;
using System.Collections.Generic;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// 1) DCM 이 초기화되면 "멀티 플레이어 (전체)" + 저장된 플레이어별 항목 등록, Harmony 패치
    /// 2) COOP 가 원격 플레이어를 만들면 DCM ModelHandler 를 붙이고 이름을 기록
    /// 3) 이름이 늦게 들어오거나 동기화 모델이 바뀌면 그 플레이어만 다시 적용
    /// </summary>
    internal class BridgeRunner : MonoBehaviour
    {
        private const float RetryInterval = 1f;
        private const float ScanInterval = 1f;
        private const int MaxAttemptsPerCharacter = 10;

        private readonly List<RemotePlayerInfo> _scanBuffer = new();
        private readonly Dictionary<int, int> _failedAttempts = new();

        private float _nextDcmTry;
        private float _nextCoopTry;
        private float _nextNetTry;
        private float _nextScan;

        private void Awake()
        {
            Settings.Load();
        }

        private void Update()
        {
            var now = Time.unscaledTime;

            if (!DcmApi.IsReady)
            {
                if (now < _nextDcmTry) return;
                _nextDcmTry = now + RetryInterval;
                if (!DcmApi.TryInit()) return;

                DcmApi.RegisterTargetType(DcmApi.TargetTypeId, DcmApi.CommonDisplayName);
                PlayerRegistry.RegisterSaved();
                Patches.Apply();
            }

            if (!CoopApi.IsReady)
            {
                if (now < _nextCoopTry) return;
                _nextCoopTry = now + RetryInterval;
                if (!CoopApi.TryInit(OnPlayerSpawned)) return;
            }

            if (!ModelSync.IsReady && now >= _nextNetTry)
            {
                _nextNetTry = now + 10f; // 실패해도 나머지 기능은 동작. 가끔만 재시도
                ModelSync.TryInit();
            }

            ModelSync.Tick();

            if (now < _nextScan) return;
            _nextScan = now + ScanInterval;
            Scan();
        }

        private void OnDestroy()
        {
            CoopApi.Shutdown();
            Patches.Remove();
            // 대상 타입은 등록 해제하지 않는다(UsingModel.json 값은 그대로 유지되고, UI 목록이 깨지지 않도록).
        }

        // COOP ModApiEvents.PlayerSpawned (시그니처가 이벤트 타입과 정확히 같아야 함)
        private void OnPlayerSpawned(CharacterMainControl cmc, string playerId, bool isLocal)
        {
            if (isLocal || cmc == null) return;
            _nextScan = 0f; // 다음 프레임에 스캔 (이름 정보와 함께 처리)
        }

        private void Scan()
        {
            CoopApi.CollectRemotePlayers(_scanBuffer);
            foreach (var info in _scanBuffer)
            {
                var cmc = info.GameObject.GetComponent<CharacterMainControl>();
                if (cmc == null) cmc = info.GameObject.GetComponentInChildren<CharacterMainControl>(true);
                if (cmc == null) continue;

                if (!string.IsNullOrEmpty(info.SteamName))
                    PlayerRegistry.Ensure(info.SteamName);

                var marker = cmc.GetComponent<CoopRemotePlayerMarker>();
                if (marker == null || !marker.Applied)
                {
                    TryApply(cmc, info.SteamName);
                    continue;
                }

                // 이미 적용됨: 이름이 새로 확인됐거나 동기화 모델이 바뀐 경우에만 다시 적용
                var nameChanged = !string.IsNullOrEmpty(info.SteamName) && marker.PlayerName != info.SteamName;
                if (nameChanged) marker.PlayerName = info.SteamName;

                // 우선순위 패치가 없으면 AppliedSyncedModel 이 갱신되지 않으므로 비교하지 않는다(무한 재적용 방지).
                var syncedChanged = false;
                if (Patches.PriorityPatched && Settings.UseSyncedModels)
                    syncedChanged = ModelSync.GetSyncedModelId(marker.PlayerName) != marker.AppliedSyncedModel;

                if (nameChanged || syncedChanged)
                    Refresh(cmc);
            }
        }

        private void TryApply(CharacterMainControl cmc, string playerName)
        {
            var id = 0;
            try
            {
                if (cmc == null || !cmc.gameObject.activeInHierarchy) return;
                if (cmc == CharacterMainControl.Main) return;
                id = cmc.GetInstanceID();
                if (_failedAttempts.TryGetValue(id, out var fails) && fails >= MaxAttemptsPerCharacter) return;

                if (cmc.characterModel == null)
                {
                    CountFailure(id, cmc, "characterModel is not ready yet");
                    return;
                }

                var handler = DcmApi.GetExistingHandler(cmc);
                if (handler != null && DcmApi.IsHandlerInitialized(handler))
                {
                    var existingTarget = DcmApi.GetHandlerTargetTypeId(handler);
                    if (existingTarget != DcmApi.TargetTypeId)
                    {
                        Log.Warn($"'{cmc.name}' already has a DCM handler for '{existingTarget}', skipping.");
                        _failedAttempts[id] = MaxAttemptsPerCharacter;
                        return;
                    }
                }
                else
                {
                    handler = DcmApi.AttachHandler(cmc);
                    if (handler == null || !DcmApi.IsHandlerInitialized(handler))
                    {
                        CountFailure(id, cmc, "DCM ModelHandler failed to initialize");
                        return;
                    }
                }

                // 표시를 먼저 붙인 뒤 갱신해야 우선순위 postfix 가 이 캐릭터를 원격 플레이어로 인식한다.
                var marker = cmc.GetComponent<CoopRemotePlayerMarker>();
                if (marker == null) marker = cmc.gameObject.AddComponent<CoopRemotePlayerMarker>();
                marker.Init(cmc, playerName);
                marker.Applied = true;
                DcmApi.RefreshHandler(handler);

                _failedAttempts.Remove(id);
                Log.Info($"Custom model hooked for remote player '{(playerName.Length > 0 ? playerName : cmc.name)}'.");
            }
            catch (Exception ex)
            {
                CountFailure(id, cmc, ex.ToString());
            }
        }

        private static void Refresh(CharacterMainControl cmc)
        {
            try
            {
                var handler = DcmApi.GetExistingHandler(cmc);
                if (handler != null && DcmApi.IsHandlerInitialized(handler))
                    DcmApi.RefreshHandler(handler);
            }
            catch (Exception ex)
            {
                Log.Error("Refresh failed: " + ex);
            }
        }

        private void CountFailure(int id, CharacterMainControl? cmc, string reason)
        {
            _failedAttempts.TryGetValue(id, out var fails);
            fails++;
            _failedAttempts[id] = fails;
            if (fails == 1 || fails == MaxAttemptsPerCharacter)
                Log.Warn($"Could not apply to '{(cmc != null ? cmc.name : "?")}' (try {fails}): {reason}");
        }
    }
}
