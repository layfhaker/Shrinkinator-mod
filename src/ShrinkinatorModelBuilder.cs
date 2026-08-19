using System;
using System.Collections.Generic;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Shrinkinator
{
    /// <summary>
    /// Процедурная кастомная модель «shrink ray gun» — собирается в коде из
    /// Unity-примитивов (Cube/Cylinder/Sphere/Capsule), без Unity Editor и внешних
    /// ассетов. Ретро sci-fi рей-ган: тёмный металлический корпус, бак с зелёной
    /// «жидкостью», конусный ствол с кольцами, светящийся наконечник, рукоять
    /// и боковые плавники (стилизация под «-инатор» Доктора Дуфеншмирца).
    ///
    /// Модель строится ОДИН РАЗ на префабе-клоне ДО регистрации network prefab
    /// в REPOLib — поэтому в мультиплеере PUN воспроизводит её у всех клиентов
    /// автоматически (это просто дети префаба), отдельная сетевая синхронизация
    /// не нужна.
    ///
    /// Всё построение обёрнуто в try/catch: если ванильная иерархия изменилась,
    /// мод не падает — просто остаётся ванильный визуал.
    /// </summary>
    internal static class ShrinkinatorModelBuilder
    {
        /// <summary>Имя корневого объекта кастомной модели (ребёнок клона пушки).</summary>
        internal const string VisualRootName = "ShrinkinatorVisual";

        /// <summary>Базовая длина модели в метрах — масштабируется под ванильные bounds.</summary>
        private const float BaseLength = 0.40f;

        /// <summary>
        /// Локальная позиция рукояти в базовых единицах модели (умножается на scale,
        /// затем переносится в локальное пространство корня модели). Используется и
        /// геометрией (деталь "Grip"), и GunHandlingTuner'ом (точка хвата/центр масс).
        /// </summary>
        internal static readonly Vector3 GripOffsetBase = new Vector3(0f, -0.058f, 0.045f);

        // --- Кэшированные материалы: создаются один раз, не плодим при каждом спавне ---
        private static Material _metalMaterial;
        private static Material _greenMaterial;

        private static readonly Color MetalColor = new Color(0.16f, 0.17f, 0.19f);
        private static readonly Color GreenColor = new Color(0.2f, 1.0f, 0.35f);

        // Фрагменты имён объектов, чьи renderer'ы НЕ отключаем:
        // muzzle flash / пуля / прочие логические ветки ванильной пушки.
        private static readonly string[] ExcludedNameFragments =
        {
            "muzzle", "flash", "bullet", "shell", "casing", "sound", "laser", "beam"
        };

        /// <summary>
        /// Точка входа: прячет ванильный визуал клона и строит кастомную модель.
        /// Вызывается из ItemRegistration до Items.RegisterItem (основной поток).
        /// Любой сбой логируется и не прерывает регистрацию предмета.
        /// Возвращает true, если модель построена; тогда gripLocalPosition —
        /// позиция рукояти в локальном пространстве корня клона (для GunHandlingTuner).
        /// </summary>
        internal static bool Apply(GameObject clone, ItemGun gun, out Vector3 gripLocalPosition)
        {
            gripLocalPosition = Vector3.zero;
            if (clone == null || gun == null)
            {
                return false;
            }

            List<Renderer> visualRenderers = null;
            GameObject visualRoot = null;
            try
            {
                return ApplyInternal(clone, gun, out gripLocalPosition, out visualRenderers, out visualRoot);
            }
            catch (Exception e)
            {
                // Модель — чисто косметика: при сбое откатываемся к ванильному
                // визуалу — ре-энейблим уже отключённые renderer'ы и удаляем
                // недостроенный корень модели (если успели создать).
                RollbackToVanilla(visualRenderers, visualRoot);
                Log.Warning("[Shrinkinator] Не удалось построить кастомную модель, откат к ванильному визуалу: " + e);
                return false;
            }
        }

        /// <summary>
        /// Откат после сбоя: включает обратно все renderer'ы, которые мы успели
        /// отключить, и уничтожает недостроенный корень кастомной модели.
        /// </summary>
        private static void RollbackToVanilla(List<Renderer> visualRenderers, GameObject visualRoot)
        {
            if (visualRoot != null)
            {
                Object.Destroy(visualRoot);
            }

            if (visualRenderers == null)
            {
                return;
            }

            foreach (Renderer renderer in visualRenderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = true;
                }
            }
        }

        private static bool ApplyInternal(GameObject clone, ItemGun gun, out Vector3 gripLocalPosition,
            out List<Renderer> visualRenderers, out GameObject visualRoot)
        {
            gripLocalPosition = Vector3.zero;
            visualRenderers = null;
            visualRoot = null;

            if (gun.gunMuzzle == null)
            {
                Log.Warning("[Shrinkinator] У клона нет gunMuzzle — кастомную модель не строим.");
                return false;
            }

            // --- 1. Находим визуальные renderer'ы самого предмета ---
            visualRenderers = CollectVisualRenderers(clone, gun);
            if (visualRenderers.Count == 0)
            {
                Log.Warning("[Shrinkinator] На клоне не найдено визуальных MeshRenderer/SkinnedMeshRenderer — модель не строим (иерархия изменилась?).");
                return false;
            }

            // --- 2. Замеряем bounds ванильного визуала ДО отключения renderer'ов ---
            // Ось ствола = gunMuzzle.forward (в локальном пространстве корня клона).
            Vector3 muzzlePosLocal = clone.transform.InverseTransformPoint(gun.gunMuzzle.position);
            Vector3 muzzleDirLocal = clone.transform.InverseTransformDirection(gun.gunMuzzle.forward);
            if (muzzleDirLocal.sqrMagnitude < 1e-8f)
            {
                muzzleDirLocal = Vector3.forward;
            }
            muzzleDirLocal.Normalize();

            // «Вверх» модели берём из gunMuzzle.up — так рукоять совпадёт с
            // ориентацией ванильной пушки. Fallback — мировой up.
            Vector3 upLocal = clone.transform.InverseTransformDirection(gun.gunMuzzle.up);
            if (upLocal.sqrMagnitude < 1e-8f || Vector3.Dot(upLocal.normalized, muzzleDirLocal) > 0.95f)
            {
                upLocal = Vector3.up;
            }
            upLocal = (upLocal - muzzleDirLocal * Vector3.Dot(upLocal, muzzleDirLocal)).normalized;

            float length = MeasureLengthAlongAxis(clone.transform, visualRenderers, muzzleDirLocal);
            float scale = length / BaseLength;

            // Корень модели: ось ствола проходит через ванильный gunMuzzle,
            // задний срез корпуса — на (length) позади дула вдоль оси.
            Vector3 rootPosLocal = muzzlePosLocal - muzzleDirLocal * length;
            Quaternion rootRotLocal = Quaternion.LookRotation(muzzleDirLocal, upLocal);

            // Позиция рукояти в локальном пространстве корня КЛОНА — отдаём
            // наружу для GunHandlingTuner (точка хвата «Force Grab Point» и
            // центр масс «Center of Mass» ставятся в рукоять).
            gripLocalPosition = rootPosLocal + rootRotLocal * (GripOffsetBase * scale);

            // --- 3. Отключаем ванильный визуал (только renderer.enabled, объекты не трогаем) ---
            foreach (Renderer renderer in visualRenderers)
            {
                if (renderer != null)
                {
                    renderer.enabled = false;
                }
            }

            // --- 4. Материалы (один раз на весь процесс) ---
            EnsureMaterials(visualRenderers);

            // --- 5. Строим модель ---
            GameObject root = new GameObject(VisualRootName);
            visualRoot = root;
            root.layer = clone.layer;
            Transform rootTransform = root.transform;
            rootTransform.SetParent(clone.transform, false);
            rootTransform.localPosition = rootPosLocal;
            rootTransform.localRotation = rootRotLocal;
            rootTransform.localScale = Vector3.one;

            BuildModel(rootTransform, scale);

            // --- 6. Переставляем gunMuzzle на кончик нового ствола ---
            Vector3 tipLocal = rootPosLocal + muzzleDirLocal * length;
            gun.gunMuzzle.position = clone.transform.TransformPoint(tipLocal);
            gun.gunMuzzle.rotation = Quaternion.LookRotation(
                clone.transform.TransformDirection(muzzleDirLocal),
                clone.transform.TransformDirection(upLocal));

            // gunTrigger — на позицию нашего спускового кубика (косметика).
            if (gun.gunTrigger != null)
            {
                Vector3 triggerLocal = rootPosLocal + rootRotLocal * (new Vector3(0f, -0.030f, 0.078f) * scale);
                gun.gunTrigger.position = clone.transform.TransformPoint(triggerLocal);
                gun.gunTrigger.rotation = Quaternion.LookRotation(
                    clone.transform.TransformDirection(muzzleDirLocal),
                    clone.transform.TransformDirection(upLocal));
            }

            Log.Info("[Shrinkinator] Кастомная модель \"" + VisualRootName + "\" построена (длина "
                + length.ToString("0.00") + " м, скрыто renderer'ов: " + visualRenderers.Count + ").");
            return true;
        }

        /// <summary>
        /// Собирает renderer'ы визуала предмета: все Renderer в детях клона,
        /// но только MeshRenderer и SkinnedMeshRenderer (ParticleSystemRenderer,
        /// LineRenderer, TrailRenderer и прочие пропускаем), кроме веток
        /// muzzleFlashPrefab/bulletPrefab и объектов со «служебными» именами.
        /// </summary>
        private static List<Renderer> CollectVisualRenderers(GameObject clone, ItemGun gun)
        {
            var result = new List<Renderer>();
            Renderer[] all = clone.GetComponentsInChildren<Renderer>(true);
            foreach (Renderer renderer in all)
            {
                if (renderer == null)
                {
                    continue;
                }

                // Отключаем только «мешевые» renderer'ы: частицы, линии и трейлы
                // остаются как есть (эффекты/лучи пушки не трогаем).
                if (!(renderer is MeshRenderer) && !(renderer is SkinnedMeshRenderer))
                {
                    continue;
                }

                if (IsExcludedBranch(renderer.transform, gun))
                {
                    continue;
                }

                result.Add(renderer);
            }
            return result;
        }

        /// <summary>
        /// Проверяет, что renderer НЕ лежит под muzzleFlashPrefab/bulletPrefab
        /// и ни один предок не носит «служебное» имя (flash/bullet/sound/...).
        /// </summary>
        private static bool IsExcludedBranch(Transform transform, ItemGun gun)
        {
            for (Transform current = transform; current != null; current = current.parent)
            {
                if (gun.muzzleFlashPrefab != null && current.gameObject == gun.muzzleFlashPrefab)
                {
                    return true;
                }
                if (gun.bulletPrefab != null && current.gameObject == gun.bulletPrefab)
                {
                    return true;
                }

                string nameLower = current.name.ToLowerInvariant();
                foreach (string fragment in ExcludedNameFragments)
                {
                    if (nameLower.Contains(fragment))
                    {
                        return true;
                    }
                }
            }
            return false;
        }

        /// <summary>
        /// Замеряет протяжённость ванильного визуала вдоль оси ствола (по углам
        /// мировых bounds всех renderer'ов, в локальном пространстве клона).
        /// Результат зажат в [0.30, 0.50] м; при сбое — BaseLength.
        /// </summary>
        private static float MeasureLengthAlongAxis(Transform root, List<Renderer> renderers, Vector3 axisLocal)
        {
            float min = float.MaxValue;
            float max = float.MinValue;
            int corners = 0;

            foreach (Renderer renderer in renderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                Bounds bounds = renderer.bounds;
                Vector3 center = bounds.center;
                Vector3 extents = bounds.extents;
                for (int i = 0; i < 8; i++)
                {
                    Vector3 cornerWorld = center + Vector3.Scale(extents, new Vector3(
                        (i & 1) == 0 ? -1f : 1f,
                        (i & 2) == 0 ? -1f : 1f,
                        (i & 4) == 0 ? -1f : 1f));
                    float projection = Vector3.Dot(root.InverseTransformPoint(cornerWorld), axisLocal);
                    min = Mathf.Min(min, projection);
                    max = Mathf.Max(max, projection);
                    corners++;
                }
            }

            if (corners == 0 || max - min < 0.05f)
            {
                return BaseLength;
            }

            return Mathf.Clamp(max - min, 0.30f, 0.50f);
        }

        /// <summary>
        /// Создаёт (один раз) два материала: тёмный металл и светящийся зелёный.
        /// Шейдер подбирается FindGameShader'ом: probe-шейдер со встроенного
        /// примитива → шейдер с материалов клона → встроенный Standard → любой.
        /// </summary>
        private static void EnsureMaterials(List<Renderer> vanillaRenderers)
        {
            if (_metalMaterial != null && _greenMaterial != null)
            {
                return;
            }

            Shader shader = FindGameShader(vanillaRenderers);

            _metalMaterial = new Material(shader) { name = "Shrinkinator Dark Metal" };
            SetColorIfSupported(_metalMaterial, MetalColor);
            SetFloatIfSupported(_metalMaterial, "_Metallic", 0.8f);
            SetFloatIfSupported(_metalMaterial, "_Glossiness", 0.6f);

            _greenMaterial = new Material(shader) { name = "Shrinkinator Glow Green" };
            SetColorIfSupported(_greenMaterial, GreenColor);
            SetFloatIfSupported(_greenMaterial, "_Metallic", 0.4f);
            SetFloatIfSupported(_greenMaterial, "_Glossiness", 0.7f);
            if (_greenMaterial.HasProperty("_EmissionColor"))
            {
                _greenMaterial.EnableKeyword("_EMISSION");
                _greenMaterial.SetColor("_EmissionColor", GreenColor * 1.5f);
            }
        }

        /// <summary>
        /// Подбирает шейдер по приоритету:
        /// 1) probe-шейдер со встроенного примитива Unity (CreatePrimitive) —
        ///    гарантированно builtin и корректно освещается;
        /// 2) шейдер с материалов ванильного клона, поддерживающий _Color и
        ///    _Glossiness («стандартоподобный»);
        /// 3) встроенный Standard;
        /// 4) любой первый попавшийся шейдер из игры (fallbackAny).
        /// </summary>
        private static Shader FindGameShader(List<Renderer> vanillaRenderers)
        {
            // 1. Probe-шейдер: берём со свежесозданного примитива Unity.
            Shader probeShader = GetProbeShader();
            if (probeShader != null)
            {
                return probeShader;
            }

            // 2. Ванильный «стандартоподобный» шейдер с материалов клона.
            Shader fallbackAny = null;
            foreach (Renderer renderer in vanillaRenderers)
            {
                if (renderer == null)
                {
                    continue;
                }

                foreach (Material material in renderer.sharedMaterials)
                {
                    if (material == null || material.shader == null)
                    {
                        continue;
                    }

                    if (fallbackAny == null)
                    {
                        fallbackAny = material.shader;
                    }

                    if (material.HasProperty("_Color") && material.HasProperty("_Glossiness"))
                    {
                        return material.shader;
                    }
                }
            }

            // 3. Встроенный Standard.
            Shader standard = Shader.Find("Standard");
            if (standard != null)
            {
                return standard;
            }

            // 4. Любой первый попавшийся шейдер из игры.
            return fallbackAny;
        }

        /// <summary>
        /// Создаёт временный примитив и возвращает шейдер его материала
        /// (встроенный, всегда корректно освещается). При сбое — null.
        /// </summary>
        private static Shader GetProbeShader()
        {
            GameObject probe = GameObject.CreatePrimitive(PrimitiveType.Cube);
            Shader probeShader = null;
            MeshRenderer renderer = probe.GetComponent<MeshRenderer>();
            if (renderer != null && renderer.sharedMaterial != null)
            {
                probeShader = renderer.sharedMaterial.shader;
            }
            Object.Destroy(probe);
            return probeShader;
        }

        private static void SetColorIfSupported(Material material, Color color)
        {
            if (material.HasProperty("_Color"))
            {
                material.color = color;
            }
        }

        private static void SetFloatIfSupported(Material material, string property, float value)
        {
            if (material.HasProperty(property))
            {
                material.SetFloat(property, value);
            }
        }

        // =====================================================================
        // Геометрия модели (базовые размеры в метрах, умножаются на scale).
        // Ось ствола — локальный +Z корня, «вверх» — +Y. Дуло на z = 0.40.
        // Те же параметры продублированы в /tmp/render_preview.py для превью.
        // =====================================================================
        private static void BuildModel(Transform root, float s)
        {
            // --- Корпус: скруглённый блок (куб + две сферы-торцевые) ---
            AddPart(root, "Body", PrimitiveType.Cube,
                new Vector3(0f, 0f, 0.100f) * s, Vector3.zero,
                new Vector3(0.055f, 0.070f, 0.150f) * s, _metalMaterial);
            AddPart(root, "BodyRearDome", PrimitiveType.Sphere,
                new Vector3(0f, 0.005f, 0.020f) * s, Vector3.zero,
                new Vector3(0.060f, 0.065f, 0.060f) * s, _metalMaterial);
            AddPart(root, "BodyFrontDome", PrimitiveType.Sphere,
                new Vector3(0f, 0f, 0.175f) * s, Vector3.zero,
                new Vector3(0.058f, 0.058f, 0.058f) * s, _metalMaterial);

            // --- Бак с «зелёной жидкостью» сверху + 2 металлических обода ---
            AddPart(root, "Tank", PrimitiveType.Capsule,
                new Vector3(0f, 0.058f, 0.095f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.042f, 0.050f, 0.042f) * s, _greenMaterial);
            AddPart(root, "TankRingFront", PrimitiveType.Cylinder,
                new Vector3(0f, 0.058f, 0.048f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.050f, 0.006f, 0.050f) * s, _metalMaterial);
            AddPart(root, "TankRingRear", PrimitiveType.Cylinder,
                new Vector3(0f, 0.058f, 0.142f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.050f, 0.006f, 0.050f) * s, _metalMaterial);

            // --- Ствол: конус из 3 цилиндров уменьшающегося диаметра + 2 кольца ---
            AddPart(root, "Barrel1", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.210f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.045f, 0.030f, 0.045f) * s, _metalMaterial);
            AddPart(root, "BarrelRing1", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.245f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.056f, 0.007f, 0.056f) * s, _greenMaterial);
            AddPart(root, "Barrel2", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.285f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.034f, 0.030f, 0.034f) * s, _metalMaterial);
            AddPart(root, "BarrelRing2", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.318f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.043f, 0.006f, 0.043f) * s, _greenMaterial);
            AddPart(root, "Barrel3", PrimitiveType.Cylinder,
                new Vector3(0f, 0f, 0.355f) * s, new Vector3(90f, 0f, 0f),
                new Vector3(0.026f, 0.030f, 0.026f) * s, _metalMaterial);

            // --- Наконечник: светящаяся зелёная сфера (отсюда вылетает луч) ---
            AddPart(root, "Tip", PrimitiveType.Sphere,
                new Vector3(0f, 0f, 0.392f) * s, Vector3.zero,
                new Vector3(0.026f, 0.026f, 0.026f) * s, _greenMaterial);

            // --- Рукоять (наклонный куб) + спуск + скоба ---
            // Позиция рукояти — константа GripOffsetBase (её же читает GunHandlingTuner).
            AddPart(root, "Grip", PrimitiveType.Cube,
                GripOffsetBase * s, new Vector3(18f, 0f, 0f),
                new Vector3(0.034f, 0.085f, 0.042f) * s, _metalMaterial);
            AddPart(root, "Trigger", PrimitiveType.Cube,
                new Vector3(0f, -0.030f, 0.078f) * s, Vector3.zero,
                new Vector3(0.010f, 0.022f, 0.012f) * s, _metalMaterial);
            AddPart(root, "TriggerGuard", PrimitiveType.Cube,
                new Vector3(0f, -0.048f, 0.082f) * s, Vector3.zero,
                new Vector3(0.006f, 0.006f, 0.050f) * s, _metalMaterial);

            // --- Плавники/«ушки»: два боковых + один верхний кормовой ---
            AddPart(root, "FinLeft", PrimitiveType.Cube,
                new Vector3(-0.036f, 0.012f, 0.075f) * s, new Vector3(0f, 0f, 12f),
                new Vector3(0.008f, 0.030f, 0.055f) * s, _metalMaterial);
            AddPart(root, "FinRight", PrimitiveType.Cube,
                new Vector3(0.036f, 0.012f, 0.075f) * s, new Vector3(0f, 0f, -12f),
                new Vector3(0.008f, 0.030f, 0.055f) * s, _metalMaterial);
            AddPart(root, "FinTop", PrimitiveType.Cube,
                new Vector3(0f, 0.045f, 0.005f) * s, new Vector3(-20f, 0f, 0f),
                new Vector3(0.010f, 0.035f, 0.045f) * s, _metalMaterial);

            // --- Боковые зелёные «заклёпки»-индикаторы ---
            AddPart(root, "RivetLeft", PrimitiveType.Sphere,
                new Vector3(-0.029f, 0.010f, 0.120f) * s, Vector3.zero,
                new Vector3(0.012f, 0.012f, 0.012f) * s, _greenMaterial);
            AddPart(root, "RivetRight", PrimitiveType.Sphere,
                new Vector3(0.029f, 0.010f, 0.120f) * s, Vector3.zero,
                new Vector3(0.012f, 0.012f, 0.012f) * s, _greenMaterial);
        }

        /// <summary>
        /// Создаёт примитив, вешает на корень модели, назначает материал и
        /// удаляет его Collider — коллизия предмета остаётся от ванильного префаба.
        /// </summary>
        private static void AddPart(Transform root, string name, PrimitiveType type,
            Vector3 localPosition, Vector3 localEuler, Vector3 localScale, Material material)
        {
            GameObject part = GameObject.CreatePrimitive(type);
            part.name = VisualRootName + "_" + name;
            part.layer = root.gameObject.layer;

            Collider collider = part.GetComponent<Collider>();
            if (collider != null)
            {
                Object.Destroy(collider);
            }

            Transform partTransform = part.transform;
            partTransform.SetParent(root, false);
            partTransform.localPosition = localPosition;
            partTransform.localRotation = Quaternion.Euler(localEuler);
            partTransform.localScale = localScale;

            MeshRenderer renderer = part.GetComponent<MeshRenderer>();
            if (renderer != null && material != null)
            {
                renderer.sharedMaterial = material;
            }
        }
    }
}
