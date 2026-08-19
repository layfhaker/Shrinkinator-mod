using System;
using System.Collections.Generic;
using HarmonyLib;
using Photon.Pun;
using UnityEngine;

namespace Shrinkinator
{
    /// <summary>
    /// Регистрация предмета в момент, когда ванильные предметы уже загружены
    /// (тот же тайминг, что использует REPOLib для своих предметов).
    /// </summary>
    [HarmonyPatch(typeof(StatsManager), nameof(StatsManager.RunStartStats))]
    internal static class StatsManagerPatch
    {
        private static void Postfix()
        {
            try
            {
                ItemRegistration.TryRegister();
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка в постфиксе RunStartStats", e);
            }
        }
    }

    /// <summary>
    /// Выстрел туманом (спека 1.3). ShootBulletRPC приходит всем клиентам:
    /// VFX рисуем у всех, поиск целей и применение — только на хосте.
    /// </summary>
    [HarmonyPatch(typeof(ItemGun), nameof(ItemGun.ShootBulletRPC))]
    internal static class ItemGunShootPatch
    {
        private static float _lastCloudTime;
        private static int _lastCloudGun;

        private static void Postfix(ItemGun __instance, Vector3 _endPosition, bool _hit)
        {
            try
            {
                if (!ItemRegistration.IsOurGun(__instance))
                {
                    return;
                }

                if (__instance.hurtCollider != null)
                {
                    __instance.hurtCollider.enabled = false;
                    __instance.hurtCollider.gameObject.SetActive(false);
                }

                // Ваниль может вызвать ShootBulletRPC несколько раз за клик
                // (numberOfBullets). Один выстрел — одно облако и один ApplyCloud.
                int gunId = __instance.GetInstanceID();
                float now = Time.unscaledTime;
                if (gunId == _lastCloudGun && now - _lastCloudTime < 0.08f)
                {
                    return;
                }
                _lastCloudGun = gunId;
                _lastCloudTime = now;

                Vector3 center = ComputeCloudCenter(__instance, _endPosition, _hit);
                float radius = Mathf.Max(0.3f, ShrinkinatorConfig.CloudRadius.Value);

                // Туман видят все клиенты (RPC и так разослан всем).
                MistVfx.Spawn(center, radius);

                // Применение эффекта — только хост.
                if (!SemiFunc.IsMasterClientOrSingleplayer())
                {
                    return;
                }

                ApplyCloud(__instance, center, radius);
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка в обработчике выстрела", e);
            }
        }

        /// <summary>
        /// Центр облака: точка попадания (ограниченная дальностью тумана)
        /// или точка на дальности MistRange вдоль ствола, если луч ни во что не попал.
        /// </summary>
        private static Vector3 ComputeCloudCenter(ItemGun gun, Vector3 endPosition, bool hit)
        {
            Transform muzzle = gun.gunMuzzle;
            float range = Mathf.Max(1f, ShrinkinatorConfig.MistRange.Value);

            if (muzzle == null)
            {
                return endPosition;
            }

            if (hit)
            {
                Vector3 offset = endPosition - muzzle.position;
                if (offset.magnitude > range)
                {
                    return muzzle.position + offset.normalized * range;
                }
                return endPosition;
            }

            return muzzle.position + muzzle.forward * range;
        }

