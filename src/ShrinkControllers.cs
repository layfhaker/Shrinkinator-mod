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
    /// стакаются мультипликативно — ценность можно уменьшать несколько раз.
    /// Вешается патчем на PhysGrabObject.Awake (тот же GameObject, что и ValuableObject).
    /// </summary>
    public class ValuableShrinkController : MonoBehaviour
    {
        /// <summary>Вызывается хостом из обработчика выстрела.</summary>
        internal void ApplyFromHost(float scale, bool scalePrice)
        {
            ShrinkRpc.SendToAll(
                GetComponent<PhotonView>(),
                nameof(RPC_ApplyShrink),
                () => ApplyLocal(scale, scalePrice),
                scale, scalePrice);
        }

        [PunRPC]
        private void RPC_ApplyShrink(float scale, bool scalePrice)
        {
            ApplyLocal(scale, scalePrice);
        }

        private void ApplyLocal(float scale, bool scalePrice)
        {
            try
            {
                float volumeFactor = scale * scale * scale;

                // Размер — навсегда.
                transform.localScale = transform.localScale * scale;

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
    /// Контроллер временного уменьшения игрока (спека 1.4).
    /// Скейлит PlayerAvatarCollision (через мост CollisionTransform) и визуал
    /// (playerAvatarVisuals.meshParent). Собственный CharacterController локального
    /// игрока и камера не трогаются — см. известные ограничения в README.
    /// Вешается патчем на PlayerAvatar.Start.
    /// </summary>
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
            if (!_active || _collision == null)
            {
                return;
            }

            // PlayerAvatarCollision.Update каждый кадр перезаписывает Scale и
            // CollisionTransform.localScale (для локального игрока — из PlayerController),
            // поэтому подкрепляем наш масштаб ПОСЛЕ него, в LateUpdate.
            // Поле Scale доступно через паблисайзер — оборачиваем в try/catch на случай
            // смены паблисайзинга/версии игры; предупреждение логируем один раз,
            // чтобы не спамить лог каждый кадр.
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

        private void RevertLocal()
        {
            try
            {
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
