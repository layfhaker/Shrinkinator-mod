using System;
using Photon.Pun;
using UnityEngine;

namespace Shrinkinator
{
    /// <summary>
    /// Базовые помощники для shrink-контроллеров: отправка RPC с учётом синглплеера.
    /// Применение эффекта инициирует ТОЛЬКО хост (спека 1.4); RPC уходит всем клиентам,
    /// чтобы визуал и физика менялись одинаково у всех.
    /// </summary>
    internal static class ShrinkRpc
    {
        /// <summary>
        /// Посылает RPC всем (в мультиплеере) или вызывает метод напрямую (в синглплеере,
        /// где Photon-обратной петли нет — так же делает сама игра, см. ItemGun.ShootBullet).
        /// </summary>
        internal static void SendToAll(PhotonView photonView, string method, Action directCall, params object[] args)
        {
            if (photonView == null)
            {
                Log.Warning("[Shrinkinator] Нет PhotonView для RPC " + method + " — применяем локально.");
                directCall();
                return;
            }

            if (SemiFunc.IsMultiplayer())
            {
                photonView.RPC(method, RpcTarget.All, args);
            }
            else
            {
                directCall();
            }
        }
    }

    /// <summary>
    /// Контроллер уменьшения ценности (ValuableObject). Постоянный эффект (спека 1.4):
    /// scale применяется однократно на выстрел и не откатывается. Повторные выстрелы
    /// стакаются мультипликативно, но не ниже ValuableMinScale от исходного размера.
    /// Вешается патчем на PhysGrabObject.Awake (тот же GameObject, что и ValuableObject).
    /// </summary>
    public class ValuableShrinkController : MonoBehaviour
    {
        private Vector3 _baseScale;
        private bool _hasBase;

        /// <summary>Вызывается хостом из обработчика выстрела.</summary>
        internal void ApplyFromHost(float scale, bool scalePrice)
        {
            if (!TryGetAppliedScale(scale, out float applied))
            {
                return;
            }

            ShrinkRpc.SendToAll(
                GetComponent<PhotonView>(),
                nameof(RPC_ApplyShrink),
                () => ApplyLocal(applied, scalePrice),
                applied, scalePrice);
        }

        [PunRPC]
        private void RPC_ApplyShrink(float scale, bool scalePrice)
        {
            ApplyLocal(scale, scalePrice);
        }

        /// <summary>
        /// Сколько ещё можно умножить текущий scale, чтобы не пробить пол
        /// ValuableMinScale относительно размера на первый выстрел.
        /// </summary>
        private bool TryGetAppliedScale(float requested, out float applied)
        {
            applied = requested;
            CaptureBaseScale();

            float minRelative = Mathf.Clamp(ShrinkinatorConfig.ValuableMinScale.Value, 0.05f, 1f);
            float original = Mathf.Max(Mathf.Abs(_baseScale.x), 0.0001f);
            float relative = Mathf.Abs(transform.localScale.x) / original;
            if (relative <= minRelative * 1.001f)
            {
                return false;
            }

            float nextRelative = relative * requested;
            if (nextRelative < minRelative)
            {
                applied = minRelative / relative;
            }

            return applied < 0.999f;
        }

        private void CaptureBaseScale()
        {
            if (_hasBase)
            {
                return;
            }

            _baseScale = transform.localScale;
            _hasBase = true;
        }