        /// <summary>
        /// Обрабатывает облако: основная сфера в точке попадания + промежуточная
        /// на середине луча, чтобы «туман» покрывал путь, а не только точку (спека 1.3).
        /// </summary>
        private static void ApplyCloud(ItemGun gun, Vector3 center, float radius)
        {
            Transform muzzle = gun.gunMuzzle;
            Vector3 origin = muzzle != null ? muzzle.position : center;

            int mask = SemiFunc.LayerMaskGetPhysGrabObject().value
                       | LayerMask.GetMask("Enemy", "Player");

            // Защита от двойной обработки одной цели из нескольких сфер.
            var handledValuables = new HashSet<ValuableObject>();
            var handledEnemies = new HashSet<EnemyRigidbody>();
            var handledPlayers = new HashSet<PlayerAvatar>();

            // Мост PlayerAvatar -> PlayerAvatarCollision: коллизия игрока — отдельный
            // корневой объект сцены (не ребёнок аватара), собираем соответствие заранее.
            Dictionary<PlayerAvatar, PlayerAvatarCollision> playerCollisions = CollectPlayerCollisions();

            // 1.0 — точка попадания, 0.5 — середина луча, 0.25 — ближняя зона у ствола
            // (без неё туман «не видел» цели вплотную к игроку).
            float[] fractions = { 1f, 0.5f, 0.25f };
            foreach (float fraction in fractions)
            {
                Vector3 sphereCenter = Vector3.Lerp(origin, center, fraction);
                Collider[] colliders;
                try
                {
                    colliders = Physics.OverlapSphere(sphereCenter, radius, mask, QueryTriggerInteraction.Collide);
                }
                catch (Exception e)
                {
                    Log.Warning("[Shrinkinator] OverlapSphere не удался: " + e.Message);
                    continue;
                }

                foreach (Collider collider in colliders)
                {
                    if (collider == null)
                    {
                        continue;
                    }

                    try
                    {
                        HandleCollider(collider, gun, handledValuables, handledEnemies, handledPlayers, playerCollisions);
                    }
                    catch (Exception e)
                    {
                        Log.Error("[Shrinkinator] Ошибка обработки цели в облаке", e);
                    }
                }
            }
        }

        private static void HandleCollider(
            Collider collider,
            ItemGun gun,
            HashSet<ValuableObject> handledValuables,
            HashSet<EnemyRigidbody> handledEnemies,
            HashSet<PlayerAvatar> handledPlayers,
            Dictionary<PlayerAvatar, PlayerAvatarCollision> playerCollisions)
        {
            float scale = Mathf.Clamp(ShrinkinatorConfig.ScaleFactor.Value, 0.05f, 1f);
            float duration = Mathf.Max(1f, ShrinkinatorConfig.DurationSeconds.Value);

            // --- Ценность (спека: у ValuableObject есть ValuableObject-компонент) ---
            ValuableObject valuable = collider.GetComponentInParent<ValuableObject>();
            if (valuable != null)
            {
                if (handledValuables.Add(valuable))
                {
                    ValuableShrinkController controller = AttachPatches.EnsureValuableController(valuable.gameObject);
                    if (controller != null)
                    {
                        controller.ApplyFromHost(scale, ShrinkinatorConfig.ValueScalePrice.Value);
                    }
                }
                return;
            }

            // --- Прочие предметы (в т.ч. наша и ванильные пушки) не трогаем ---
            if (collider.GetComponentInParent<ItemAttributes>() != null)
            {
                return;
            }

            // --- Враг ---
            EnemyRigidbody enemyRb = collider.GetComponentInParent<EnemyRigidbody>();
            if (enemyRb != null)
            {
                if (handledEnemies.Add(enemyRb))
                {
                    EnemyShrinkController controller = AttachPatches.EnsureEnemyController(enemyRb.gameObject);
                    if (controller != null)
                    {
                        controller.ApplyFromHost(scale, duration);
                    }
                }
                return;
            }

            // --- Игрок (слой Player) ---
            // Коллайдеры игрока — это отдельные корневые объекты сцены
            // (PlayerAvatarCollision.CollisionTransform и CharacterController на
            // PlayerController GO), у них НЕТ PlayerAvatar/PlayerAvatarCollision
            // в родителях, поэтому GetComponentInParent здесь бесполезен.
            // Как в референсе ShrinkerGun: ищем ближайшего игрока перебором
            // GameDirector.instance.PlayerList по расстоянию до точки попадания.
            if (collider.gameObject.layer == LayerMask.NameToLayer("Player"))
            {
                PlayerAvatar avatar = FindNearestPlayer(collider.transform.position, playerCollisions, 2f);
                if (avatar != null && handledPlayers.Add(avatar))
                {
                    // Контроллер вешается на PlayerAvatar (патч PlayerAvatar.Start).
                    PlayerShrinkController controller = AttachPatches.EnsurePlayerController(avatar.gameObject);
                    if (controller != null)
                    {
                        controller.ApplyFromHost(scale, duration);
                    }
                }
            }
        }

