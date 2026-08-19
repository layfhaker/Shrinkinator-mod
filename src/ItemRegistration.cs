using System;
using UnityEngine;
using REPOLib.Modules;
using Object = UnityEngine.Object;

namespace Shrinkinator
{
    /// <summary>
    /// Клонирование ванильной пушки и регистрация «Уменьшитель-инатора» через REPOLib.
    ///
    /// Подход (по спеке 1.1): вместо Unity Editor + .repobundle клонируем ванильный
    /// префаб пистолета в рантайме, подменяем Item SO на клон с нашими параметрами
    /// и регистрируем через REPOLib.Modules.Items.RegisterItem.
    ///
    /// Отступление от спеки: Item SO клонируется через Object.Instantiate(sourceItem),
    /// а не CreateInstance + ручное копирование полей — Instantiate копирует ВСЕ поля
    /// (включая будущие, добавленные патчами игры), что надёжнее. Поведение идентично.
    /// </summary>
    internal static class ItemRegistration
    {
        /// <summary>Asset-имя клона префаба (идёт в network prefab id "Items/...").</summary>
        internal const string PrefabName = "Item Shrinkinator";

        /// <summary>Ссылка на наш Item SO — используется патчами для опознания пушки.</summary>
        internal static Item ShrinkinatorItem { get; private set; }

        /// <summary>Сетевой путь префаба в формате Photon ("Items/&lt;имя&gt;").</summary>
        internal static string NetworkPrefabPath => "Items/" + PrefabName;

        /// <summary>Клон-шаблон префаба (он же сетевой prefab "Items/Item Shrinkinator").</summary>
        internal static GameObject Template => _template;

        private static GameObject _template;

        private static bool _registered;

        /// <summary>
        /// Вызывается постфиксом на StatsManager.RunStartStats (как делает сам REPOLib):
        /// к этому моменту ванильные предметы уже загружены в StatsManager.itemDictionary.
        /// Метод идемпотентен — реальная регистрация выполняется один раз.
        /// </summary>
        internal static void TryRegister()
        {
            if (_registered)
            {
                return;
            }

            try
            {
                RegisterInternal();
            }
            catch (Exception e)
            {
                // Не ставим флаг — попробуем снова при следующем RunStartStats.
                Log.Error("[Shrinkinator] Ошибка регистрации предмета", e);
            }
        }

