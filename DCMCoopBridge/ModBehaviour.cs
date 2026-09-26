using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// 게임 모드 로더가 찾는 진입점. (네임스페이스 = info.ini 의 name)
    /// 실제 로직은 씬 전환에도 살아남는 별도 GameObject(BridgeRunner)에서 돈다.
    /// </summary>
    public class ModBehaviour : Duckov.Modding.ModBehaviour
    {
        private GameObject? _runner;

        private void OnEnable()
        {
            if (_runner != null) return;

            _runner = new GameObject("DCMCoopBridge_Runner");
            DontDestroyOnLoad(_runner);
            _runner.AddComponent<BridgeRunner>();
            Log.Info("Loaded. Waiting for Duckov Custom Model and COOP Mod...");
        }

        private void OnDisable()
        {
            if (_runner == null) return;
            Destroy(_runner);
            _runner = null;
            Log.Info("Unloaded.");
        }
    }

    internal static class Log
    {
        private const string Prefix = "[DCMCoopBridge] ";
        public static void Info(string msg) => Debug.Log(Prefix + msg);
        public static void Warn(string msg) => Debug.LogWarning(Prefix + msg);
        public static void Error(string msg) => Debug.LogError(Prefix + msg);
    }
}