        /// <summary>
        /// Строит соответствие PlayerAvatar -> его PlayerAvatarCollision
        /// (компонент коллизии живёт на отдельном корневом объекте сцены,
        /// поэтому достать его можно только перебором по ссылке PlayerAvatar).
        /// </summary>
        private static Dictionary<PlayerAvatar, PlayerAvatarCollision> CollectPlayerCollisions()
        {
            var map = new Dictionary<PlayerAvatar, PlayerAvatarCollision>();
            foreach (PlayerAvatarCollision collision in UnityEngine.Object.FindObjectsOfType<PlayerAvatarCollision>())
            {
                if (collision != null && collision.PlayerAvatar != null && !map.ContainsKey(collision.PlayerAvatar))
                {
                    map[collision.PlayerAvatar] = collision;
                }
            }
            return map;
        }

        /// <summary>
        /// Ближайший живой игрок к точке попадания в радиусе maxDistance.
        /// Позицию игрока берём из его PlayerAvatarCollision (CollisionTransform) —
        /// аватар и коллизия могут заметно расходиться; мёртвых/отключённых пропускаем.
        /// </summary>
        private static PlayerAvatar FindNearestPlayer(
            Vector3 point,
            Dictionary<PlayerAvatar, PlayerAvatarCollision> playerCollisions,
            float maxDistance)
        {
            if (GameDirector.instance == null)
            {
                return null;
            }

            PlayerAvatar nearest = null;
            float nearestSqr = maxDistance * maxDistance;

            foreach (PlayerAvatar avatar in GameDirector.instance.PlayerList)
            {
                if (avatar == null || avatar.isDisabled)
                {
                    continue;
                }

                Vector3 playerPos;
                PlayerAvatarCollision collision;
                if (playerCollisions != null
                    && playerCollisions.TryGetValue(avatar, out collision)
                    && collision != null)
                {
                    playerPos = collision.CollisionTransform != null
                        ? collision.CollisionTransform.position
                        : collision.transform.position;
                }
                else
                {
                    playerPos = avatar.transform.position;
                }

                float sqr = (playerPos - point).sqrMagnitude;
                if (sqr <= nearestSqr)
                {
                    nearestSqr = sqr;
                    nearest = avatar;
                }
            }

            return nearest;
        }
    }

    /// <summary>
    /// Резолв runtime-префаба без Harmony-патча Resources.Load.
    ///
    /// Глобальный перехват Resources.Load ломает TMP_Settings: в главном меню
    /// пропадают все кнопки (TextMeshProUGUI.Awake → NRE). Вместо этого:
    /// 1. Кладём шаблон в кэши RunManager (PrefabRef.Prefab читает их раньше Load).
    /// 2. Перехватываем сам PrefabRef.get_Prefab — это путь админ-меню в одиночке
    ///    (Object.Instantiate(item.prefab.Prefab)).
    /// 3. Перехватываем MultiplayerPool.Instantiate — путь Photon/магазина в сети.
    /// </summary>
    internal static class NetworkPrefabResolverPatch
    {
        private static bool _cacheLogged;

        /// <summary>
        /// Пока true, postfix Instantiate не включает клон — PUN сам включает
        /// объект, полученный из IPunPrefabPool.
        /// </summary>
        internal static bool SuppressCloneActivation;

        /// <summary>
        /// Кладёт runtime-префаб в оба кэша, которые читает PrefabRef&lt;GameObject&gt;.
        /// Поля internal в игровой сборке, доступны через publicizer.
        /// </summary>
        internal static void CacheRuntimePrefab(MultiplayerPool preferredPool = null)
        {
            GameObject template = ItemRegistration.Template;
            if (template == null)
            {
                return;
            }

            try
            {
                bool cached = false;
                RunManager runManager = RunManager.instance;
                if (runManager != null)
                {
                    if (runManager.singleplayerPool != null)
                    {
                        runManager.singleplayerPool[ItemRegistration.NetworkPrefabPath] = template;
                        cached = true;
                    }

                    preferredPool = preferredPool ?? runManager.multiplayerPool;
                }

                if (preferredPool != null && preferredPool.ResourceCache != null)
                {
                    preferredPool.ResourceCache[ItemRegistration.NetworkPrefabPath] = template;
                    cached = true;
                }

                if (cached && !_cacheLogged)
                {
                    _cacheLogged = true;
                    Log.Info("[Shrinkinator] Runtime-префаб добавлен в кэши одиночного и сетевого спавна.");
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Shrinkinator] Не удалось заполнить кэш runtime-префаба: " + e.Message);
            }
        }

