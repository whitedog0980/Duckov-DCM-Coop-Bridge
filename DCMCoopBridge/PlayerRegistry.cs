using System;
using System.Collections.Generic;

namespace DCMCoopBridge
{
    /// <summary>
    /// 플레이어별 DCM 항목("멀티: 닉네임") 관리.
    ///
    /// 안정성을 위해 "추가만" 한다: 한 번 본 플레이어는 known_players.txt 에 기록되고
    /// 다음 실행부터 게임 시작 시 한꺼번에 등록된다. 세션 중 항목을 지우지 않으므로
    /// DCM 설정 창 목록이 들쭉날쭉하지 않고, 선택해 둔 항목이 사라지는 일도 없다.
    /// </summary>
    internal static class PlayerRegistry
    {
        private static readonly HashSet<string> Registered = new(StringComparer.Ordinal);

        public static string TargetIdFor(string playerName)
        {
            return DcmApi.PerPlayerPrefix + playerName;
        }

        public static void RegisterSaved()
        {
            foreach (var name in Settings.LoadKnownPlayers())
                Register(name);
            if (Registered.Count > 0)
                Log.Info($"Registered {Registered.Count} saved per-player target(s).");
        }

        /// <summary>처음 보는 플레이어면 항목을 추가하고 파일에 기록한다.</summary>
        public static void Ensure(string playerName)
        {
            if (string.IsNullOrEmpty(playerName) || Registered.Contains(playerName)) return;
            if (!Register(playerName)) return;
            Settings.AppendKnownPlayer(playerName);
            Log.Info($"New player '{playerName}' added to DCM target list.");
        }

        private static bool Register(string playerName)
        {
            if (Registered.Contains(playerName)) return true;
            var captured = playerName;
            if (!DcmApi.RegisterTargetType(TargetIdFor(captured),
                    lang => DcmApi.PerPlayerDisplayName(lang, captured)))
                return false;
            Registered.Add(captured);
            return true;
        }
    }
}