        private static void RegisterInternal()
        {
            // --- Ищем ванильную пушку среди всех предметов ---
            Item vanillaItem = null;
            GameObject vanillaPrefab = null;

            foreach (Item item in Items.AllItems)
            {
                if (item == null || item.itemType != SemiFunc.itemType.gun)
                {
                    continue;
                }

                GameObject prefab = GetPrefabSafe(item);
                if (prefab == null || prefab.GetComponent<ItemGun>() == null)
                {
                    continue;
                }

                // Берём первую пушку с компонентом ItemGun (обычно это пистолет "Item Gun").
                vanillaItem = item;
                vanillaPrefab = prefab;
                break;
            }

            if (vanillaItem == null || vanillaPrefab == null)
            {
                Log.Warning("[Shrinkinator] Ванильная пушка не найдена в StatsManager — регистрация отложена.");
                return;
            }

            Log.Info("[Shrinkinator] Клонируем ванильный предмет \"" + vanillaItem.itemName + "\".");

            // --- Клонируем префаб неактивным, чтобы Awake компонентов не отработал раньше времени ---
            GameObject clone = InstantiateInactive(vanillaPrefab);
            if (clone == null)
            {
                Log.Error("[Shrinkinator] Не удалось клонировать префаб пушки.");
                return;
            }

            clone.name = PrefabName;
            Object.DontDestroyOnLoad(clone);
            _template = clone;

            // --- Клонируем Item SO и настраиваем ---
            Item itemClone = Object.Instantiate(vanillaItem);
            // ВАЖНО: item.name (Unity-имя объекта) — ключ в StatsManager.itemDictionary
            // (REPOLib AddItem использует item.name). Должно быть уникальным.
            itemClone.name = PrefabName;
            itemClone.itemName = ShrinkinatorConfig.ItemName.Value;
            itemClone.itemType = SemiFunc.itemType.gun;
            itemClone.itemVolume = SemiFunc.itemVolume.medium;
            itemClone.maxAmountInShop = 1;
            itemClone.maxAmount = 1;
            // Иконка: emojiIcon — это enum (SemiFunc.emojiIcon), а не строка, поэтому
            // произвольный emoji ("🧪") без своих ассетов не поставить. Берём иконку
            // транквилизатора — отличается от ванильного пистолета (item_gun_handgun).
            itemClone.emojiIcon = SemiFunc.emojiIcon.item_gun_tranq;

            // Цена: клонируем Value-пресет и умножаем (по умолчанию чуть дороже пистолета).
            if (itemClone.value != null)
            {
                Value valueClone = Object.Instantiate(itemClone.value);
                float multiplier = Mathf.Max(0.1f, ShrinkinatorConfig.PriceMultiplier.Value);
                valueClone.valueMin *= multiplier;
                valueClone.valueMax *= multiplier;
                itemClone.value = valueClone;
            }

            // Зелёный цветовой пресет: клонируем ванильный и перекрашиваем.
            if (itemClone.colorPreset != null)
            {
                ColorPresets colorClone = Object.Instantiate(itemClone.colorPreset);
                colorClone.colorMain = new Color(0.25f, 0.9f, 0.35f);
                colorClone.colorLight = new Color(0.55f, 1f, 0.6f);
                colorClone.colorDark = new Color(0.08f, 0.45f, 0.15f);
                itemClone.colorPreset = colorClone;
            }

            // --- Настраиваем ItemGun на клоне ---
            ItemGun gun = clone.GetComponent<ItemGun>();
            if (gun == null)
            {
                Log.Error("[Shrinkinator] На клоне префаба нет ItemGun — регистрация отменена.");
                Object.Destroy(clone);
                return;
            }

            gun.gunRandomSpread = 0f;
            gun.shootCooldown = Mathf.Max(0.1f, ShrinkinatorConfig.ShootCooldown.Value);
            gun.misfirePercentageChange = 0f;
            gun.numberOfBullets = 1;
            // Один выстрел = один ShootRPC. Иначе (дробовик/автомат) StateShooting
            // ещё раз снимает полный бар поверх batteryDrain.
            gun.hasOneShot = true;
            gun.hasBuildUp = false;
            // Снимаем ровно 1 бар батареи за выстрел (баров = Charges).
            gun.batteryDrainFullBar = true;
            gun.batteryDrainFullBars = 1;
            gun.batteryDrain = 100f / Mathf.Max(1, ShrinkinatorConfig.Charges.Value);

            ItemBattery battery = clone.GetComponent<ItemBattery>();
            if (battery != null)
            {
                int bars = Mathf.Max(1, ShrinkinatorConfig.Charges.Value);
                battery.batteryBars = bars;
                battery.batteryLife = 100f;
                battery.batteryLifeInt = bars;
            }

            // Клонируем префаб пули, чтобы перекрасить луч в зелёный и взять материал для тумана.
            RecolorBullet(gun);

            // --- AimAssist: выравниваем ось дула ДО построения модели ---
            // Модель строит ствол по gunMuzzle.forward; если выровнять дуло после,
            // луч и визуальный ствол рассинхронятся. Выравниваем первым — тогда
            // модель строится уже по выровненной оси. Порядок обязательный:
            // AlignMuzzleForward → ShrinkinatorModelBuilder.Apply → GunHandlingTuner.Apply.
            GunHandlingTuner.AlignMuzzleForward(clone, gun);

            // --- Кастомная процедурная модель (до регистрации network prefab —
            // тогда в мультиплеере PUN воспроизведёт её у всех клиентов сам) ---
            Vector3 gripLocalPosition = Vector3.zero;
            bool gripKnown = false;
            if (ShrinkinatorConfig.UseCustomModel.Value)
            {
                gripKnown = ShrinkinatorModelBuilder.Apply(clone, gun, out gripLocalPosition);
            }

            // --- Балансировка хвата (grab points, поля из конфига) — только наш клон! ---
            // ДОЛЖНО идти после построения модели (нужна позиция рукояти) и до
            // активации шаблона ниже — тогда PhysGrabObject.Awake шаблона и всех
            // заспавненных экземпляров подхватит точки "Center of Mass"/
            // "Force Grab Point" из префаба.
            GunHandlingTuner.Apply(clone, gun, gripKnown, gripLocalPosition);

            // --- Подставляем наш Item SO в ItemAttributes клона ---
            ItemAttributes attributes = clone.GetComponent<ItemAttributes>();
            if (attributes == null)
            {
                Log.Error("[Shrinkinator] На клоне префаба нет ItemAttributes — регистрация отменена.");
                Object.Destroy(clone);
                return;
            }
            attributes.item = itemClone;

            // --- Регистрация через REPOLib (network prefab + StatsManager -> магазин) ---
            Items.RegisterItem(attributes);

            // Шаблон обязан остаться НЕАКТИВНЫМ. Если его включить, на DontDestroyOnLoad
            // крутятся ItemAttributes.Update (Start мы пропускаем → NRE) и живой PhotonView,
            // из-за чего Photon рвёт комнату при загрузке уровня.
            // Экземпляры активирует NetworkPrefabResolverPatch после Instantiate
            // (PUN сам включает объекты из пула; одиночка/админка — наш postfix).
            clone.SetActive(false);

            // PrefabRef сначала ищет runtime-префабы в игровых кэшах. Заполняем их
            // после активации шаблона, чтобы и сетевой, и одиночный спавн не зависели
            // от Resources.Load.
            NetworkPrefabResolverPatch.CacheRuntimePrefab();

            ShrinkinatorItem = itemClone;
            _registered = true;
            Log.Info("[Shrinkinator] Предмет \"" + itemClone.itemName + "\" зарегистрирован (prefab \"" + PrefabName + "\").");
        }

