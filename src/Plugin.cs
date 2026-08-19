using System;
using BepInEx;
using HarmonyLib;

namespace Shrinkinator
{
    /// <summary>
    /// Точка входа плагина «Уменьшитель-инатор».
    /// Требует REPOLib (жёсткая зависимость): через неё регистрируется предмет в магазине.
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    [BepInDependency(REPOLib.MyPluginInfo.PLUGIN_GUID, BepInDependency.DependencyFlags.HardDependency)]
    public class Plugin : BaseUnityPlugin
    {
        internal const string PluginGuid = "com.kimi.shrinkinator";
        internal const string PluginName = "Shrinkinator";
        internal const string PluginVersion = "1.0.8";

        private void Awake()
        {
            Log.Init(Logger);
            ShrinkinatorConfig.Init(Config);

            // Регистрируем все Harmony-патчи сборки (Patches.cs).
            var harmony = new Harmony(PluginGuid);
            try
            {
                harmony.PatchAll();
            }
            catch (Exception e)
            {
                // Один сбойный патч не должен ронять остальные.
                Log.Error("[Shrinkinator] Ошибка применения Harmony-патчей", e);
            }

            Log.Info(PluginName + " v" + PluginVersion + " загружен.");
        }
    }
}
