using System;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Shrinkinator
{
    /// <summary>
    /// Code-created VFX зелёного тумана — без ассетов, только ParticleSystem.
    /// Проигрывается локально у каждого клиента из постфикса ShootBulletRPC.
    /// </summary>
    internal static class MistVfx
    {
        private static Material _material;
        private static bool _materialReady;

        /// <summary>
        /// Материал ванильного дыма пули — только запасной вариант.
        /// Основной материал собираем сами: ванильный дым слишком плотный и без fade.
        /// </summary>
        internal static void Init(Material baseMaterial)
        {
            // Предпочитаем свой прозрачный шейдер; ванильный материал — fallback.
            EnsureMaterial(baseMaterial);
        }

        private static Material EnsureMaterial(Material fallback = null)
        {
            if (_materialReady)
            {
                return _material;
            }

            _materialReady = true;
            try
            {
                Shader shader = Shader.Find("Legacy Shaders/Particles/Alpha Blended")
                                ?? Shader.Find("Particles/Standard Unlit")
                                ?? Shader.Find("Sprites/Default")
                                ?? (fallback != null ? fallback.shader : null);
                if (shader == null)
                {
                    _material = fallback;
                    return _material;
                }

                _material = new Material(shader)
                {
                    color = new Color(0.4f, 1f, 0.5f, 0.18f),
                    renderQueue = 3000
                };
                if (_material.HasProperty("_TintColor"))
                {
                    _material.SetColor("_TintColor", new Color(0.35f, 1f, 0.45f, 0.12f));
                }
            }
            catch (Exception e)
            {
                Log.Warning("[Shrinkinator] Не удалось подготовить материал тумана: " + e.Message);
                _material = fallback;
            }

            return _material;
        }

        /// <summary>Создаёт облако зелёного тумана в указанной точке.</summary>
        internal static void Spawn(Vector3 position, float radius)
        {
            try
            {
                var go = new GameObject("ShrinkinatorMist");
                go.transform.position = position;
                Object.Destroy(go, 3.5f);

                ParticleSystem ps = go.AddComponent<ParticleSystem>();
                ParticleSystem.MainModule main = ps.main;
                main.playOnAwake = false;
                main.loop = false;
                main.duration = 0.35f;
                main.startLifetime = new ParticleSystem.MinMaxCurve(0.8f, 1.4f);
                main.startSpeed = new ParticleSystem.MinMaxCurve(0.15f, 0.45f);
                main.startSize = new ParticleSystem.MinMaxCurve(
                    Mathf.Max(0.25f, radius * 0.18f),
                    Mathf.Max(0.4f, radius * 0.35f));
                main.startColor = new Color(0.4f, 1f, 0.5f, 0.2f);
                main.maxParticles = 24;
                main.simulationSpace = ParticleSystemSimulationSpace.World;
                main.stopAction = ParticleSystemStopAction.Destroy;
                main.gravityModifier = -0.05f;

                ParticleSystem.EmissionModule emission = ps.emission;
                emission.rateOverTime = 0f;
                emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 14) });

                ParticleSystem.ShapeModule shape = ps.shape;
                shape.enabled = true;
                shape.shapeType = ParticleSystemShapeType.Sphere;
                shape.radius = Mathf.Max(0.15f, radius * 0.35f);

                ParticleSystem.ColorOverLifetimeModule color = ps.colorOverLifetime;
                color.enabled = true;
                var gradient = new Gradient();
                gradient.SetKeys(
                    new[]
                    {
                        new GradientColorKey(new Color(0.45f, 1f, 0.55f), 0f),
                        new GradientColorKey(new Color(0.2f, 0.8f, 0.3f), 1f)
                    },
                    new[]
                    {
                        new GradientAlphaKey(0.22f, 0f),
                        new GradientAlphaKey(0.12f, 0.35f),
                        new GradientAlphaKey(0f, 1f)
                    });
                color.color = gradient;

                ParticleSystem.SizeOverLifetimeModule size = ps.sizeOverLifetime;
                size.enabled = true;
                size.size = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.EaseInOut(0f, 0.7f, 1f, 1.6f));

                ParticleSystem.VelocityOverLifetimeModule velocity = ps.velocityOverLifetime;
                velocity.enabled = true;
                velocity.speedModifier = new ParticleSystem.MinMaxCurve(1f, AnimationCurve.Linear(0f, 1f, 1f, 0.15f));

                ParticleSystem.CollisionModule collision = ps.collision;
                collision.enabled = false;

                ParticleSystemRenderer renderer = ps.GetComponent<ParticleSystemRenderer>();
                renderer.renderMode = ParticleSystemRenderMode.Billboard;
                Material material = EnsureMaterial();
                if (material != null)
                {
                    renderer.sharedMaterial = material;
                }

                ps.Play();
            }
            catch (Exception e)
            {
                Log.Error("[Shrinkinator] Ошибка создания VFX тумана", e);
            }
        }
    }
}
