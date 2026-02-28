using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using HarmonyLib;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;

namespace ScavTaskMod
{
    [BepInPlugin("scavtaskmod.plugin", "ScavTaskMod", "1.0.0")]
    [BepInDependency("xyz.drakia.bigbrain", "1.4.0")]
    [BepInProcess("EscapeFromTarkov.exe")]
    public class ScavTaskPlugin : BaseUnityPlugin
    {
        public static ManualLogSource Log;

        // true = боты всегда активны (AI sleeping отключён)
        public static ConfigEntry<bool> DisableAISleeping;

        private void Awake()
        {
            Log = Logger;

            DisableAISleeping = Config.Bind(
                "AI",
                "DisableAISleeping",
                true,
                new ConfigDescription(
                    "Отключить засыпание ботов на расстоянии. " +
                    "true = мозги ботов работают на любой дистанции (рекомендуется для ScavTaskMod). " +
                    "false = стандартное поведение EFT (боты засыпают за ~130м)."
                )
            );

            // Патч BotStandBy.Update()
            var harmony = new Harmony("scavtaskmod.ailimit");
            var target  = typeof(BotStandBy).GetMethod("Update",
                              BindingFlags.Public | BindingFlags.Instance);
            var prefix  = typeof(BotStandByUpdatePatch).GetMethod("Prefix",
                              BindingFlags.Static | BindingFlags.Public);

            if (target != null && prefix != null)
            {
                harmony.Patch(target, new HarmonyMethod(prefix));
                Log.LogInfo("[ScavTaskMod] BotStandBy.Update patched (AI sleeping control ready)");
            }
            else
            {
                Log.LogWarning("[ScavTaskMod] BotStandBy.Update NOT found — AI sleeping patch skipped");
            }

            Log.LogInfo("[ScavTaskMod] Loaded");

            // Приоритет 40 — выше StandBy (25-30) но ниже боевых слоёв EFT (55+)
            BrainManager.AddCustomLayer(
                typeof(ScavTaskLayer),
                new List<string> { "Assault" },
                40,
                new List<WildSpawnType> { WildSpawnType.assault }
            );
            Log.LogInfo("[ScavTaskMod] Layer registered for Assault brain (priority 40)");

            // Решала (bossBully) — прячется, отстреливается, босс
            // Приоритет 35: выше StandBy, ниже боевых слоёв EFT; SAIN перехватывает через return false
            BrainManager.AddCustomLayer(
                typeof(ReshalaBossLayer),
                new List<string> { "BossBully", "bossBully" },
                35,
                new List<WildSpawnType> { WildSpawnType.bossBully }
            );
            Log.LogInfo("[ScavTaskMod] ReshalaBossLayer registered (priority 35)");

            // Свита Решалы (followerBully) — периметр / давление / охота на убийцу
            BrainManager.AddCustomLayer(
                typeof(ReshalaGuardLayer),
                new List<string> { "FollowerBully", "followerBully" },
                35,
                new List<WildSpawnType> { WildSpawnType.followerBully }
            );
            Log.LogInfo("[ScavTaskMod] ReshalaGuardLayer registered (priority 35)");

            // PMC-боты (BEAR и USEC) — квесты EFT + охота на боссов
            // Приоритет 38: выше StandBy и Reshala (35), ниже боевых слоёв EFT (55+)
            BrainManager.AddCustomLayer(
                typeof(PmcTaskLayer),
                new List<string> { "PmcBear", "PmcUsec" },
                38,
                new List<WildSpawnType> { WildSpawnType.pmcBEAR, WildSpawnType.pmcUSEC }
            );
            Log.LogInfo("[ScavTaskMod] PmcTaskLayer registered for PmcBear/PmcUsec (priority 38)");
        }

        private void Start()
        {
            StartCoroutine(CapturePlayerSpawn());
        }

        // Ждём начала рейда и захватываем позицию спавна игрока
        private IEnumerator CapturePlayerSpawn()
        {
            // Сброс состояния при старте нового рейда
            ScavTaskLayer.PlayerSpawnKnown = false;
            ScavTaskLayer.NoBossOnMap      = false;
            ReshalaShared.Reset();
            PmcTaskLayer.Reset();

            // Ждём пока загрузится GameWorld с игроком
            while (true)
            {
                var gw = Singleton<GameWorld>.Instance;
                if (gw?.MainPlayer != null &&
                    gw.MainPlayer.HealthController.IsAlive &&
                    !gw.MainPlayer.IsYourPlayer == false)
                    break;
                yield return new WaitForSeconds(1f);
            }

            // Даём игроку 6 секунд чтобы точно оказаться на спавне
            yield return new WaitForSeconds(6f);

            var world = Singleton<GameWorld>.Instance;
            if (world?.MainPlayer != null)
            {
                ScavTaskLayer.PlayerSpawnPos   = world.MainPlayer.Position;
                ScavTaskLayer.PlayerSpawnKnown = true;
                Log.LogInfo($"[ScavTaskMod] Player spawn captured: {ScavTaskLayer.PlayerSpawnPos}");

                // Карта и игрок готовы — инициализируем EFT-квесты
                EftQuestTaskManager.Initialize();
                Log.LogInfo("[ScavTaskMod] EftQuestTaskManager initialized");
            }
        }

        private void Update()
        {
            EftQuestTaskManager.Tick();
        }
    }

    // Патч: пропускаем BotStandBy.Update() если DisableAISleeping = true
    // Это предотвращает переход бота в состояние paused (засыпание) по дистанции
    public static class BotStandByUpdatePatch
    {
        public static bool Prefix(BotStandBy __instance)
        {
            // Если настройка выключена — пропускаем патч, работает оригинал
            if (!ScavTaskPlugin.DisableAISleeping.Value)
                return true;

            // Убеждаемся что бот не застрял в paused/goToSave от предыдущего состояния
            if (__instance.StandByType != BotStandByType.active)
                __instance.Activate();

            // Возвращаем false = не вызывать оригинальный Update()
            return false;
        }
    }
}
