using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// DCM 에 거는 Harmony postfix 두 개. (둘 다 원격 플레이어 표시가 있는 캐릭터에만 작동)
    ///
    /// 1) ModelHandler.InitializeModelPriorityList (private)
    ///    DCM 은 핸들러마다 SortedDictionary&lt;int, modelId&gt; 우선순위 목록을 두고, 큰 키부터 설치된·호환되는
    ///    첫 모델을 입힌다(없으면 원래 모델). 원격 플레이어 핸들러의 목록에 키를 추가한다.
    ///      20 : 내가 지정한 "멀티: 닉네임"      (요구사항 2: 클라이언트가 플레이어별로 지정)
    ///      10 : 그 플레이어가 자기에게 쓴 모델   (요구사항 3: 동기화, 내게 없으면 자동으로 건너뜀)
    ///       0 : "멀티 플레이어 (전체)"           (DCM 기본 동작)
    ///      없음: 원래 오리 모델
    ///    DCM 은 설정이 바뀔 때마다 모든 핸들러에서 이 메서드를 다시 부르므로 UI 변경이 바로 반영된다.
    ///
    /// 2) AnimatorParameterUpdaterManager.UpdateAll
    ///    원격 플레이어의 이동 파라미터를 커스텀 애니메이터로 전달 (CoopRemotePlayerMarker 참고)
    /// </summary>
    internal static class Patches
    {
        public const int PriorityPerPlayer = 20;
        public const int PrioritySynced = 10;

        private const string HarmonyId = "DCMCoopBridge";
        private const int MaxErrors = 20;

        private static Harmony? _harmony;
        private static int _animErrors;
        private static int _priorityErrors;

        /// <summary>우선순위 패치가 실제로 걸렸는지 (안 걸렸으면 플레이어별/동기화 모델 기능 비활성)</summary>
        public static bool PriorityPatched { get; private set; }

        public static void Apply()
        {
            if (_harmony != null) return;
            _harmony = new Harmony(HarmonyId);

            PriorityPatched = TryPatch(DcmApi.InitPriorityListMethod, nameof(PriorityPostfix),
                "per-player/synced model priority");
            TryPatch(DcmApi.UpdateAllMethod, nameof(AnimatorPostfix), "remote animator fix");
        }

        private static bool TryPatch(MethodBase? target, string postfixName, string what)
        {
            if (_harmony == null) return false;
            if (target == null)
            {
                Log.Warn($"Patch target for {what} not found; feature disabled.");
                return false;
            }

            try
            {
                var postfix = typeof(Patches).GetMethod(postfixName, BindingFlags.NonPublic | BindingFlags.Static);
                _harmony.Patch(target, postfix: new HarmonyMethod(postfix));
                Log.Info($"Patched: {what}.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to patch {what}: {ex}");
                return false;
            }
        }

        public static void Remove()
        {
            try
            {
                _harmony?.UnpatchAll(HarmonyId);
            }
            catch
            {
                // ignored
            }

            _harmony = null;
            PriorityPatched = false;
        }

        // ───────── 1) 우선순위 ─────────

        private static void PriorityPostfix(object __instance)
        {
            if (_priorityErrors >= MaxErrors) return;
            if (!(__instance is Component handler) || handler == null) return;
            if (!handler.TryGetComponent<CoopRemotePlayerMarker>(out var marker)) return;

            try
            {
                var list = DcmApi.GetPriorityList(handler);
                if (list == null) return;

                var name = marker.PlayerName;

                var perPlayer = string.IsNullOrEmpty(name)
                    ? string.Empty
                    : DcmApi.GetConfiguredModelId(PlayerRegistry.TargetIdFor(name));
                SetOrRemove(list, PriorityPerPlayer, perPlayer);

                var synced = Settings.UseSyncedModels ? ModelSync.GetSyncedModelId(name) : null;
                SetOrRemove(list, PrioritySynced, synced);

                marker.AppliedSyncedModel = synced;
            }
            catch (Exception ex)
            {
                _priorityErrors++;
                Log.Error("Priority patch error: " + ex);
            }
        }

        private static void SetOrRemove(System.Collections.IDictionary list, int key, string? modelId)
        {
            if (string.IsNullOrEmpty(modelId)) list.Remove(key);
            else list[key] = modelId;
        }

        // ───────── 2) 애니메이터 ─────────

        // __args 로 받으면 DCM 타입(CustomAnimatorControl)을 컴파일 타임에 몰라도 된다.
        private static void AnimatorPostfix(object[] __args)
        {
            if (_animErrors >= MaxErrors) return;
            if (__args == null || __args.Length == 0) return;
            if (!(__args[0] is Component control) || control == null) return;
            if (!control.TryGetComponent<CoopRemotePlayerMarker>(out var marker) || !marker.Applied) return;

            try
            {
                marker.DriveCustomAnimator(control);
            }
            catch (Exception ex)
            {
                _animErrors++;
                Log.Error("Remote animator fix error: " + ex);
                if (_animErrors >= MaxErrors) Log.Error("Too many errors; remote animator fix disabled.");
            }
        }
    }
}
