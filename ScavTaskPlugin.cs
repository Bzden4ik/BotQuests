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
        }

        private void Start()
        {
            StartCoroutine(CapturePlayerSpawn());
        }

        // Ждём начала рейда и захватываем позицию спавна игрока
        private IEnumerator CapturePlayerSpawn()
        {
            ScavTaskLayer.PlayerSpawnKnown = false;

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
            }
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
