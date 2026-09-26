using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// "각자 자기 캐릭터에 지정한 모델"을 다른 플레이어에게 알려 주는 동기화.
    ///
    /// 흐름 (COOP ModNetworkApi, 채널 dcmcoopbridge:model):
    ///   클라이언트 ──A(내 이름, 내 모델ID)──▶ 호스트 ──E(이름, 모델ID)──▶ 모든 클라이언트
    ///   호스트 자신의 모델은 호스트가 바로 E 로 방송.
    /// 모델 파일 자체는 보내지 않는다. 받는 쪽에 같은 ModelID 의 모델이 없으면 DCM 이 건너뛰고
    /// 다음 우선순위(멀티 플레이어 전체 → 원래 모델)로 넘어간다.
    ///
    /// 식별자는 Steam 닉네임. (COOP playerId 는 IP:포트라 세션마다 바뀌고, 호스트/클라이언트 간 형식도 다름)
    /// </summary>
    internal static class ModelSync
    {
        public const string Channel = "dcmcoopbridge:model";

        private const string Magic = "DCB1";
        private const char Sep = '\u001F';
        private const float AnnounceInterval = 15f;
        private const float OwnCheckInterval = 2f;

        private static readonly Dictionary<string, string> Table = new(StringComparer.Ordinal);

        private static string? _lastOwnModel;
        private static float _nextAnnounce;
        private static float _nextOwnCheck;
        private static bool _wasStarted;

        public static bool IsReady => CoopApi.NetworkApiReady;

        public static bool TryInit()
        {
            return CoopApi.TryInitNetwork(Channel, OnMessage);
        }

        /// <summary>해당 플레이어가 자기 캐릭터에 쓰는 모델 ID. 모르거나 기본 모델이면 null.</summary>
        public static string? GetSyncedModelId(string playerName)
        {
            if (string.IsNullOrEmpty(playerName)) return null;
            return Table.TryGetValue(playerName, out var id) && !string.IsNullOrEmpty(id) ? id : null;
        }

        public static void Tick()
        {
            if (!IsReady) return;

            if (!CoopApi.NetworkStarted)
            {
                if (_wasStarted) Table.Clear(); // 방을 나가면 이전 세션 정보 폐기
                _wasStarted = false;
                return;
            }

            if (!_wasStarted)
            {
                _wasStarted = true;
                _nextAnnounce = 0f;
            }

            var now = Time.unscaledTime;
            if (now >= _nextOwnCheck)
            {
                _nextOwnCheck = now + OwnCheckInterval;
                var own = Settings.ShareMyModel ? DcmApi.GetConfiguredModelId(DcmApi.CharacterTargetTypeId) : "";
                if (own != _lastOwnModel)
                {
                    _lastOwnModel = own;
                    _nextAnnounce = 0f; // 바뀌면 즉시 알림
                }
            }

            if (now < _nextAnnounce) return;
            _nextAnnounce = now + AnnounceInterval; // 늦게 들어온 사람을 위해 주기적으로 재전송
            Announce();
        }

        private static void Announce()
        {
            var name = CoopApi.LocalSteamName;
            if (string.IsNullOrEmpty(name)) return;
            var own = _lastOwnModel ?? string.Empty;

            if (CoopApi.IsServer)
                CoopApi.Broadcast(Channel, Encode('E', name, own));
            else
                CoopApi.SendToServer(Channel, Encode('A', name, own));
        }

        private static void OnMessage(byte[] payload, bool receivedOnServer, bool fromRemotePeer)
        {
            if (!Decode(payload, out var type, out var name, out var modelId)) return;

            if (receivedOnServer)
            {
                // 호스트: 클라이언트의 알림을 저장하고 모두에게 전달
                if (type != 'A' || !fromRemotePeer) return;
                Store(name, modelId);
                CoopApi.Broadcast(Channel, Encode('E', name, modelId));
                return;
            }

            if (type == 'E') Store(name, modelId);
        }

        private static void Store(string name, string modelId)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (name == CoopApi.LocalSteamName) return; // 내 모델은 DCM "캐릭터" 항목이 담당
            if (Table.TryGetValue(name, out var old) && old == modelId) return;
            Table[name] = modelId;
            Log.Info($"Synced model for '{name}': {(string.IsNullOrEmpty(modelId) ? "(default)" : modelId)}");
        }

        private static byte[] Encode(char type, string name, string modelId)
        {
            return Encoding.UTF8.GetBytes(Magic + Sep + type + Sep + name + Sep + modelId);
        }

        private static bool Decode(byte[] payload, out char type, out string name, out string modelId)
        {
            type = '\0';
            name = modelId = string.Empty;
            try
            {
                var parts = Encoding.UTF8.GetString(payload).Split(Sep);
                if (parts.Length != 4 || parts[0] != Magic || parts[1].Length != 1) return false;
                type = parts[1][0];
                name = parts[2].Trim();
                modelId = parts[3].Trim();
                return true;
            }
            catch
            {
                return false;
            }
        }
    }
}
