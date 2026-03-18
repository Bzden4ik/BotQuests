using BepInEx;
using BepInEx.Logging;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace LifePMC
{
    [BepInPlugin("com.lifepmc.mod", "LifePMC", "1.0.0")]
    [BepInDependency("xyz.drakia.bigbrain")]
    public class Plugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;
        public static string SaveDir;

        private string _loadedMapId;
        private bool   _gameWorldWasNull = true;

        private void Awake()
        {
            Log = Logger;

            LifePMCConfig.Init(base.Config);

            string pluginsDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            SaveDir = Path.Combine(pluginsDir, "PointEditor");
            Directory.CreateDirectory(SaveDir);

            // ── Регистрация слоёв ────────────────────────────────────────────────
            // Имена мозгов SPT 4.0 (совпадают с BaseBrain.ShortName() из SAIN BigBrainHandler)
            var brainTypes = new List<string> { "PMC", "PmcBear", "PmcUsec" };
            BrainManager.AddCustomLayer(typeof(PMCLayer), brainTypes, 25);

            // ── Стартовый лог ────────────────────────────────────────────────────
            Log.LogInfo("=================================================");
            Log.LogInfo("[LifePMC] МОД ЗАГРУЖЕН v1.0");
            Log.LogInfo($"[LifePMC] Папка точек : {SaveDir}");
            Log.LogInfo($"[LifePMC] Мозги ботов : {string.Join(", ", brainTypes)} (приоритет 25)");
            Log.LogInfo($"[LifePMC] Файл точек  : PointEditor/<mapId>.json");
            Log.LogInfo($"[LifePMC] Конфиг      : таймаут={LifePMCConfig.ObjectiveTimeout.Value}с  " +
                        $"кулдаун={LifePMCConfig.ObjectiveCooldown.Value}с  " +
                        $"бой-пауза={LifePMCConfig.CombatCooldown.Value}с");
            Log.LogInfo("=================================================");
        }

        private void Update()
        {
            var gw = Singleton<GameWorld>.Instance;

            if (gw != null)
            {
                // GameWorld только что появился — логируем
                if (_gameWorldWasNull)
                {
                    _gameWorldWasNull = false;
                    Log.LogInfo("[LifePMC] GameWorld появился — определяю карту...");
                }

                string mapId = GetMapId(gw);

                if (mapId != null && mapId != _loadedMapId)
                {
                    _loadedMapId = mapId;
                    Log.LogInfo($"[LifePMC] Карта: '{mapId}' — загружаю точки...");
                    PointLoader.Load(mapId, SaveDir);
                }
                else if (mapId == null && _loadedMapId == null)
                {
                    // Карта ещё не определилась — тихо ждём
                }
            }
            else if (_loadedMapId != null)
            {
                Log.LogInfo("[LifePMC] GameWorld исчез — рейд закончен, сбрасываю данные.");
                _loadedMapId = null;
                _gameWorldWasNull = true;
                PointLoader.Reset();
            }
        }

        private static string GetMapId(GameWorld gw)
        {
            try
            {
                string id = gw.LocationId;
                if (!string.IsNullOrEmpty(id)) return id.ToLowerInvariant();
            }
            catch { }
            try
            {
                return gw.MainPlayer?.Location?.ToLowerInvariant();
            }
            catch { }
            return null;
        }
    }
}
