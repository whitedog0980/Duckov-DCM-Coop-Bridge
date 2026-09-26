using System.Collections.Generic;
using UnityEngine;

namespace DCMCoopBridge
{
    /// <summary>
    /// 원격 플레이어 표시 + 커스텀 애니메이터 파라미터 보정.
    ///
    /// DCM 의 기본 파라미터 업데이터는 CharacterMainControl 의 이동 상태(Velocity, movementControl.Moving 등)를 읽는다.
    /// 그런데 COOP 의 원격 플레이어는 이동 컴포넌트가 꺼져 있고, 대신 원본 Animator 에 MoveSpeed/MoveDirX/MoveDirY/Dashing
    /// 값을 네트워크로 받아 직접 써 넣는다. 그대로 두면 커스텀 모델이 걷지 않고 미끄러지므로
    /// 원본 Animator 의 값을 읽어 DCM 커스텀 애니메이터로 넘겨준다.
    /// </summary>
    internal class CoopRemotePlayerMarker : MonoBehaviour
    {
        private static readonly int HMoveSpeed = Animator.StringToHash("MoveSpeed");
        private static readonly int HMoveDirX = Animator.StringToHash("MoveDirX");
        private static readonly int HMoveDirY = Animator.StringToHash("MoveDirY");
        private static readonly int HDashing = Animator.StringToHash("Dashing");

        // DCM CustomAnimatorHash 와 동일한 이름
        private static readonly int HMoving = Animator.StringToHash("Moving");
        private static readonly int HGrounded = Animator.StringToHash("Grounded");
        private static readonly int HVelocityMagnitude = Animator.StringToHash("VelocityMagnitude");
        private static readonly int HVelocityX = Animator.StringToHash("VelocityX");
        private static readonly int HVelocityY = Animator.StringToHash("VelocityY");
        private static readonly int HVelocityZ = Animator.StringToHash("VelocityZ");

        private const float MovingThreshold = 0.05f;
        private const float VelocitySmoothTime = 0.08f;

        private readonly HashSet<int> _sourceFloats = new();
        private readonly HashSet<int> _sourceBools = new();

        private CharacterMainControl? _cmc;
        private Animator? _source;
        private RuntimeAnimatorController? _sourceController;

        private Vector3 _lastPos;
        private Vector3 _velocity;
        private Vector3 _velocityRef;
        private float _lastTime = -1f;

        public bool Applied { get; set; }

        /// <summary>이 원격 플레이어의 Steam 닉네임 (모르면 빈 문자열)</summary>
        public string PlayerName { get; set; } = string.Empty;

        /// <summary>마지막으로 우선순위 목록에 넣은 동기화 모델 ID (변경 감지용)</summary>
        public string? AppliedSyncedModel { get; set; }

        public void Init(CharacterMainControl cmc, string playerName)
        {
            _cmc = cmc;
            PlayerName = playerName ?? string.Empty;
            _source = null;
            _lastTime = -1f;
        }

        /// <summary>DCM 업데이터들이 한 프레임 파라미터를 다 쓴 직후(Harmony postfix)에 호출된다.</summary>
        public void DriveCustomAnimator(object customAnimatorControl)
        {
            if (_cmc == null) return;

            UpdateVelocity();

            DcmApi.SetFloat(customAnimatorControl, HVelocityMagnitude, _velocity.magnitude);
            DcmApi.SetFloat(customAnimatorControl, HVelocityX, _velocity.x);
            DcmApi.SetFloat(customAnimatorControl, HVelocityY, _velocity.y);
            DcmApi.SetFloat(customAnimatorControl, HVelocityZ, _velocity.z);
            // 원격 플레이어는 CharacterController 가 꺼져 있어 IsOnGround 가 신뢰할 수 없음 → 항상 접지로 취급
            DcmApi.SetBool(customAnimatorControl, HGrounded, true);

            var src = GetSourceAnimator();
            if (src == null)
            {
                DcmApi.SetBool(customAnimatorControl, HMoving, _velocity.sqrMagnitude > MovingThreshold * MovingThreshold);
                return;
            }

            var moveSpeed = _sourceFloats.Contains(HMoveSpeed) ? src.GetFloat(HMoveSpeed) : _velocity.magnitude;
            DcmApi.SetFloat(customAnimatorControl, HMoveSpeed, moveSpeed);
            DcmApi.SetBool(customAnimatorControl, HMoving, moveSpeed > MovingThreshold);

            if (_sourceFloats.Contains(HMoveDirX))
                DcmApi.SetFloat(customAnimatorControl, HMoveDirX, src.GetFloat(HMoveDirX));
            if (_sourceFloats.Contains(HMoveDirY))
                DcmApi.SetFloat(customAnimatorControl, HMoveDirY, src.GetFloat(HMoveDirY));
            if (_sourceBools.Contains(HDashing))
                DcmApi.SetBool(customAnimatorControl, HDashing, src.GetBool(HDashing));
        }

        private void UpdateVelocity()
        {
            var t = Time.time;
            var pos = transform.position;
            if (_lastTime < 0f || t <= _lastTime)
            {
                _lastPos = pos;
                _lastTime = t;
                return;
            }

            var dt = t - _lastTime;
            var raw = (pos - _lastPos) / dt;
            _velocity = Vector3.SmoothDamp(_velocity, raw, ref _velocityRef, VelocitySmoothTime, Mathf.Infinity, dt);
            _lastPos = pos;
            _lastTime = t;
        }

        /// <summary>COOP 가 값을 써 넣는 원본 모델의 Animator (COOP AnimParamInterpolator 와 같은 방식으로 찾음).</summary>
        private Animator? GetSourceAnimator()
        {
            if (_source != null && _source.runtimeAnimatorController == _sourceController) return _source;

            _source = null;
            _sourceFloats.Clear();
            _sourceBools.Clear();

            var model = _cmc != null ? _cmc.characterModel : null;
            if (model == null) return null;

            Animator? anim = null;
            var ac = model.GetComponentInChildren<CharacterAnimationControl>(true);
            if (ac != null) anim = ac.animator;
            if (anim == null)
            {
                var mb = model.GetComponentInChildren<CharacterAnimationControl_MagicBlend>(true);
                if (mb != null) anim = mb.animator;
            }

            if (anim == null || anim.runtimeAnimatorController == null) return null;

            // 존재하지 않는 파라미터를 GetFloat 하면 매 프레임 경고 로그가 찍히므로 미리 목록을 만든다.
            foreach (var p in anim.parameters)
                switch (p.type)
                {
                    case AnimatorControllerParameterType.Float:
                        _sourceFloats.Add(p.nameHash);
                        break;
                    case AnimatorControllerParameterType.Bool:
                        _sourceBools.Add(p.nameHash);
                        break;
                }

            _source = anim;
            _sourceController = anim.runtimeAnimatorController;
            return _source;
        }
    }
}
