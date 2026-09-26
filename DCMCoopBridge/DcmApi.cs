using System;
using System.Collections;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// Duckov Custom Model(DCM)에 대한 리플렉션 브리지.
    ///
    /// DCM은 자체 로더가 DuckovCustomModel.GameModules.dll 을 Assembly.Load(bytes)로 올리기 때문에
    /// 컴파일 타임 참조로 묶으면 같은 DLL이 두 번 로드되어 static 상태(설정, 레지스트리)가 갈라질 수 있다.
    /// 그래서 "이미 초기화된" DCM 어셈블리를 찾아서 그 안의 공개 API만 리플렉션으로 호출한다.
    /// </summary>
    internal static class DcmApi
    {
        /// <summary>모든 원격 플레이어 공통 항목. 원격 플레이어 ModelHandler 는 항상 이 대상 ID로 만든다.</summary>
        public const string TargetTypeId = "extension:CoopRemotePlayer";

        /// <summary>플레이어별 항목 접두사. 실제 ID = 접두사 + Steam 닉네임</summary>
        public const string PerPlayerPrefix = "extension:CoopPlayer:";

        public const string CharacterTargetTypeId = "built-in:Character";

        private const string ModEntryTypeName = "DuckovCustomModel.ModEntry";
        private const string ModelManagerTypeName = "DuckovCustomModel.Managers.ModelManager";
        private const string ModelHandlerTypeName = "DuckovCustomModel.MonoBehaviours.ModelHandler";
        private const string CustomAnimatorControlTypeName = "DuckovCustomModel.MonoBehaviours.CustomAnimatorControl";
        private const string UpdaterManagerTypeName = "DuckovCustomModel.Managers.AnimatorParameterUpdaterManager";
        private const string RegistryTypeName = "DuckovCustomModel.Core.Managers.ModelTargetTypeRegistry";

        private static FieldInfo? _usingModelField;
        private static MethodInfo? _usingModelGetId;
        private static MethodInfo? _register;
        private static MethodInfo? _isRegistered;
        private static MethodInfo? _initHandler;
        private static MethodInfo? _updatePriorityList;
        private static MethodInfo? _getTargetTypeId;
        private static PropertyInfo? _handlerIsInitialized;
        private static FieldInfo? _priorityListField;
        private static MethodInfo? _setFloat;
        private static MethodInfo? _setBool;

        private static readonly object?[] Args2 = new object?[2];
        private static readonly object?[] Args1 = new object?[1];

        public static bool IsReady { get; private set; }
        public static Type? ModelHandlerType { get; private set; }

        /// <summary>AnimatorParameterUpdaterManager.UpdateAll (애니메이터 보정 postfix 대상)</summary>
        public static MethodInfo? UpdateAllMethod { get; private set; }

        /// <summary>ModelHandler.InitializeModelPriorityList (private, 우선순위 postfix 대상)</summary>
        public static MethodInfo? InitPriorityListMethod { get; private set; }

        public static bool TryInit()
        {
            if (IsReady) return true;

            try
            {
                // ModEntry.UsingModel 이 채워져 있는(=실제로 Initialize 된) DCM 어셈블리를 고른다.
                Assembly? gameModules = null;
                Type? modEntry = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    var t = SafeGetType(asm, ModEntryTypeName);
                    if (t == null) continue;
                    var f = t.GetField("UsingModel", BindingFlags.Public | BindingFlags.Static);
                    if (f?.GetValue(null) == null) continue;
                    gameModules = asm;
                    modEntry = t;
                    _usingModelField = f;
                    break;
                }

                if (gameModules == null || modEntry == null || _usingModelField == null) return false;

                var modelManager = gameModules.GetType(ModelManagerTypeName, false);
                ModelHandlerType = gameModules.GetType(ModelHandlerTypeName, false);
                var customAnimatorControl = gameModules.GetType(CustomAnimatorControlTypeName, false);
                var updaterManager = gameModules.GetType(UpdaterManagerTypeName, false);
                if (modelManager == null || ModelHandlerType == null || customAnimatorControl == null ||
                    updaterManager == null)
                {
                    Log.Error("DCM found but its API types are missing. DCM version may be incompatible.");
                    return false;
                }

                _usingModelGetId = _usingModelField.FieldType.GetMethod("GetModelID", new[] { typeof(string) });

                // DCM이 실제로 바인딩한 Core 어셈블리 (ModelManager.ModelBundles : List<ModelBundleInfo>)
                var bundlesField = modelManager.GetField("ModelBundles", BindingFlags.Public | BindingFlags.Static);
                var coreAssembly = bundlesField?.FieldType.GetGenericArguments().FirstOrDefault()?.Assembly;
                var registry = coreAssembly?.GetType(RegistryTypeName, false) ?? FindTypeAnywhere(RegistryTypeName);
                if (registry == null)
                {
                    Log.Error("ModelTargetTypeRegistry not found. DCM version may be too old (needs v1.10+).");
                    return false;
                }

                _register = registry.GetMethod("RegisterTargetType", BindingFlags.Public | BindingFlags.Static);
                _isRegistered = registry.GetMethod("IsRegistered", BindingFlags.Public | BindingFlags.Static);

                _initHandler = modelManager.GetMethod("InitializeModelHandler",
                    BindingFlags.Public | BindingFlags.Static, null,
                    new[] { typeof(CharacterMainControl), typeof(string) }, null);

                const BindingFlags inst = BindingFlags.Public | BindingFlags.Instance;
                _updatePriorityList = ModelHandlerType.GetMethod("UpdateModelPriorityList", inst, null,
                    Type.EmptyTypes, null);
                _getTargetTypeId = ModelHandlerType.GetMethod("GetTargetTypeId", inst, null, Type.EmptyTypes, null);
                _handlerIsInitialized = ModelHandlerType.GetProperty("IsInitialized", inst);
                _priorityListField = ModelHandlerType.GetField("ModelPriorityList", inst);
                InitPriorityListMethod = ModelHandlerType.GetMethod("InitializeModelPriorityList",
                    BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null);

                _setFloat = customAnimatorControl.GetMethod("SetParameterFloat", new[] { typeof(int), typeof(float) });
                _setBool = customAnimatorControl.GetMethod("SetParameterBool", new[] { typeof(int), typeof(bool) });

                UpdateAllMethod = updaterManager.GetMethod("UpdateAll", BindingFlags.Public | BindingFlags.Static);

                if (_register == null || _initHandler == null || _updatePriorityList == null ||
                    _usingModelGetId == null)
                {
                    Log.Error("Required DCM methods not found. DCM version may be incompatible.");
                    return false;
                }

                if (_priorityListField == null || InitPriorityListMethod == null)
                    Log.Warn("ModelPriorityList API not found; per-player / synced models are disabled.");

                IsReady = true;
                Log.Info($"Duckov Custom Model detected ({gameModules.GetName().Name}).");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Failed to bind DCM API: " + ex);
                return false;
            }
        }

        public static bool SupportsPriorityOverride => _priorityListField != null && InitPriorityListMethod != null;

        // ───────── 대상 타입 등록 ─────────

        /// <summary>DCM 대상 목록에 항목을 추가한다. 플레이어용(Character) 모델이 그대로 목록에 뜬다.</summary>
        public static bool RegisterTargetType(string targetTypeId, Func<SystemLanguage, string> displayName)
        {
            if (!IsReady || _register == null) return false;

            try
            {
                if (IsRegistered(targetTypeId)) return true;
                _register.Invoke(null, new object?[] { targetTypeId, new[] { CharacterTargetTypeId }, displayName });
                return true;
            }
            catch (TargetInvocationException ex) when (ex.InnerException is InvalidOperationException)
            {
                return true; // 이미 등록됨
            }
            catch (Exception ex)
            {
                Log.Error($"Failed to register target type '{targetTypeId}': {ex}");
                return false;
            }
        }

        public static bool IsRegistered(string targetTypeId)
        {
            if (_isRegistered == null) return false;
            return (bool)_isRegistered.Invoke(null, new object[] { targetTypeId });
        }

        public static string CommonDisplayName(SystemLanguage language)
        {
            switch (language)
            {
                case SystemLanguage.Korean: return "멀티 플레이어 (전체)";
                case SystemLanguage.Japanese: return "マルチプレイヤー (全員)";
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.Chinese: return "联机玩家 (全部)";
                case SystemLanguage.ChineseTraditional: return "聯機玩家 (全部)";
                default: return "Coop Players (All)";
            }
        }

        public static string PerPlayerDisplayName(SystemLanguage language, string playerName)
        {
            switch (language)
            {
                case SystemLanguage.Korean: return "멀티: " + playerName;
                case SystemLanguage.Japanese: return "マルチ: " + playerName;
                case SystemLanguage.ChineseSimplified:
                case SystemLanguage.Chinese:
                case SystemLanguage.ChineseTraditional: return "联机: " + playerName;
                default: return "Coop: " + playerName;
            }
        }

        // ───────── 설정 ─────────

        /// <summary>UsingModel.json 에서 대상 ID 에 지정된 모델 ID (없으면 빈 문자열)</summary>
        public static string GetConfiguredModelId(string targetTypeId)
        {
            if (_usingModelField == null || _usingModelGetId == null) return string.Empty;
            var usingModel = _usingModelField.GetValue(null);
            if (usingModel == null) return string.Empty;
            Args1[0] = targetTypeId;
            return _usingModelGetId.Invoke(usingModel, Args1) as string ?? string.Empty;
        }

        // ───────── 핸들러 ─────────

        public static Component? GetExistingHandler(CharacterMainControl cmc)
        {
            if (ModelHandlerType == null || cmc == null) return null;
            return cmc.GetComponent(ModelHandlerType);
        }

        public static bool IsHandlerInitialized(Component handler)
        {
            return _handlerIsInitialized != null && handler != null &&
                   (bool)_handlerIsInitialized.GetValue(handler);
        }

        public static string? GetHandlerTargetTypeId(Component handler)
        {
            if (_getTargetTypeId == null || handler == null) return null;
            return _getTargetTypeId.Invoke(handler, null) as string;
        }

        public static IDictionary? GetPriorityList(object handler)
        {
            return _priorityListField?.GetValue(handler) as IDictionary;
        }

        /// <summary>원격 플레이어 캐릭터에 DCM ModelHandler 를 붙인다. (모델 적용은 호출 측에서 Refresh)</summary>
        public static Component? AttachHandler(CharacterMainControl cmc)
        {
            if (!IsReady || _initHandler == null) return null;
            return _initHandler.Invoke(null, new object[] { cmc, TargetTypeId }) as Component;
        }

        /// <summary>우선순위 목록을 다시 계산하고 모델을 다시 입힌다(우리 postfix 포함).</summary>
        public static void RefreshHandler(Component handler)
        {
            if (_updatePriorityList == null || handler == null) return;
            _updatePriorityList.Invoke(handler, null);
        }

        // ───────── 애니메이터 ─────────

        public static void SetFloat(object control, int hash, float value)
        {
            if (_setFloat == null) return;
            Args2[0] = hash;
            Args2[1] = value;
            _setFloat.Invoke(control, Args2);
        }

        public static void SetBool(object control, int hash, bool value)
        {
            if (_setBool == null) return;
            Args2[0] = hash;
            Args2[1] = value;
            _setBool.Invoke(control, Args2);
        }

        // ───────── 공용 ─────────

        private static Type? SafeGetType(Assembly asm, string name)
        {
            try
            {
                return asm.GetType(name, false);
            }
            catch
            {
                return null;
            }
        }

        internal static Type? FindTypeAnywhere(string fullName)
        {
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = SafeGetType(asm, fullName);
                if (t != null) return t;
            }

            return null;
        }
    }
}
