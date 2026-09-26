using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// 이 모드 전용 설정/데이터. 창작마당 폴더는 업데이트 때 덮어써지므로 persistentDataPath 에 저장한다.
    ///   settings.txt     : key=value (ShareMyModel, UseSyncedModels)
    ///   known_players.txt: 한 줄에 Steam 닉네임 하나. 여기 있는 이름마다 DCM 에 "멀티: 이름" 항목이 생김.
    ///                      (지우고 싶은 플레이어는 이 파일에서 줄을 지우고 게임 재시작)
    /// </summary>
    internal static class Settings
    {
        /// <summary>내 "캐릭터" 모델 ID 를 다른 플레이어에게 알림</summary>
        public static bool ShareMyModel { get; private set; } = true;

        /// <summary>다른 플레이어가 알린 모델을 내 화면에 사용</summary>
        public static bool UseSyncedModels { get; private set; } = true;

        public static string Directory
        {
            get
            {
                var dir = Path.Combine(Application.persistentDataPath, "DCMCoopBridge");
                try
                {
                    System.IO.Directory.CreateDirectory(dir);
                }
                catch
                {
                    // ignored
                }

                return dir;
            }
        }

        private static string SettingsPath => Path.Combine(Directory, "settings.txt");
        private static string KnownPlayersPath => Path.Combine(Directory, "known_players.txt");

        public static void Load()
        {
            try
            {
                if (!File.Exists(SettingsPath))
                {
                    Save();
                    return;
                }

                foreach (var raw in File.ReadAllLines(SettingsPath))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line.StartsWith("#")) continue;
                    var eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var key = line.Substring(0, eq).Trim();
                    var value = line.Substring(eq + 1).Trim();
                    var b = !value.Equals("false", StringComparison.OrdinalIgnoreCase) && value != "0";
                    if (key.Equals(nameof(ShareMyModel), StringComparison.OrdinalIgnoreCase)) ShareMyModel = b;
                    else if (key.Equals(nameof(UseSyncedModels), StringComparison.OrdinalIgnoreCase))
                        UseSyncedModels = b;
                }

                Log.Info($"Settings: ShareMyModel={ShareMyModel}, UseSyncedModels={UseSyncedModels} ({SettingsPath})");
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load settings, using defaults: " + ex.Message);
            }
        }

        private static void Save()
        {
            try
            {
                File.WriteAllLines(SettingsPath, new[]
                {
                    "# DCM Coop Bridge settings (true/false). Restart the game after editing.",
                    "# ShareMyModel: tell other players which model you use for your own character",
                    $"{nameof(ShareMyModel)}={ShareMyModel.ToString().ToLowerInvariant()}",
                    "# UseSyncedModels: show other players with the model they chose for themselves (if you have it)",
                    $"{nameof(UseSyncedModels)}={UseSyncedModels.ToString().ToLowerInvariant()}",
                });
            }
            catch
            {
                // ignored
            }
        }

        public static List<string> LoadKnownPlayers()
        {
            try
            {
                if (!File.Exists(KnownPlayersPath)) return new List<string>();
                return File.ReadAllLines(KnownPlayersPath)
                    .Select(l => l.Trim())
                    .Where(l => l.Length > 0 && !l.StartsWith("#"))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to load known players: " + ex.Message);
                return new List<string>();
            }
        }

        public static void AppendKnownPlayer(string name)
        {
            try
            {
                File.AppendAllLines(KnownPlayersPath, new[] { name });
            }
            catch (Exception ex)
            {
                Log.Warn("Failed to save known player: " + ex.Message);
            }
        }
    }
}
