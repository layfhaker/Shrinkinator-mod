using System;
using UnityEngine;

namespace Shrinkinator
{
    /// <summary>
    /// Балансировка хвата и прицеливания клона нашей пушки (ванильные пушки не трогаем).
    ///
    /// Механика удержания (по декомпилу ItemGun / PhysGrabObject / PhysGrabber):
    ///
    /// 1. ПРИЦЕЛИВАНИЕ. ItemGun.UpdateMaster (хост) каждый кадр удержания вызывает
    ///    PhysGrabObject.TurnXYZ(Quaternion.Euler(aimVerticalOffset, 0, 0), identity, identity) —
    ///    это задаёт cameraRelativeGrabbedForward/Up на PhysGrabber'е держащего, а
    ///    PhysGrabObject.FixedUpdate крутящим моментом доворачивает transform.forward/up
    ///    предмета к осям камеры (с наклоном aimVerticalOffset по pitch). То есть
    ///    ванильная пушка УМЕЕТ смотреть по камере, но с ванильным aimVerticalOffset = -10°
    ///    бьёт на 10° ниже прицела. Ставим 0 — ствол строго параллелен взгляду.
    ///    Raycast выстрела идёт из gunMuzzle.position вдоль gunMuzzle.forward
    ///    (ItemGun.ShootRPC), поэтому дополнительно выравниваем ось дула по
    ///    transform.forward корня (AimAssist) — именно её держит физика.
    ///
    /// 2. ТОЧКА ХВАТА. Если у предмета есть дочерний трансформ "Force Grab Point"
    ///    (PhysGrabObject.Awake: base.transform.Find("Force Grab Point")), PhysGrabber
    ///    при захвате за ЛЮБУЮ точку коллайдера прикрепляется именно к этой точке
    ///    (PhysGrabber: vector = forceGrabPoint.position, localGrabPosition от неё).
    ///    Так ваниль делает «инструментальный» хват. Переставляем её в нашу рукоять.
    ///
    /// 3. ЦЕНТР МАСС / БОЛТАНИЕ. PhysGrabObject.Awake читает дочерний трансформ
    ///    "Center of Mass" и пишет его localPosition в rb.centerOfMass. Сила захвата
    ///    прикладывается к точке хвата (FixedUpdate: rb.AddForceAtPosition(сила,
    ///    physGrabPoint.position)); если точка приложения силы совпадает с центром
    ///    масс — крутящий момент от пружины нулевой, «маятникового» болтания нет.
    ///    Ставим центр масс в рукоять (туда же, где точка хвата).
    ///
    /// 4. ПОЗИЦИЯ УДЕРЖАНИЯ. grabVerticalOffset (ItemGun.UpdateMaster →
    ///    OverrideGrabVerticalPosition) смещает цель пружины по вертикали камеры;
    ///    distanceKeep (0.8 м) задаёт дистанцию удержания — оба поля ванильные,
    ///    distanceKeep не меняем.
    ///
    /// Apply вызывается из ItemRegistration ПОСЛЕ построения кастомной модели
    /// (нужна позиция рукояти) и ДО активации клона-шаблона — тогда
    /// PhysGrabObject.Awake шаблона и всех будущих заспавненных экземпляров сам
    /// подхватит "Center of Mass"/"Force Grab Point" из префаба. Выравнивание
    /// оси дула (AlignMuzzleForward) идёт отдельно и РАНЬШЕ построения модели —
    /// чтобы модель строилась уже по выровненной оси.
    /// </summary>
    internal static class GunHandlingTuner
    {
        /// <summary>Имя дочернего трансформa — точки принудительного хвата (ванильная механика).</summary>
        private const string ForceGrabPointName = "Force Grab Point";

        /// <summary>Имя дочернего трансформa — центра масс (читается PhysGrabObject.Awake).</summary>
        private const string CenterOfMassName = "Center of Mass";

        /// <summary>
        /// Применяет настройку к клону. gripKnown/gripLocalPosition — позиция рукояти
        /// в локальном пространстве корня клона (известна только при построенной
        /// кастомной модели). Любой сбой логируется и не прерывает регистрацию.
        /// </summary>
        internal static void Apply(GameObject clone, ItemGun gun, bool gripKnown, Vector3 gripLocalPosition)
        {
            if (clone == null || gun == null)
            {
                return;
            }

            try
            {
                ApplyInternal(clone, gun, gripKnown, gripLocalPosition);
            }
            catch (Exception e)
            {
                // Хват — настройка комфорта: сбой не должен ломать предмет.
                Log.Warning("[Shrinkinator] Не удалось настроить хват пушки: " + e);
            }
        }