        internal static GameObject InstantiateTemplate(Vector3 position, Quaternion rotation)
        {
            GameObject template = ItemRegistration.Template;
            // Шаблон всегда неактивен: клон тоже рождается выключенным.
            // PUN потом сам включает объект из пула.
            return UnityEngine.Object.Instantiate(template, position, rotation);
        }

        /// <summary>
        /// После Awake пулы уже созданы. Если предмет уже зарегистрирован
        /// (повторный заход в лобби), заново кладём его в кэш.
        /// </summary>
        [HarmonyPatch(typeof(RunManager), "Awake")]
        [HarmonyPriority(Priority.Last)]
        internal static class RunManagerAwakeCachePatch
        {
            private static void Postfix(RunManager __instance)
            {
                try
                {
                    if (ItemRegistration.Template != null)
                    {
                        CacheRuntimePrefab(__instance != null ? __instance.multiplayerPool : null);
                    }
                }
                catch (Exception e)
                {
                    Log.Warning("[Shrinkinator] Не удалось обновить кэш префаба в RunManager.Awake: " + e.Message);
                }
            }
        }

        /// <summary>
        /// Админ-меню (и магазин в одиночке) спавнят через item.prefab.Prefab.
        /// Геттер иначе идёт в Resources.Load и получает null на runtime-префабе.
        /// </summary>
        [HarmonyPatch(typeof(PrefabRef<GameObject>), nameof(PrefabRef<GameObject>.Prefab), MethodType.Getter)]
        internal static class PrefabRefGetPrefabPatch
        {
            private static bool Prefix(PrefabRef<GameObject> __instance, ref GameObject __result)
            {
                try
                {
                    GameObject template = ItemRegistration.Template;
                    if (template == null || __instance == null)
                    {
                        return true;
                    }

                    if (__instance.ResourcePath != ItemRegistration.NetworkPrefabPath)
                    {
                        return true;
                    }

                    CacheRuntimePrefab();
                    __result = template;
                    return false;
                }
                catch (Exception e)
                {
                    Log.Error("[Shrinkinator] Ошибка в PrefabRef.Prefab", e);
                    return true;
                }
            }
        }

        /// <summary>Спавн напрямую через MultiplayerPool.Instantiate.</summary>
        [HarmonyPatch(typeof(MultiplayerPool), nameof(MultiplayerPool.Instantiate))]
        internal static class MultiplayerPoolInstantiatePatch
        {
            private static bool Prefix(
                MultiplayerPool __instance,
                string prefabId,
                Vector3 position,
                Quaternion rotation,
                ref GameObject __result)
            {
                try
                {
                    if (ItemRegistration.Template == null || prefabId != ItemRegistration.NetworkPrefabPath)
                    {
                        return true;
                    }

                    CacheRuntimePrefab(__instance);
                    SuppressCloneActivation = true;
                    try
                    {
                        __result = InstantiateTemplate(position, rotation);
                    }
                    finally
                    {
                        SuppressCloneActivation = false;
                    }
                    Log.Info("[Shrinkinator] Спавн предмета через MultiplayerPool: " + prefabId);
                    return false;
                }
                catch (Exception e)
                {
                    Log.Error("[Shrinkinator] Ошибка в MultiplayerPool.Instantiate", e);
                    return true;
                }
            }
        }
    }

    /// <summary>
    /// Жизненный цикл только у шаблона: Start иначе парентит его в сцену и
    /// уничтожает при смене уровня; Update без Start падает с NRE
    /// (physGrabObject так и остаётся null). Экземпляры идут как обычно.
    /// </summary>
    [HarmonyPatch(typeof(ItemAttributes))]
    internal static class TemplateItemAttributesLifecyclePatch
    {
        private static bool ShouldRunVanilla(ItemAttributes instance)
        {
            return instance == null || !ItemRegistration.IsTemplate(instance.gameObject);
        }

        [HarmonyPatch("Start")]
        [HarmonyPrefix]
        private static bool StartPrefix(ItemAttributes __instance)
        {
            return ShouldRunVanilla(__instance);
        }

