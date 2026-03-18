using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using UnityEngine;

namespace LifePMC
{
    public class PMCLayer : CustomLayer
    {
        private float _lastCombatTime = -999f;
        private float _nextActionTime = 0f;
        private int   _stuckCount     = 0;
        private bool  _permanentStop  = false;
        private bool  _objectiveDone  = false;

        // ── Дебаг-троттлинг: логируем причину блокировки не чаще раза в 10с ────
        private float  _lastBlockLogTime = -999f;
        private string _lastBlockReason  = "";
        private const float BLOCK_LOG_INTERVAL = 10f;

        // Логируем создание слоя только при первой активации
        private bool _firstActivation = true;

        public PMCLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            PMCGoToLogic.LayerMap[botOwner.name] = this;

            botOwner.GetPlayer.HealthController.DiedEvent += _ =>
                PMCGoToLogic.LayerMap.Remove(botOwner.name);

            // Логируем что слой был создан для этого бота
            string nick   = botOwner.Profile?.Nickname ?? "???";
            string role   = botOwner.Profile?.Info?.Settings?.Role.ToString() ?? "???";
            string side   = botOwner.Profile?.Side.ToString() ?? "???";
            string brain  = botOwner.Brain?.BaseBrain?.ShortName() ?? "???";
            Plugin.Log.LogInfo($"[LifePMC] PMCLayer создан для бота: " +
                               $"nick={nick}  role={role}  side={side}  brain={brain}  name={botOwner.name}");
        }

        public override string GetName() => "LifePMC_PMCLayer";

        public override bool IsActive()
        {
            try
            {
                // ── Базовые проверки ─────────────────────────────────────────────
                if (BotOwner == null || BotOwner.Mover == null)
                    return LogBlock("Mover=null");

                if (BotOwner.BotState != EBotState.Active)
                    return LogBlock($"BotState={BotOwner.BotState}");

                if (_permanentStop)
                    return LogBlock("permanentStop");

                // ── Данные загружены? ─────────────────────────────────────────────
                if (!PointLoader.IsLoaded)
                    return LogBlock("IsLoaded=false (ждём загрузки карты)");

                if (PointLoader.GetPoints().Count == 0)
                    return LogBlock("нет точек для этой карты");

                // ── Лимит застреваний ─────────────────────────────────────────────
                if (_stuckCount >= LifePMCConfig.MaxStuck.Value)
                {
                    _permanentStop = true;
                    Plugin.Log.LogWarning($"[LifePMC] {BotOwner.name} — слишком много застреваний ({_stuckCount}), стоп навсегда");
                    return false;
                }

                // ── Бой ───────────────────────────────────────────────────────────
                bool combat = IsBotInCombat(out string combatReason);
                if (combat)
                {
                    _lastCombatTime = Time.time;
                    return LogBlock($"в бою ({combatReason})");
                }

                float combatAgo = Time.time - _lastCombatTime;
                if (combatAgo < LifePMCConfig.CombatCooldown.Value)
                    return LogBlock($"кулдаун после боя ({combatAgo:F0}/{LifePMCConfig.CombatCooldown.Value:F0}с)");

                // ── Кулдаун после цели ────────────────────────────────────────────
                float waitLeft = _nextActionTime - Time.time;
                if (waitLeft > 0f)
                    return LogBlock($"ждёт след. задание ({waitLeft:F0}с осталось)");

                // ── АКТИВЕН ───────────────────────────────────────────────────────
                if (_firstActivation)
                {
                    _firstActivation = false;
                    Plugin.Log.LogInfo($"[LifePMC] ★ {BotOwner.name} — слой АКТИВЕН, выдаю задание!");
                }
                _lastBlockReason = "";
                return true;
            }
            catch (Exception ex)
            {
                Plugin.Log.LogError($"[LifePMC] PMCLayer.IsActive exception: {ex.Message}");
                return false;
            }
        }

        public override bool IsCurrentActionEnding() => _objectiveDone;

        public void OnObjectiveComplete(bool wasStuck)
        {
            _objectiveDone  = true;
            _stuckCount     = wasStuck ? _stuckCount + 1 : 0;
            _nextActionTime = Time.time + LifePMCConfig.ObjectiveCooldown.Value;

            string result = wasStuck ? "ЗАСТРЯЛ" : "выполнено";
            Plugin.Log.LogInfo($"[LifePMC] {BotOwner.name} задание {result}. " +
                               $"Stucks={_stuckCount}. Следующее через {LifePMCConfig.ObjectiveCooldown.Value:F0}с");
        }

        public override Action GetNextAction()
        {
            _objectiveDone = false;
            return new Action(typeof(PMCGoToLogic), "GoToPoint");
        }

        public override void Stop() => _objectiveDone = false;

        // ── Логирует причину блокировки (не чаще BLOCK_LOG_INTERVAL) ─────────────
        private bool LogBlock(string reason)
        {
            if (reason != _lastBlockReason || Time.time - _lastBlockLogTime > BLOCK_LOG_INTERVAL)
            {
                _lastBlockReason  = reason;
                _lastBlockLogTime = Time.time;
                Plugin.Log.LogInfo($"[LifePMC] {BotOwner?.name ?? "?"} не активен: {reason}");
            }
            return false;
        }

        // ── Проверка боя ──────────────────────────────────────────────────────────
        private bool IsBotInCombat(out string reason)
        {
            reason = "";
            try
            {
                var mem = BotOwner.Memory;
                if (mem == null) return false;

                if (mem.GoalEnemy != null && mem.GoalEnemy.IsVisible)
                {
                    reason = $"враг виден: {mem.GoalEnemy.Person?.Profile?.Nickname ?? "???"}";
                    return true;
                }
                if (mem.GoalEnemy != null && Time.time - mem.GoalEnemy.TimeLastSeen < 10f)
                {
                    reason = $"враг недавно видел ({(Time.time - mem.GoalEnemy.TimeLastSeen):F0}с назад)";
                    return true;
                }
                if (mem.IsUnderFire)
                {
                    reason = "под огнём";
                    return true;
                }
            }
            catch (Exception ex)
            {
                reason = $"exception: {ex.Message}";
            }
            return false;
        }
    }
}