        private static void ApplyInternal(GameObject clone, ItemGun gun, bool gripKnown, Vector3 gripLocalPosition)
        {
            // --- 1. Прицеливание и силы удержания (ванильные поля ItemGun) ---
            gun.aimVerticalOffset = ShrinkinatorConfig.AimVerticalOffset.Value;
            gun.grabVerticalOffset = ShrinkinatorConfig.GrabVerticalOffset.Value;
            gun.grabStrengthMultiplier = Mathf.Max(0.1f, ShrinkinatorConfig.GrabStrengthMultiplier.Value);
            gun.torqueMultiplier = Mathf.Max(0.1f, ShrinkinatorConfig.TorqueMultiplier.Value);

            // --- 2. Точка хвата за рукоять + центр масс в рукояти ---
            // Позиция рукояти известна только у кастомной модели; при ванильном
            // визуале оставляем ванильные точки префаба как есть.
            if (!gripKnown)
            {
                return;
            }

            Transform forceGrabPoint = EnsurePoint(clone.transform, ForceGrabPointName, gripLocalPosition);
            if (forceGrabPoint != null)
            {
                Log.Info("[Shrinkinator] Точка хвата \"" + ForceGrabPointName + "\" перенесена в рукоять.");
            }

            if (ShrinkinatorConfig.CenterOfMassInGrip.Value)
            {
                Transform centerOfMass = EnsurePoint(clone.transform, CenterOfMassName, gripLocalPosition);
                if (centerOfMass != null)
                {
                    Log.Info("[Shrinkinator] Центр масс \"" + CenterOfMassName + "\" перенесён в рукоять.");
                }

                // Продублируем напрямую в Rigidbody шаблона — страховка на случай,
                // если порядок настройки/активации когда-нибудь изменится и
                // PhysGrabObject.Awake отработает до этой точки (сейчас это
                // невозможно: мы идём до активации клона, а поле PhysGrabObject.rb
                // присваивается именно в Awake — поэтому берём компонент через
                // GetComponent). Конфликта нет: ванильный Awake потом всё равно
                // перезапишет centerOfMass из дочерней точки "Center of Mass"
                // тем же значением. centerOfMass задаётся в локальных координатах
                // Rigidbody (он на корне клона).
                Rigidbody rigidbody = clone.GetComponent<Rigidbody>();
                if (rigidbody != null)
                {
                    rigidbody.centerOfMass = gripLocalPosition;
                }
            }
        }

        /// <summary>
        /// AimAssist: выравнивает ось дула по оси, которую физика держит по камере.
        /// TurnXYZ доворачивает transform.forward/up КОРНЯ к осям камеры; если
        /// gunMuzzle.forward отклонён от transform.forward (особенности ванильного
        /// префаба), луч уйдёт с постоянным угловым смещением — устраняем его.
        ///
        /// ВАЖНО: вызывается из ItemRegistration ДО построения кастомной модели —
        /// тогда ShrinkinatorModelBuilder строит ствол уже по выровненной оси
        /// (иначе модель строилась бы по исходному gunMuzzle.forward, а поворот
        /// дула после построения рассинхронил бы луч с визуальным стволом).
        /// Сбой логируется и не прерывает регистрацию.
        /// </summary>
        public static void AlignMuzzleForward(GameObject clone, ItemGun gun)
        {
            if (clone == null || gun == null)
            {
                return;
            }

            try
            {
                if (ShrinkinatorConfig.AimAssist.Value && gun.gunMuzzle != null)
                {
                    float angle = Vector3.Angle(gun.gunMuzzle.forward, clone.transform.forward);
                    if (angle > 0.5f)
                    {
                        Vector3 forward = clone.transform.forward;
                        Vector3 up = gun.gunMuzzle.up - forward * Vector3.Dot(gun.gunMuzzle.up, forward);
                        if (up.sqrMagnitude < 1e-6f)
                        {
                            up = clone.transform.up - forward * Vector3.Dot(clone.transform.up, forward);
                        }
                        if (up.sqrMagnitude < 1e-6f)
                        {
                            up = Vector3.up;
                        }
                        gun.gunMuzzle.rotation = Quaternion.LookRotation(forward, up.normalized);
                        Log.Info("[Shrinkinator] AimAssist: ось дула выровнена по корпусу (было отклонение "
                            + angle.ToString("0.0") + "°).");
                    }
                }
            }
            catch (Exception e)
            {
                // Выравнивание — настройка комфорта: сбой не должен ломать предмет.
                Log.Warning("[Shrinkinator] Не удалось выровнять ось дула: " + e);
            }
        }

        /// <summary>
        /// Находит (или создаёт) ДОЧЕРНИЙ трансформ с заданным именем — именно так
        /// их ищет игра (base.transform.Find работает только по прямым детям) —
        /// и переставляет его в указанную локальную позицию.
        /// </summary>
        private static Transform EnsurePoint(Transform root, string name, Vector3 localPosition)
        {
            Transform point = root.Find(name);
            if (point == null)
            {
                var pointObject = new GameObject(name);
                pointObject.layer = root.gameObject.layer;
                point = pointObject.transform;
                point.SetParent(root, false);
            }

            point.localPosition = localPosition;
            point.localRotation = Quaternion.identity;
            point.localScale = Vector3.one;
            return point;
        }
    }
}