        /// <summary>
        /// Клонирует префаб пули и перекрашивает LineRenderer луча и дым в зелёный.
        /// Заодно отдаёт материал дыма в MistVfx (гарантированно валидный партикл-материал).
        /// </summary>
        private static void RecolorBullet(ItemGun gun)
        {
            try
            {
                if (gun.bulletPrefab == null)
                {
                    return;
                }

                GameObject bulletClone = InstantiateInactive(gun.bulletPrefab);
                if (bulletClone == null)
                {
                    return;
                }

                bulletClone.name = "Item Shrinkinator Bullet";
                Object.DontDestroyOnLoad(bulletClone);

                // Ванильная пуля несёт HurtCollider и убивает/отбрасывает мобов.
                // Для уменьшителя оставляем только луч и дым.
                foreach (HurtCollider hurt in bulletClone.GetComponentsInChildren<HurtCollider>(true))
                {
                    hurt.enabled = false;
                    hurt.gameObject.SetActive(false);
                }

                ItemGunBullet bullet = bulletClone.GetComponent<ItemGunBullet>();
                Color green = new Color(0.35f, 1f, 0.45f);
                if (bullet != null)
                {
                    if (bullet.shootLine != null)
                    {
                        bullet.shootLine.startColor = green;
                        bullet.shootLine.endColor = green;
                    }

                    ParticleSystem smoke = bullet.particleSmoke != null ? bullet.particleSmoke : bullet.particleImpact;
                    if (smoke != null)
                    {
                        ParticleSystem.MainModule main = smoke.main;
                        main.startColor = green;
                        // sharedMaterial живёт на ParticleSystemRenderer, а не на ParticleSystem.
                        ParticleSystemRenderer smokeRenderer = smoke.GetComponent<ParticleSystemRenderer>();
                        if (smokeRenderer != null)
                        {
                            MistVfx.Init(smokeRenderer.sharedMaterial);
                        }
                    }
                }

                gun.bulletPrefab = bulletClone;
            }
            catch (Exception e)
            {
                // Перекраска — косметика, её сбой не должен ломать регистрацию.
                Log.Warning("[Shrinkinator] Не удалось перекрасить пулю: " + e.Message);
            }
        }

        /// <summary>
        /// Аккуратно достаёт GameObject из Item.prefab (PrefabRef) с защитой от исключений.
        /// </summary>
        private static GameObject GetPrefabSafe(Item item)
        {
            try
            {
                if (item.prefab == null || !item.prefab.IsValid())
                {
                    return null;
                }
                return item.prefab.Prefab;
            }
            catch (Exception)
            {
                return null;
            }
        }

        /// <summary>
        /// Instantiate с гарантией неактивного клона: временно гасим исходный префаб-ассет,
        /// чтобы на клоне не вызвались Awake/OnEnable до окончания настройки.
        /// </summary>
        private static GameObject InstantiateInactive(GameObject prefab)
        {
            bool wasActive = prefab.activeSelf;
            try
            {
                if (wasActive)
                {
                    prefab.SetActive(false);
                }
                return Object.Instantiate(prefab);
            }
            finally
            {
                if (wasActive)
                {
                    prefab.SetActive(true);
                }
            }
        }

        /// <summary>
        /// Опознание нашей пушки (спека 1.2): по ссылке на Item SO, с запасным вариантом по имени.
        /// </summary>
        internal static bool IsOurGun(ItemGun gun)
        {
            if (gun == null)
            {
                return false;
            }

            ItemAttributes attributes = gun.GetComponent<ItemAttributes>();
            if (attributes == null || attributes.item == null)
            {
                return false;
            }

            if (ShrinkinatorItem != null && attributes.item == ShrinkinatorItem)
            {
                return true;
            }

            // Запасной вариант: совпадение asset-имени SO (на случай пересоздания ссылок).
            return attributes.item.name == PrefabName;
        }

        /// <summary>Проверяет, является ли объект постоянным шаблоном предмета.</summary>
        internal static bool IsTemplate(GameObject gameObject)
        {
            return gameObject != null && _template != null && gameObject == _template;
        }

    }
}
