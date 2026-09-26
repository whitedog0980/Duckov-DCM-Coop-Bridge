using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using UnityEngine;

namespace DCMCoopBridge
{
    internal struct RemotePlayerInfo
    {
        public GameObject GameObject;
        public string SteamName; // 빈 문자열일 수 있음(Steam 미사용 LAN 접속 등)
    }

    /// <summary>
    /// Escape From Duckov COOP Mod 에 대한 리플렉션 브리지.
    ///
    /// 사용하는 COOP 공개 API:
    ///   ModApiEvents.PlayerSpawned : Action&lt;CharacterMainControl, string, bool&gt;
    ///   NetService.Instance . remoteCharacters / playerStatuses          (호스트: NetPeer 키)
    ///                       . clientRemoteCharacters / clientPlayerStatuses (클라이언트: playerId 키)
    ///                       . ResolveLocalSteamName(), IsServer, networkStarted
    ///   ModNetworkApi.RegisterHandler / SendToServer / Broadcast          (모델 ID 동기화)
    ///
    /// COOP 의 ModApi / LiteNetLib 타입(ModMessageContext, NetDataWriter)은 컴파일 타임에 참조하지 않기 위해
    /// System.Linq.Expressions 로 델리게이트를 런타임에 만든다.
    /// </summary>
    internal static class CoopApi
    {
        private const string ModApiEventsTypeName = "EscapeFromDuckovCoopMod.ModApiEvents";
        private const string NetServiceTypeName = "EscapeFromDuckovCoopMod.NetService";
        private const string ModNetworkApiTypeName = "EscapeFromDuckovCoopMod.ModNetworkApi";

        private const BindingFlags InstAll = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        // 이벤트
        private static EventInfo? _playerSpawnedEvent;
        private static Delegate? _spawnSubscription;

        // NetService
        private static FieldInfo? _instance;
        private static FieldInfo? _remoteCharacters;
        private static FieldInfo? _playerStatuses;
        private static FieldInfo? _clientRemoteCharacters;
        private static FieldInfo? _clientPlayerStatuses;
        private static FieldInfo? _networkStarted;
        private static PropertyInfo? _isServer;
        private static MethodInfo? _resolveLocalSteamName;
        private static PropertyInfo? _statusSteamName;

        // ModNetworkApi
        private static MethodInfo? _sendToServer;
        private static MethodInfo? _broadcast;
        private static IDisposable? _handlerSubscription;
        private static Delegate? _payloadWriter; // Action<NetDataWriter>
        private static readonly PayloadHolder Holder = new();
        private static PropertyInfo? _ctxPayload;
        private static PropertyInfo? _ctxIsServer;
        private static PropertyInfo? _ctxSender;
        private static Action<byte[], bool, bool>? _onMessage;

        public static bool IsReady { get; private set; }
        public static bool NetworkApiReady { get; private set; }

        public static bool TryInit(Action<CharacterMainControl, string, bool> onPlayerSpawned)
        {
            if (IsReady) return true;

            try
            {
                var netService = DcmApi.FindTypeAnywhere(NetServiceTypeName);
                if (netService == null) return false;

                _instance = netService.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
                _remoteCharacters = netService.GetField("remoteCharacters", InstAll);
                _playerStatuses = netService.GetField("playerStatuses", InstAll);
                _clientRemoteCharacters = netService.GetField("clientRemoteCharacters", InstAll);
                _clientPlayerStatuses = netService.GetField("clientPlayerStatuses", InstAll);
                _networkStarted = netService.GetField("networkStarted", InstAll);
                _isServer = netService.GetProperty("IsServer", InstAll);
                _resolveLocalSteamName = netService.GetMethod("ResolveLocalSteamName", InstAll, null,
                    Type.EmptyTypes, null);

                var statusType = _clientPlayerStatuses?.FieldType.GetGenericArguments() is { Length: 2 } ga
                    ? ga[1]
                    : null;
                _statusSteamName = statusType?.GetProperty("SteamName", InstAll);

                // 스폰 이벤트 (없으면 주기 스캔만으로 동작)
                var apiEvents = DcmApi.FindTypeAnywhere(ModApiEventsTypeName);
                _playerSpawnedEvent = apiEvents?.GetEvent("PlayerSpawned", BindingFlags.Public | BindingFlags.Static);
                if (_playerSpawnedEvent != null)
                    try
                    {
                        _spawnSubscription = Delegate.CreateDelegate(_playerSpawnedEvent.EventHandlerType,
                            onPlayerSpawned.Target, onPlayerSpawned.Method);
                        _playerSpawnedEvent.AddEventHandler(null, _spawnSubscription);
                    }
                    catch (Exception ex)
                    {
                        _spawnSubscription = null;
                        Log.Warn("Could not subscribe to ModApiEvents.PlayerSpawned (scan fallback): " + ex.Message);
                    }

                IsReady = true;
                Log.Info("COOP Mod detected" + (_spawnSubscription != null ? " (PlayerSpawned event hooked)." : "."));
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Failed to bind COOP API: " + ex);
                return false;
            }
        }

