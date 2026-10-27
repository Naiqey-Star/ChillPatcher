using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using BepInEx;
using BepInEx.Logging;
using HarmonyLib;
using Bulbul;
using ChillPatcher.Patches;
using ChillPatcher.Patches.UIFramework;
using ChillPatcher.UIFramework;
using ChillPatcher.UIFramework.Config;
using ChillPatcher.UIFramework.Music;
using ChillPatcher.SDK.Interfaces;
using Cysharp.Threading.Tasks;

namespace ChillPatcher
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        private const string SteamAppId = "3548580";
        internal static new ManualLogSource Logger;
        internal static ManualLogSource Log;

        /// <summary>
        /// 鎻掍欢鏍圭洰褰?        /// </summary>
        public static string PluginPath { get; private set; }

        private void Awake()
        {
            // 启动不再显示黑色控制台窗口；只有真正需要构建/切换 profile 时才显示。
            Logger = base.Logger;
            Log = Logger;
            // Record runtime provenance before patching or SDK-dependent initialization.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => a.GetName().Name.StartsWith("ChillPatcher", StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var location = assembly.Location;
                    var hash = "unavailable";
                    if (File.Exists(location))
                    {
                        using (var sha = System.Security.Cryptography.SHA256.Create())
                        using (var stream = File.OpenRead(location))
                            hash = BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "");
                    }
                    Logger.LogInfo($"[RuntimeProvenance] Assembly={assembly.FullName}; Path={location}; MVID={assembly.ManifestModule.ModuleVersionId}; SHA256={hash}");
                }
                catch (Exception ex)
                {
                    Logger.LogWarning($"[RuntimeProvenance] {assembly.FullName}: {ex.Message}");
                }
            }

            PluginPath = Path.GetDirectoryName(Info.Location);

            EnsureSteamAppIdFile();

            CoreDependencyLoader.EnsureDependencies(Log);
            Logger.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} is loaded!");

            // 初始化配置
            PluginConfig.Initialize(Config);
            UIFrameworkConfig.Initialize(Config);

            // Apply Harmony patches
            var harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            try
            {
                harmony.PatchAll();
                Logger.LogInfo("Harmony patches applied!");
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Some Harmony patches failed: {ex.Message}");
                foreach (var type in System.Reflection.Assembly.GetExecutingAssembly().GetTypes())
                {
                    try
                    {
                        if (type.GetCustomAttributes(typeof(HarmonyPatch), false).Length > 0 ||
                            type.GetNestedTypes().Length > 0)
                        {
                            harmony.CreateClassProcessor(type).Patch();
                        }
                    }
                    catch (Exception ex2)
                    {
                        Logger.LogWarning($"Skipped patch {type.Name}: {ex2.Message}");
                    }
                }
            }

            Logger.LogInfo("==== ChillPatcher Configuration ====");
            Logger.LogInfo($"Album Art Display: {(UIFrameworkConfig.EnableAlbumArtDisplay.Value ? "ON" : "OFF")}");
            Logger.LogInfo($"Unlimited Songs: {(UIFrameworkConfig.EnableUnlimitedSongs.Value ? "ON" : "OFF")}");
            Logger.LogInfo($"Extended Formats: {(UIFrameworkConfig.EnableExtendedFormats.Value ? "ON" : "OFF")}");
            Logger.LogInfo($"IPC Backend: Always external (OmniMixPlayer)");
            Logger.LogInfo("====================================");

            KeyboardHookPatch.Initialize();

            // 自动日记：AIChat 每轮对话后更新当天原生日记
            try
            {
                Integration.AutoDiaryService.Initialize(Logger);
                // 日历记忆：把日记注入 AIChat 的 System Prompt
                Integration.CalendarMemoryService.Initialize(Logger);
            }
            catch (Exception ex)
            {
                Logger.LogWarning($"Failed to initialize AutoDiaryService: {ex.Message}");
            }

            // Automatic compact layout replaces the interactive layout editor.
            try
            {
                ChillPatcher.Patches.RightIconsCompactLayout.Ensure();
                ChillPatcher.Patches.RightBottomRowLayout.Ensure();
                ChillPatcher.Patches.WallpaperMouseBridge.Ensure();
                ChillPatcher.Patches.RimeCandidateOverlay.Ensure();
                // Interactive F8 layout editor removed; automatic compact layout only.
                Logger.LogInfo("RightIcons compact layout initialized");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize compact layout: {ex}");
            }

            if (PluginConfig.EnableAchievementCache.Value)
            {
                AchievementSyncManager.Initialize();
            }

            if (PluginConfig.EnableWallpaperEngineMode.Value)
            {
                SteamReconnectManager.Initialize();
            }

            // 初始化 UI 框架（不再初始化模块系统）
            try
            {
                ChillUIFramework.Initialize();
                Logger.LogInfo("ChillUIFramework initialized!");
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize UI Framework: {ex}");
            }

            // 初始化 OneJS
            try
            {
                var uiDir = Path.Combine(PluginPath, "ui");
                OneJSBridge.Initialize(uiDir, Config, Logger);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to initialize OneJS: {ex}");
            }

            // PlayerLoop
            try
            {
                PlayerLoopInjector.Install(Logger);
            }
            catch (Exception ex)
            {
                Logger.LogError($"Failed to install PlayerLoop injector: {ex}");
            }
        }

        private void EnsureSteamAppIdFile()
        {
            TryWriteSteamAppIdFile(Directory.GetCurrentDirectory());
            try
            {
                var appRoot = Path.GetDirectoryName(UnityEngine.Application.dataPath);
                if (!string.IsNullOrEmpty(appRoot))
                    TryWriteSteamAppIdFile(appRoot);
            }
            catch (Exception ex)
            {
                Logger?.LogWarning($"[SteamAppId] {ex.Message}");
            }
        }

        private void TryWriteSteamAppIdFile(string directory)
        {
            try
            {
                if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory)) return;
                var appIdPath = Path.Combine(directory, "steam_appid.txt");
                var needsWrite = !File.Exists(appIdPath) ||
                    !string.Equals(File.ReadAllText(appIdPath).Trim(), SteamAppId, StringComparison.Ordinal);
                if (!needsWrite) return;
                File.WriteAllText(appIdPath, SteamAppId + Environment.NewLine);
                Logger?.LogInfo($"[SteamAppId] Written {appIdPath}");
            }
            catch { }
        }

        private void OnApplicationQuit()
        {
            Logger.LogInfo("OnApplicationQuit - cleaning up...");

            try
            {
                PlaybackStateManager.Instance?.ForceSave();
                Logger.LogInfo("Playback state saved!");
            }
            catch { }

            try
            {
                OmniMixIntegration.Instance?.Dispose();
                Logger.LogInfo("OmniMix integration cleaned up!");
            }
            catch { }

            KeyboardHookPatch.Cleanup();
            try { OneJSBridge.Shutdown(); } catch { }
            try { ChillUIFramework.Cleanup(); } catch { }
        }
    }
}