        private void ApplyLocal(float scale, bool scalePrice)
        {
            try
            {
                CaptureBaseScale();

                float minRelative = Mathf.Clamp(ShrinkinatorConfig.ValuableMinScale.Value, 0.05f, 1f);
                float original = Mathf.Max(Mathf.Abs(_baseScale.x), 0.0001f);
                float relative = Mathf.Abs(transform.localScale.x) / original;
                if (relative <= minRelative * 1.001f)
                {
                    return;
                }

                float applied = scale;
                float nextRelative = relative * scale;
                if (nextRelative < minRelative)
                {
                    applied = minRelative / relative;
                }

                if (applied >= 0.999f)
                {
                    return;
                }

                float volumeFactor = applied * applied * applied;

                transform.localScale = transform.localScale * applied;

                // Масса: меняем и rb.mass, и PhysGrabObject.massOriginal,
                // иначе ванильный ResetMass() вернёт старую массу.
                Rigidbody body = GetComponent<Rigidbody>();
                PhysGrabObject physGrab = GetComponent<PhysGrabObject>();
                if (body != null)
                {
                    float newMass = Mathf.Max(0.05f, body.mass * volumeFactor);
                    body.mass = newMass;
                    if (physGrab != null)
                    {
                        physGrab.massOriginal = newMass;
                    }
                }

                // Цена по умолчанию не меняется (ValueScalePrice = false).
                if (scalePrice)
                {
                    ValuableObject valuable = GetComponent<ValuableObject>();
                    if (valuable != null)
                    {
                        float newValue = Mathf.Max(1f, Mathf.Round(valuable.dollarValueCurrent * volumeFactor));
                        if (SemiFunc.IsMasterClientOrSingleplayer())
                        {
                            PhotonView valuableView = valuable.photonView != null ? valuable.photonView : GetComponent<PhotonView>();
                            if (SemiFunc.IsMultiplayer() && valuableView != null)
                            {
                                valuableView.RPC("DollarValueSetRPC", RpcTarget.All, newValue);
                            }
                            else
                            {
                                valuable.DollarValueSetRPC(newValue, default);
                            }
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка уменьшения ценности", e);
            }
        }
    }

    /// <summary>
    /// Уменьшение врага: только визуал (корень Animator) + масса.
    /// Коллайдеры, EnemyRigidbody и NavMesh не трогаем — иначе ломается хват,
    /// скиннинг и PhysFollow (моб улетает). Точку followTarget после скейла
    /// возвращаем в «несжатую» мировую позицию, чтобы физика осталась на месте.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public class EnemyShrinkController : MonoBehaviour
    {
        private static int _effectCounter;

        private bool _active;
        private int _effectId;
        private float _scale = 1f;
        private float _timer;
        private float _failsafeTimer;

        private Transform _visualRoot;
        private Vector3 _visualOriginalScale = Vector3.one;
        private Transform _follow;
        private bool _followUnderVisual;

        private float _massOriginal = -1f;
        private float _shrunkMass;
        private float _grabTimeNeededOriginal = -1f;
        private bool _grabOverrideOriginal;
        private bool _grabStunOriginal;
        private bool _grabSettingsStored;

        /// <summary>Вызывается хостом из обработчика выстрела.</summary>
        internal void ApplyFromHost(float scale, float duration)
        {
            int effectId = ++_effectCounter;
            ShrinkRpc.SendToAll(
                GetComponent<PhotonView>(),
                nameof(RPC_ApplyShrink),
                () => RPC_ApplyShrink(scale, duration, effectId),
                scale, duration, effectId);
        }

        [PunRPC]
        private void RPC_ApplyShrink(float scale, float duration, int effectId)
        {
            try
            {
                if (_active)
                {
                    if (Mathf.Approximately(_scale, scale))
                    {
                        _timer = duration;
                        _failsafeTimer = duration + 5f;
                        _effectId = effectId;
                        return;
                    }

                    RevertLocal();
                }

                CacheTargets();

                _active = true;
                _scale = scale;
                _effectId = effectId;
                _timer = duration;
                _failsafeTimer = duration + 5f;

                ApplyVisual();

                PhysGrabObject physGrab = GetComponent<PhysGrabObject>();
                Rigidbody body = GetComponent<Rigidbody>();
                float currentMass = physGrab != null ? physGrab.massOriginal : (body != null ? body.mass : 1f);
                if (currentMass <= 0f)
                {
                    currentMass = 1f;
                }

                _massOriginal = currentMass;
                _shrunkMass = Mathf.Max(0.5f, currentMass * scale);
                ApplyEnemyMass(_shrunkMass);

                EnemyRigidbody enemyRb = GetComponent<EnemyRigidbody>();
                if (enemyRb != null && !_grabSettingsStored)
                {
                    _grabTimeNeededOriginal = enemyRb.grabTimeNeeded;
                    _grabOverrideOriginal = enemyRb.grabOverride;
                    _grabStunOriginal = enemyRb.grabStun;
                    _grabSettingsStored = true;
                    // Как прокачанная сила: короткий рывок срывает моба, grabStun
                    // даёт короткое оглушение. GrabForce SO не трогаем (общий на тип).
                    enemyRb.grabTimeNeeded = Mathf.Min(enemyRb.grabTimeNeeded, 0.12f);
                    enemyRb.grabOverride = true;
                    enemyRb.grabStun = true;
                }
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка уменьшения врага", e);
            }
        }

        [PunRPC]
        private void RPC_Expand(int effectId)
        {
            if (_active && effectId == _effectId)
            {
                RevertLocal();
            }
        }

        private void CacheTargets()
        {
            _visualRoot = null;
            _follow = null;
            _followUnderVisual = false;

            EnemyRigidbody enemyRb = GetComponent<EnemyRigidbody>();
            EnemyParent enemyParent = GetComponentInParent<EnemyParent>();
            _follow = enemyRb != null ? enemyRb.followTarget : null;

            if (enemyParent != null)
            {
                Animator animator = enemyParent.GetComponentInChildren<Animator>();
                if (animator != null)
                {
                    _visualRoot = animator.transform;
                }
            }

            if (_visualRoot != null)
            {
                _visualOriginalScale = _visualRoot.localScale;
            }

            _followUnderVisual = _visualRoot != null && _follow != null
                && (_follow == _visualRoot || _follow.IsChildOf(_visualRoot));
        }

        private void ApplyVisual()
        {
            if (_visualRoot == null)
            {
                return;
            }

            _visualRoot.localScale = _visualOriginalScale * _scale;
            if (!IsBeingGrabbed())
            {
                StabilizeFollowTarget();
            }
        }

        private bool IsBeingGrabbed()
        {
            PhysGrabObject physGrab = GetComponent<PhysGrabObject>();
            if (physGrab != null && physGrab.playerGrabbing != null && physGrab.playerGrabbing.Count > 0)
            {
                return true;
            }

            EnemyRigidbody enemyRb = GetComponent<EnemyRigidbody>();
            return enemyRb != null && enemyRb.grabbed;
        }

        /// <summary>
        /// localScale визуала сжимает всех потомков к пивоту. followTarget —
        /// якорь PhysFollow, его мировую позицию возвращаем как без скейла.
        /// </summary>
        private void StabilizeFollowTarget()
        {
            if (!_followUnderVisual || _follow == null || _visualRoot == null)
            {
                return;
            }

            if (_scale < 0.0001f)
            {
                return;
            }

            Vector3 pivot = _visualRoot.position;
            Vector3 offset = _follow.position - pivot;
            _follow.position = pivot + offset / _scale;
        }

        private void ApplyEnemyMass(float mass)
        {
            PhysGrabObject physGrab = GetComponent<PhysGrabObject>();
            Rigidbody body = GetComponent<Rigidbody>();
            if (physGrab != null)
            {
                physGrab.massOriginal = mass;
            }
            if (body != null)
            {
                body.mass = mass;
            }
        }

        private void FixedUpdate()
        {
            if (!_active)
            {
                return;
            }

            ApplyEnemyMass(_shrunkMass);

            PhysGrabObject physGrab = GetComponent<PhysGrabObject>();
            if (physGrab != null)
            {
                // grabDisplacement = вектор рывка * grabStrength. Масса на порог
                // не влияет — без апгрейда силы порог GrabForce не взять.
                // Поднимаем силу хвата этого объекта, как если бы сила была прокачана.
                physGrab.OverrideGrabStrength(12f, 0.2f);
                physGrab.OverrideMinGrabStrength(8f, 0.2f);
            }

            if (IsBeingGrabbed())
            {
                EnemyRigidbody enemyRb = GetComponent<EnemyRigidbody>();
                if (enemyRb != null)
                {
                    enemyRb.DisableFollowPosition(0.2f, 2f);
                    enemyRb.DisableFollowRotation(0.2f, 2f);
                }
            }
        }

        private void Update()
        {
            if (!_active)
            {
                return;
            }

            float deltaTime = Time.deltaTime;
            _failsafeTimer -= deltaTime;

            if (SemiFunc.IsMasterClientOrSingleplayer())
            {
                _timer -= deltaTime;
                if (_timer <= 0f)
                {
                    int effectId = _effectId;
                    ShrinkRpc.SendToAll(
                        GetComponent<PhotonView>(),
                        nameof(RPC_Expand),
                        () => RPC_Expand(effectId),
                        effectId);
                }
            }

            if (_failsafeTimer <= 0f)
            {
                RevertLocal();
            }
        }

        private void LateUpdate()
        {
            if (_active)
            {
                ApplyVisual();
            }
        }

        private void RevertLocal()
        {
            try
            {
                if (_visualRoot != null)
                {
                    _visualRoot.localScale = _visualOriginalScale;
                }

                if (_massOriginal > 0f)
                {
                    ApplyEnemyMass(_massOriginal);
                }

                EnemyRigidbody enemyRb = GetComponent<EnemyRigidbody>();
                if (enemyRb != null && _grabSettingsStored)
                {
                    enemyRb.grabTimeNeeded = _grabTimeNeededOriginal;
                    enemyRb.grabOverride = _grabOverrideOriginal;
                    enemyRb.grabStun = _grabStunOriginal;
                }
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка возврата размера врага", e);
            }
            finally
            {
                _active = false;
                _visualRoot = null;
                _follow = null;
                _followUnderVisual = false;
                _massOriginal = -1f;
                _grabSettingsStored = false;
            }
        }

        private void OnDisable()
        {
            if (_active)
            {
                RevertLocal();
            }
        }
    }

    /// <summary>
    /// Контроллер временного уменьшения игрока.
    /// Визуал — meshParent. Сетевая коллизия — PlayerAvatarCollision.CollisionTransform.
    /// У локального игрока настоящий хитбокс и камера живут отдельно
    /// (PlayerCollision / CameraPosition / PlayerVisionTarget) — их тоже сжимаем,
    /// иначе от первого лица ничего не меняется.
    /// Вешается патчем на PlayerAvatar.Start.
    /// </summary>
    [DefaultExecutionOrder(20000)]
    public class PlayerShrinkController : MonoBehaviour
    {
        private static int _effectCounter;

        private bool _active;
        private int _effectId;
        private float _scale = 1f;
        private float _timer;
        private float _failsafeTimer;

        private PlayerAvatarCollision _collision;
        private Vector3 _collisionOriginalScale;
        private Vector3 _collisionShrunkScale;
        private Transform _visual;
        private Vector3 _visualOriginalScale;
        private bool _warnedCollisionScale;

        private bool _localFeel;
        private Vector3 _camOffsetOriginal;
        private float _crouchPosOriginal;
        private float _crawlPosOriginal;
        private float _visionStandOriginal;
        private float _visionCrouchOriginal;
        private float _visionCrawlOriginal;
        private float _visionHeadStandOriginal;
        private float _visionHeadCrouchOriginal;
        private float _visionHeadCrawlOriginal;
        private PlayerVisionTarget _vision;
        private Vector3 _standCollisionOriginal;
        private Vector3 _crouchCollisionOriginal;
        private CapsuleCollider _standCheckCollider;
        private float _standCheckHeightOriginal;
        private float _standCheckRadiusOriginal;
        private Vector3 _standCheckOffsetOriginal;
        private float _fovOriginal;
        private float _nearClipOriginal;
        private bool _hasNearClip;
        private float _grabMinOriginal;
        private float _grabMaxOriginal;
        private float _grabMinOriginalOriginal;
        private float _grabRangeOriginal;
        private bool _hasGrabDistances;
        private float _speedMult = 1f;

        /// <summary>Вызывается хостом из обработчика выстрела.</summary>
        internal void ApplyFromHost(float scale, float duration)
        {
            PlayerAvatar avatar = GetComponent<PlayerAvatar>();
            PhotonView view = avatar != null ? avatar.photonView : GetComponent<PhotonView>();
            int effectId = ++_effectCounter;
            ShrinkRpc.SendToAll(
                view,
                nameof(RPC_ApplyShrink),
                () => RPC_ApplyShrink(scale, duration, effectId),
                scale, duration, effectId);
        }

        [PunRPC]
        private void RPC_ApplyShrink(float scale, float duration, int effectId)
        {
            try
            {
                if (_active)
                {
                    if (Mathf.Approximately(_scale, scale))
                    {
                        // Повторный выстрел — обновляем таймер, не стакая scale.
                        _timer = duration;
                        _failsafeTimer = duration + 5f;
                        _effectId = effectId;
                        RefreshLocalFeelTimers();
                        return;
                    }
                    RevertLocal();
                }

                CacheTargets();

                _active = true;
                _scale = scale;
                _effectId = effectId;
                _timer = duration;
                _failsafeTimer = duration + 5f;

                if (_collision != null)
                {
                    _collisionOriginalScale = _collision.Scale;
                    _collisionShrunkScale = _collisionOriginalScale * scale;
                }

                if (_visual != null)
                {
                    _visualOriginalScale = _visual.localScale;
                    _visual.localScale = _visualOriginalScale * scale;
                }

                ApplyLocalFeel();
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка уменьшения игрока", e);
            }
        }

        [PunRPC]
        private void RPC_Expand(int effectId)
        {
            if (_active && effectId == _effectId)
            {
                RevertLocal();
            }
        }

        /// <summary>
        /// PlayerAvatarCollision живёт на отдельном объекте (НЕ ребёнок PlayerAvatar),
        /// поэтому ищем его перебором по ссылке на наш аватар.
        /// </summary>
        private void CacheTargets()
        {
            _collision = null;
            _visual = null;

            PlayerAvatar avatar = GetComponent<PlayerAvatar>();
            if (avatar == null)
            {
                return;
            }

            foreach (PlayerAvatarCollision collision in FindObjectsOfType<PlayerAvatarCollision>())
            {
                if (collision != null && collision.PlayerAvatar == avatar)
                {
                    _collision = collision;
                    break;
                }
            }

            if (avatar.playerAvatarVisuals != null && avatar.playerAvatarVisuals.meshParent != null)
            {
                _visual = avatar.playerAvatarVisuals.meshParent.transform;
            }
        }

        private void Update()
        {
            if (!_active)
            {
                return;
            }

            // Смерть игрока — убираем эффект (спека 1.4).
            PlayerAvatar avatar = GetComponent<PlayerAvatar>();
            if (avatar == null || avatar.isDisabled)
            {
                RevertLocal();
                return;
            }

            float deltaTime = Time.deltaTime;
            _failsafeTimer -= deltaTime;

            if (SemiFunc.IsMasterClientOrSingleplayer())
            {
                _timer -= deltaTime;
                if (_timer <= 0f)
                {
                    int effectId = _effectId;
                    PhotonView view = avatar.photonView != null ? avatar.photonView : GetComponent<PhotonView>();
                    ShrinkRpc.SendToAll(
                        view,
                        nameof(RPC_Expand),
                        () => RPC_Expand(effectId),
                        effectId);
                }
            }

            if (_failsafeTimer <= 0f)
            {
                RevertLocal();
            }
        }

        private void LateUpdate()
        {
            if (!_active)
            {
                return;
            }

            if (_visual != null)
            {
                _visual.localScale = _visualOriginalScale * _scale;
            }

            // Локальный хитбокс берётся из шаблонов PlayerCollision (stand/crouch).
            // Если здесь заморозить Scale, сломается присед и Photon уйдёт с неверным размером.
            if (!IsLocalAvatar())
            {
                ReinforceCollisionScale();
            }

            ReinforceLocalFeel();
        }

        private void ReinforceCollisionScale()
        {
            if (_collision == null)
            {
                return;
            }

            // PlayerAvatarCollision.Update каждый кадр перезаписывает Scale и
            // CollisionTransform.localScale (для локального игрока — из PlayerCollision),
            // поэтому подкрепляем наш масштаб ПОСЛЕ него, в LateUpdate.
            try
            {
                _collision.Scale = _collisionShrunkScale;
                if (_collision.CollisionTransform != null)
                {
                    _collision.CollisionTransform.localScale = _collisionShrunkScale;
                }
            }
            catch (Exception e)
            {
                if (!_warnedCollisionScale)
                {
                    _warnedCollisionScale = true;
                    Log.Warning("[Shrinkinator] Не удалось подкрепить масштаб коллизии игрока "
                                + "(дальше предупреждения подавляются): " + e.Message);
                }
            }
        }

        private bool IsLocalAvatar()
        {
            PlayerAvatar avatar = GetComponent<PlayerAvatar>();
            return avatar != null && avatar.isLocal;
        }

        /// <summary>
        /// Камера, хитбоксы и рука локального игрока. PlayerCollision.instance и
        /// CameraPosition.instance — синглтоны локального клиента, чужих не трогаем.
        /// </summary>
        private void ApplyLocalFeel()
        {
            if (_localFeel || !IsLocalAvatar())
            {
                return;
            }

            try
            {
                if (CameraPosition.instance != null)
                {
                    _camOffsetOriginal = CameraPosition.instance.playerOffset;
                }

                if (CameraCrouchPosition.instance != null)
                {
                    _crouchPosOriginal = CameraCrouchPosition.instance.Position;
                }

                if (CameraCrawlPosition.instance != null)
                {
                    _crawlPosOriginal = CameraCrawlPosition.instance.Position;
                }

                PlayerAvatar avatar = GetComponent<PlayerAvatar>();
                _vision = avatar != null ? avatar.PlayerVisionTarget : null;
                if (_vision != null)
                {
                    _visionStandOriginal = _vision.StandPosition;
                    _visionCrouchOriginal = _vision.CrouchPosition;
                    _visionCrawlOriginal = _vision.CrawlPosition;
                    _visionHeadStandOriginal = _vision.HeadStandPosition;
                    _visionHeadCrouchOriginal = _vision.HeadCrouchPosition;
                    _visionHeadCrawlOriginal = _vision.HeadCrawlPosition;
                }

                if (PlayerCollision.instance != null)
                {
                    if (PlayerCollision.instance.StandCollision != null)
                    {
                        _standCollisionOriginal = PlayerCollision.instance.StandCollision.localScale;
                    }

                    if (PlayerCollision.instance.CrouchCollision != null)
                    {
                        _crouchCollisionOriginal = PlayerCollision.instance.CrouchCollision.localScale;
                    }
                }

                if (PlayerCollisionStand.instance != null)
                {
                    _standCheckCollider = PlayerCollisionStand.instance.GetComponent<CapsuleCollider>();
                    if (_standCheckCollider != null)
                    {
                        _standCheckHeightOriginal = _standCheckCollider.height;
                        _standCheckRadiusOriginal = _standCheckCollider.radius;
                    }

                    _standCheckOffsetOriginal = PlayerCollisionStand.instance.Offset;
                }

                if (CameraZoom.Instance != null)
                {
                    _fovOriginal = CameraZoom.Instance.playerZoomDefault;
                }

                if (AssetManager.instance != null && AssetManager.instance.mainCamera != null)
                {
                    _nearClipOriginal = AssetManager.instance.mainCamera.nearClipPlane;
                    _hasNearClip = true;
                }

                PhysGrabber grabber = PhysGrabber.instance;
                if (grabber != null)
                {
                    _grabMinOriginal = grabber.minDistanceFromPlayer;
                    _grabMaxOriginal = grabber.maxDistanceFromPlayer;
                    _grabMinOriginalOriginal = grabber.minDistanceFromPlayerOriginal;
                    _grabRangeOriginal = grabber.grabRange;
                    _hasGrabDistances = true;
                }

                _speedMult = Mathf.Lerp(1f, _scale, 0.5f);
                _localFeel = true;

                if (_hasNearClip && AssetManager.instance != null && AssetManager.instance.mainCamera != null)
                {
                    AssetManager.instance.mainCamera.nearClipPlane = Mathf.Max(0.01f, _nearClipOriginal * _scale * 0.5f);
                }

                if (CameraZoom.Instance != null)
                {
                    CameraZoom.Instance.playerZoomDefault = _fovOriginal + 20f * (1f - _scale);
                }

                ReinforceLocalFeel();
                RefreshLocalFeelTimers();
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка локальной камеры/хитбокса игрока", e);
            }
        }

        private void RefreshLocalFeelTimers()
        {
            if (!_localFeel)
            {
                return;
            }

            try
            {
                if (CameraZoom.Instance != null)
                {
                    float newFov = CameraZoom.Instance.playerZoomDefault;
                    CameraZoom.Instance.OverrideZoomSet(newFov, 9999f, 3f, 3f, gameObject, 999);
                }

                if (PlayerController.instance != null)
                {
                    PlayerController.instance.OverrideSpeed(_speedMult, 9999f);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Shrinkinator] Не удалось продлить локальный эффект уменьшения: " + e.Message);
            }
        }

        private void ReinforceLocalFeel()
        {
            if (!_localFeel)
            {
                return;
            }

            try
            {
                if (CameraPosition.instance != null)
                {
                    CameraPosition.instance.playerOffset = _camOffsetOriginal * _scale;
                }

                if (CameraCrouchPosition.instance != null)
                {
                    CameraCrouchPosition.instance.Position = _crouchPosOriginal * _scale;
                }

                if (CameraCrawlPosition.instance != null)
                {
                    CameraCrawlPosition.instance.Position = _crawlPosOriginal * _scale;
                }

                if (_vision != null)
                {
                    _vision.StandPosition = _visionStandOriginal * _scale;
                    _vision.CrouchPosition = _visionCrouchOriginal * _scale;
                    _vision.CrawlPosition = _visionCrawlOriginal * _scale;
                    _vision.HeadStandPosition = _visionHeadStandOriginal * _scale;
                    _vision.HeadCrouchPosition = _visionHeadCrouchOriginal * _scale;
                    _vision.HeadCrawlPosition = _visionHeadCrawlOriginal * _scale;
                }

                if (PlayerCollision.instance != null)
                {
                    if (PlayerCollision.instance.StandCollision != null)
                    {
                        PlayerCollision.instance.StandCollision.localScale = _standCollisionOriginal * _scale;
                    }

                    if (PlayerCollision.instance.CrouchCollision != null)
                    {
                        PlayerCollision.instance.CrouchCollision.localScale = _crouchCollisionOriginal * _scale;
                    }
                }

                if (_standCheckCollider != null)
                {
                    _standCheckCollider.height = _standCheckHeightOriginal * _scale;
                    _standCheckCollider.radius = _standCheckRadiusOriginal * _scale;
                }

                if (PlayerCollisionStand.instance != null)
                {
                    PlayerCollisionStand.instance.Offset = _standCheckOffsetOriginal * _scale;
                }

                if (_hasGrabDistances && PhysGrabber.instance != null)
                {
                    PhysGrabber.instance.minDistanceFromPlayer = _grabMinOriginal * _scale;
                    PhysGrabber.instance.maxDistanceFromPlayer = _grabMaxOriginal * _scale;
                    PhysGrabber.instance.minDistanceFromPlayerOriginal = _grabMinOriginalOriginal * _scale;
                    PhysGrabber.instance.grabRange = _grabRangeOriginal * _scale;
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Shrinkinator] Не удалось подкрепить локальный эффект уменьшения: " + e.Message);
            }
        }

        private void RestoreLocalFeel()
        {
            if (!_localFeel)
            {
                return;
            }

            try
            {
                if (CameraPosition.instance != null)
                {
                    CameraPosition.instance.playerOffset = _camOffsetOriginal;
                }

                if (CameraCrouchPosition.instance != null)
                {
                    CameraCrouchPosition.instance.Position = _crouchPosOriginal;
                }

                if (CameraCrawlPosition.instance != null)
                {
                    CameraCrawlPosition.instance.Position = _crawlPosOriginal;
                }

                if (_vision != null)
                {
                    _vision.StandPosition = _visionStandOriginal;
                    _vision.CrouchPosition = _visionCrouchOriginal;
                    _vision.CrawlPosition = _visionCrawlOriginal;
                    _vision.HeadStandPosition = _visionHeadStandOriginal;
                    _vision.HeadCrouchPosition = _visionHeadCrouchOriginal;
                    _vision.HeadCrawlPosition = _visionHeadCrawlOriginal;
                }

                if (PlayerCollision.instance != null)
                {
                    if (PlayerCollision.instance.StandCollision != null)
                    {
                        PlayerCollision.instance.StandCollision.localScale = _standCollisionOriginal;
                    }

                    if (PlayerCollision.instance.CrouchCollision != null)
                    {
                        PlayerCollision.instance.CrouchCollision.localScale = _crouchCollisionOriginal;
                    }
                }

                if (_standCheckCollider != null)
                {
                    _standCheckCollider.height = _standCheckHeightOriginal;
                    _standCheckCollider.radius = _standCheckRadiusOriginal;
                }

                if (PlayerCollisionStand.instance != null)
                {
                    PlayerCollisionStand.instance.Offset = _standCheckOffsetOriginal;
                }

                if (CameraZoom.Instance != null)
                {
                    CameraZoom.Instance.playerZoomDefault = _fovOriginal;
                    CameraZoom.Instance.OverrideZoomSet(_fovOriginal, 0.5f, 3f, 3f, gameObject, 999);
                }

                if (_hasNearClip && AssetManager.instance != null && AssetManager.instance.mainCamera != null)
                {
                    AssetManager.instance.mainCamera.nearClipPlane = _nearClipOriginal;
                }

                if (_hasGrabDistances && PhysGrabber.instance != null)
                {
                    PhysGrabber.instance.minDistanceFromPlayer = _grabMinOriginal;
                    PhysGrabber.instance.maxDistanceFromPlayer = _grabMaxOriginal;
                    PhysGrabber.instance.minDistanceFromPlayerOriginal = _grabMinOriginalOriginal;
                    PhysGrabber.instance.grabRange = _grabRangeOriginal;
                }

                if (PlayerController.instance != null)
                {
                    PlayerController.instance.OverrideSpeed(1f, 0.1f);
                }
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка возврата камеры/хитбокса игрока", e);
            }
            finally
            {
                _localFeel = false;
                _vision = null;
                _standCheckCollider = null;
                _hasNearClip = false;
                _hasGrabDistances = false;
            }
        }

        private void RevertLocal()
        {
            try
            {
                RestoreLocalFeel();

                if (_collision != null)
                {
                    _collision.Scale = _collisionOriginalScale;
                    if (_collision.CollisionTransform != null)
                    {
                        _collision.CollisionTransform.localScale = _collisionOriginalScale;
                    }
                }

                if (_visual != null)
                {
                    _visual.localScale = _visualOriginalScale;
                }
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка возврата размера игрока", e);
            }
            finally
            {
                _active = false;
                _collision = null;
                _visual = null;
                _warnedCollisionScale = false;
            }
        }

        private void OnDisable()
        {
            if (_active)
            {
                RevertLocal();
            }
        }
    }
}