        public static void Shutdown()
        {
            try
            {
                if (_playerSpawnedEvent != null && _spawnSubscription != null)
                    _playerSpawnedEvent.RemoveEventHandler(null, _spawnSubscription);
            }
            catch
            {
                // ignored
            }

            try
            {
                _handlerSubscription?.Dispose();
            }
            catch
            {
                // ignored
            }

            _spawnSubscription = null;
            _handlerSubscription = null;
            NetworkApiReady = false;
            IsReady = false;
        }

        // ───────── 상태 ─────────

        private static object? Service
        {
            get
            {
                var s = _instance?.GetValue(null);
                if (s is UnityEngine.Object uo && uo == null) return null;
                return s;
            }
        }

        public static bool NetworkStarted
        {
            get
            {
                var s = Service;
                return s != null && _networkStarted != null && (bool)_networkStarted.GetValue(s);
            }
        }

        public static bool IsServer
        {
            get
            {
                var s = Service;
                return s != null && _isServer != null && (bool)_isServer.GetValue(s);
            }
        }

        public static string LocalSteamName
        {
            get
            {
                try
                {
                    var s = Service;
                    if (s == null || _resolveLocalSteamName == null) return string.Empty;
                    return (_resolveLocalSteamName.Invoke(s, null) as string ?? string.Empty).Trim();
                }
                catch
                {
                    return string.Empty;
                }
            }
        }

        /// <summary>현재 씬의 원격 플레이어와 그 Steam 닉네임 (호스트/클라이언트 공통)</summary>
        public static void CollectRemotePlayers(List<RemotePlayerInfo> result)
        {
            result.Clear();
            var service = Service;
            if (!IsReady || service == null) return;

            Collect(_remoteCharacters?.GetValue(service) as IDictionary,
                _playerStatuses?.GetValue(service) as IDictionary, result);
            Collect(_clientRemoteCharacters?.GetValue(service) as IDictionary,
                _clientPlayerStatuses?.GetValue(service) as IDictionary, result);
        }

        private static void Collect(IDictionary? characters, IDictionary? statuses, List<RemotePlayerInfo> result)
        {
            if (characters == null) return;
            foreach (DictionaryEntry kv in characters)
            {
                if (!(kv.Value is GameObject go) || go == null) continue;
                var name = string.Empty;
                if (statuses != null && kv.Key != null && statuses.Contains(kv.Key))
                {
                    var status = statuses[kv.Key];
                    if (status != null && _statusSteamName != null)
                        name = (_statusSteamName.GetValue(status) as string ?? string.Empty).Trim();
                }

                result.Add(new RemotePlayerInfo { GameObject = go, SteamName = name });
            }
        }

        // ───────── ModNetworkApi ─────────

