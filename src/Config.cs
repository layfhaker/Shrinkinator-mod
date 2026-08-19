using BepInEx.Configuration;

namespace Shrinkinator
{
    /// <summary>
    /// Конфигурация мода (BepInEx/config/com.kimi.shrinkinator.cfg).
    /// Все значения читаются один раз при старте; многие применяются в момент выстрела,
    /// поэтому часть настроек можно менять «на горячую» через Configuration Manager.
    /// </summary>
    internal static class ShrinkinatorConfig
    {
        internal static ConfigEntry<float> ScaleFactor;
        internal static ConfigEntry<float> DurationSeconds;
        internal static ConfigEntry<float> CloudRadius;
        internal static ConfigEntry<float> MistRange;
        internal static ConfigEntry<int> Charges;
        internal static ConfigEntry<bool> ValueScalePrice;
        internal static ConfigEntry<string> ItemName;
        internal static ConfigEntry<float> PriceMultiplier;
        internal static ConfigEntry<float> ShootCooldown;
        internal static ConfigEntry<bool> UseCustomModel;

        // --- Хват / Handling ---
        internal static ConfigEntry<float> AimVerticalOffset;
        internal static ConfigEntry<float> GrabVerticalOffset;
        internal static ConfigEntry<bool> CenterOfMassInGrip;
        internal static ConfigEntry<bool> AimAssist;
        internal static ConfigEntry<float> GrabStrengthMultiplier;
        internal static ConfigEntry<float> TorqueMultiplier;

        internal static void Init(ConfigFile config)
        {
            ScaleFactor = config.Bind(
                "Общее", "ScaleFactor", 0.35f,
                "Множитель размера при уменьшении (0.35 = цель становится в ~3 раза меньше). Масса уменьшается пропорционально кубу множителя. Цена ценностей не меняется.");

            DurationSeconds = config.Bind(
                "Общее", "DurationSeconds", 20f,
                "Длительность уменьшения врагов и игроков в секундах. Ценности уменьшаются навсегда.");

            CloudRadius = config.Bind(
                "Туман", "CloudRadius", 2.5f,
                "Радиус облака тумана в метрах. Всё, что попало в облако, уменьшается.");

            MistRange = config.Bind(
                "Туман", "MistRange", 12f,
                "Дальность распыления тумана в метрах (если луч ни во что не попал, облако появляется на этом расстоянии).");

            Charges = config.Bind(
                "Пушка", "Charges", 6,
                "Количество выстрелов от полной батареи. Реализовано через ванильный расход батареи (100 / Charges за выстрел).");

            ValueScalePrice = config.Bind(
                "Ценности", "ValueScalePrice", false,
                "Уменьшать ли стоимость ценности вместе с размером. По умолчанию false: цена остаётся прежней, уменьшается только размер и масса.");

            ItemName = config.Bind(
                "Пушка", "ItemName", "Уменьшитель-инатор",
                "Отображаемое название предмета в магазине.");

            PriceMultiplier = config.Bind(
                "Пушка", "PriceMultiplier", 1.5f,
                "Множитель цены в магазине относительно ванильного пистолета.");

            ShootCooldown = config.Bind(
                "Пушка", "ShootCooldown", 0.8f,
                "Минимальная пауза между выстрелами в секундах.");

            UseCustomModel = config.Bind(
                "Пушка", "UseCustomModel", true,
                "Заменять визуал пушки кастомной процедурной моделью «shrink ray gun» (собирается в коде из примитивов, без ассетов). false = ванильный внешний вид пистолета. Применяется при регистрации предмета — требует перезапуска.");

            AimVerticalOffset = config.Bind(
                "Хват", "AimVerticalOffset", 0f,
                "Вертикальный угол ствола относительно направления взгляда в градусах (поле ItemGun.aimVerticalOffset). Ванильный пистолет: -10 (бьёт ниже прицела). 0 = стреляет ровно туда, куда смотрит камера.");

            GrabVerticalOffset = config.Bind(
                "Хват", "GrabVerticalOffset", -0.15f,
                "Вертикальное смещение точки удержания пушки относительно линии взгляда в метрах (поле ItemGun.grabVerticalOffset). Ванильный пистолет: -0.2. Отрицательное = ниже центра экрана, не загораживает обзор.");

            CenterOfMassInGrip = config.Bind(
                "Хват", "CenterOfMassInGrip", true,
                "Переносить центр масс (rb.centerOfMass, точка «Center of Mass») в рукоять. Убирает «маятниковое» болтание: сила захвата приложена к точке хвата, и если она совпадает с центром масс — паразитного крутящего момента нет. Работает только с кастомной моделью (позиция рукояти известна).");

            AimAssist = config.Bind(
                "Хват", "AimAssist", true,
                "Выравнивать ось дула (gunMuzzle.forward) по оси transform.forward корня предмета — именно её физика удержания поворачивает по камере. Гарантирует, что луч идёт строго по направлению взгляда.");

            GrabStrengthMultiplier = config.Bind(
                "Хват", "GrabStrengthMultiplier", 1f,
                "Множитель силы пружины удержания (поле ItemGun.grabStrengthMultiplier, ваниль = 1). Больше = жёстче следует за рукой, но может дёргаться.");

            TorqueMultiplier = config.Bind(
                "Хват", "TorqueMultiplier", 1f,
                "Множитель крутящего момента доворота к камере (поле ItemGun.torqueMultiplier, ваниль = 1). Больше = быстрее доворачивается за взглядом, но возможна «рыскливость».");
        }
    }
}