        [HarmonyPatch("Update")]
        [HarmonyPrefix]
        private static bool UpdatePrefix(ItemAttributes __instance)
        {
            return ShouldRunVanilla(__instance);
        }
    }

    /// <summary>
    /// Одиночка и админ-меню делают Object.Instantiate(item.prefab.Prefab, pos, rot).
    /// Шаблон неактивен, поэтому клон тоже рождается выключенным — включаем только
    /// наши копии. Постфикс не подменяет Instantiate, чужие объекты не трогает.
    /// </summary>
    [HarmonyPatch(typeof(UnityEngine.Object), nameof(UnityEngine.Object.Instantiate), typeof(UnityEngine.Object), typeof(Vector3), typeof(Quaternion))]
    internal static class ActivateSpawnedTemplateClonePatch
    {
        private static void Postfix(UnityEngine.Object original, UnityEngine.Object __result)
        {
            try
            {
                if (NetworkPrefabResolverPatch.SuppressCloneActivation)
                {
                    return;
                }

                GameObject source = original as GameObject;
                GameObject clone = __result as GameObject;
                if (clone == null || !ItemRegistration.IsTemplate(source))
                {
                    return;
                }

                if (!clone.activeSelf)
                {
                    clone.SetActive(true);
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Shrinkinator] Не удалось активировать заспавненный предмет: " + e.Message);
            }
        }
    }

    /// <summary>
    /// Патчи-«вешалки» shrink-контроллеров на цели (спека 1.4):
    /// ценности — на PhysGrabObject.Awake (тот же GO, что и ValuableObject),
    /// враги — на EnemyRigidbody.Awake, игроки — на PlayerAvatar.Start.
    /// После AddComponent обновляем RPC-кэш PhotonView, чтобы наши [PunRPC] находились.
    /// </summary>
    internal static class AttachPatches
    {
        [HarmonyPatch(typeof(PhysGrabObject), "Awake")]
        internal static class PhysGrabObjectAwakePatch
        {
            private static void Postfix(PhysGrabObject __instance)
            {
                try
                {
                    if (__instance != null && __instance.GetComponent<ValuableObject>() != null)
                    {
                        EnsureValuableController(__instance.gameObject);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[Shrinkinator] Ошибка патча PhysGrabObject.Awake", e);
                }
            }
        }

        [HarmonyPatch(typeof(EnemyRigidbody), "Awake")]
        internal static class EnemyRigidbodyAwakePatch
        {
            private static void Postfix(EnemyRigidbody __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        EnsureEnemyController(__instance.gameObject);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[Shrinkinator] Ошибка патча EnemyRigidbody.Awake", e);
                }
            }
        }

        [HarmonyPatch(typeof(PlayerAvatar), "Start")]
        internal static class PlayerAvatarStartPatch
        {
            private static void Postfix(PlayerAvatar __instance)
            {
                try
                {
                    if (__instance != null)
                    {
                        EnsurePlayerController(__instance.gameObject);
                    }
                }
                catch (Exception e)
                {
                    Log.Error("[Shrinkinator] Ошибка патча PlayerAvatar.Start", e);
                }
            }
        }

        // --- Методы Ensure* используются и патчами, и обработчиком выстрела (запасной путь) ---

        internal static ValuableShrinkController EnsureValuableController(GameObject target)
        {
            return EnsureController<ValuableShrinkController>(target);
        }

        internal static EnemyShrinkController EnsureEnemyController(GameObject target)
        {
            return EnsureController<EnemyShrinkController>(target);
        }

        internal static PlayerShrinkController EnsurePlayerController(GameObject target)
        {
            return EnsureController<PlayerShrinkController>(target);
        }

        private static T EnsureController<T>(GameObject target) where T : Component
        {
            if (target == null)
            {
                return null;
            }

            T controller = target.GetComponent<T>();
            if (controller != null)
            {
                return controller;
            }

            controller = target.AddComponent<T>();

            // Чтобы Photon нашёл наши [PunRPC]-методы на новом компоненте.
            PhotonView view = target.GetComponentInParent<PhotonView>();
            if (view != null)
            {
                view.RefreshRpcMonoBehaviourCache();
            }

            return controller;
        }
    }
}