        /// <summary>
        /// 채널 핸들러 등록. onMessage(payload, receivedOnServer, fromRemotePeer)
        /// </summary>
        public static bool TryInitNetwork(string channel, Action<byte[], bool, bool> onMessage)
        {
            if (NetworkApiReady) return true;

            try
            {
                var api = DcmApi.FindTypeAnywhere(ModNetworkApiTypeName);
                if (api == null) return false;

                MethodInfo? register = null;
                foreach (var m in api.GetMethods(BindingFlags.Public | BindingFlags.Static))
                {
                    if (m.Name == "RegisterHandler" && m.GetParameters().Length == 2) register = m;
                    else if (m.Name == "SendToServer" && m.GetParameters().Length == 2) _sendToServer = m;
                    else if (m.Name == "Broadcast" && m.GetParameters().Length == 3) _broadcast = m;
                }

                if (register == null || _sendToServer == null || _broadcast == null)
                {
                    Log.Warn("ModNetworkApi methods not found; model sync disabled.");
                    return false;
                }

                // Action<ModMessageContext> handler = ctx => OnRawMessage((object)ctx);
                var handlerType = register.GetParameters()[1].ParameterType;
                var ctxType = handlerType.GetGenericArguments()[0];
                _ctxPayload = ctxType.GetProperty("Payload");
                _ctxIsServer = ctxType.GetProperty("IsServer");
                _ctxSender = ctxType.GetProperty("Sender");
                var ctxParam = Expression.Parameter(ctxType, "ctx");
                var onRaw = typeof(CoopApi).GetMethod(nameof(OnRawMessage), BindingFlags.NonPublic | BindingFlags.Static)!;
                var handler = Expression.Lambda(handlerType,
                    Expression.Call(onRaw, Expression.Convert(ctxParam, typeof(object))), ctxParam).Compile();

                // Action<NetDataWriter> writer = w => w.Put(Holder.Data);
                var writerActionType = _sendToServer.GetParameters()[1].ParameterType;
                var writerType = writerActionType.GetGenericArguments()[0];
                var put = writerType.GetMethod("Put", new[] { typeof(byte[]) });
                if (put == null)
                {
                    Log.Warn("NetDataWriter.Put(byte[]) not found; model sync disabled.");
                    return false;
                }

                var wParam = Expression.Parameter(writerType, "w");
                var dataField = Expression.Field(Expression.Constant(Holder), nameof(PayloadHolder.Data));
                _payloadWriter = Expression.Lambda(writerActionType, Expression.Call(wParam, put, dataField), wParam)
                    .Compile();

                _onMessage = onMessage;
                _handlerSubscription = register.Invoke(null, new object[] { channel, handler }) as IDisposable;
                NetworkApiReady = true;
                Log.Info($"Model sync channel '{channel}' registered.");
                return true;
            }
            catch (Exception ex)
            {
                Log.Error("Failed to bind ModNetworkApi (model sync disabled): " + ex);
                return false;
            }
        }

        private static void OnRawMessage(object ctx)
        {
            try
            {
                if (_onMessage == null || _ctxPayload == null) return;
                var payload = _ctxPayload.GetValue(ctx) is ReadOnlyMemory<byte> mem ? mem.ToArray() : null;
                if (payload == null) return;
                var onServer = _ctxIsServer != null && (bool)_ctxIsServer.GetValue(ctx);
                var fromPeer = _ctxSender?.GetValue(ctx) != null;
                _onMessage(payload, onServer, fromPeer);
            }
            catch (Exception ex)
            {
                Log.Error("Model sync message error: " + ex);
            }
        }

        public static bool SendToServer(string channel, byte[] payload)
        {
            if (!NetworkApiReady || _sendToServer == null || _payloadWriter == null) return false;
            Holder.Data = payload;
            return (bool)_sendToServer.Invoke(null, new object[] { channel, _payloadWriter });
        }

        /// <summary>호스트 → 모든 클라이언트 (호스트 자신에게는 다시 보내지 않음)</summary>
        public static void Broadcast(string channel, byte[] payload)
        {
            if (!NetworkApiReady || _broadcast == null || _payloadWriter == null) return;
            Holder.Data = payload;
            _broadcast.Invoke(null, new object[] { channel, _payloadWriter, false });
        }

        private sealed class PayloadHolder
        {
            public byte[] Data = Array.Empty<byte>();
        }
    }
}
