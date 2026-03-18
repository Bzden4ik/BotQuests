using BepInEx.Logging;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace LifePMC
{
    public class PMCGoToLogic : CustomLogic
    {
        private static readonly ManualLogSource Log =
            BepInEx.Logging.Logger.CreateLogSource("LifePMC");

        /// <summary>Статус бота для DebugOver. Ключ: bot.name → строка статуса.</summary>
        public static readonly Dictionary<string, string> BotStatusMap =
            new Dictionary<string, string>();

        /// <summary>Ссылки Layer → Logic для вызова OnObjectiveComplete.</summary>
        public static readonly Dictionary<string, PMCLayer> LayerMap =
            new Dictionary<string, PMCLayer>();

        // ── LootingBots (lazy init через reflection) ──────────────────────────
        private static MethodInfo _forceScanMethod;
        private static bool       _lootingBotsChecked;

        // ── Фазы ──────────────────────────────────────────────────────────────
        private enum Phase
        {
            Navigate,  // Бежим к основной точке
            Wander,    // Обходим саб-точки (или просто стоим) — ограничено по времени
            Done
        }

        // ── Состояние ─────────────────────────────────────────────────────────
        private PMCLayer   _layer;
        private QuestPoint _point;
        private Phase      _phase;
        private float      _startTime;

        // ── Время на точке ────────────────────────────────────────────────────
        private float _locationEndTime;   // когда уйти с точки
        private float _locationDuration;  // сколько секунд провести (для логов)

        // ── Саб-точки ─────────────────────────────────────────────────────────
        private List<SubPoint> _subs;
        private int            _subIndex;
        private int            _subLoopCount;
        private Vector3        _wanderTarget;
        private bool           _wanderTargetSet;

        // ── Ожидание на stay-точке ─────────────────────────────────────────────
        private bool  _waitingAtSub;
        private float _subWaitEndTime;

        // ── Анти-застрев ──────────────────────────────────────────────────────
        private Vector3 _lastPos;
        private float   _lastPosTime;
        private const float STUCK_INTERVAL = 5f;
        private const float STUCK_DIST     = 0.5f;

        // ── GoToPoint переотправка ─────────────────────────────────────────────
        private float   _lastGoToTime = -999f;
        private Vector3 _lastGoToTarget;
        private const float GOTO_INTERVAL  = 2f;
        private const float GOTO_DEVIATION = 3f;
        private const float REACH_DIST          = 3f;
        private const float WANDER_REACH        = 3f;   // дистанция «добрался до саб-точки»
        private const float AUTO_WANDER_RADIUS  = 50f;  // радиус авто-блуждания (нет саб-точек)
        private const float AUTO_WANDER_MIN_R   = 8f;   // минимальный отступ от центра

        // ── Коррекция позы ────────────────────────────────────────────────────
        private float _lastPostureTime = -999f;
        private const float POSTURE_INTERVAL = 1f;

        // ── Дебаг: расстояние каждые 10с ──────────────────────────────────────
        private float _lastDistLogTime = -999f;
        private const float DIST_LOG_INTERVAL = 10f;

        public PMCGoToLogic(BotOwner botOwner) : base(botOwner) { }

        // ════════════════════════════════════════════════════════════════════════
        public override void Start()
        {
            LayerMap.TryGetValue(BotOwner.name, out _layer);

            _point = PointLoader.SelectPoint();
            if (_point == null)
            {
                Log.LogWarning($"[LifePMC] {BotOwner.name} Start(): нет доступных точек!");
                SetStatus("нет точек");
                _phase = Phase.Done;
                return;
            }

            _phase           = Phase.Navigate;
            _startTime       = Time.time;
            _lastPos         = BotOwner.Position;
            _lastPosTime     = Time.time;
            _lastGoToTime    = -999f;
            _lastDistLogTime = -999f;
            _subs            = null;
            _wanderTargetSet = false;
            _waitingAtSub    = false;

            BotOwner.Mover.SetTargetMoveSpeed(1f);
            BotOwner.Mover.Sprint(true);

            float dist = Vector3.Distance(BotOwner.Position, _point.Position);
            SetStatus($"→ {_point.ZoneName}");

            Log.LogInfo($"[LifePMC] ► {BotOwner.name} НАЧИНАЕТ движение к [{_point.ZoneName}]");
            Log.LogInfo($"[LifePMC]   Цель: ({_point.X:F1}, {_point.Y:F1}, {_point.Z:F1})");
            Log.LogInfo($"[LifePMC]   Дистанция: {dist:F1}м  |  WaitTime: {_point.WaitTime:F0}с");
            Log.LogInfo($"[LifePMC]   Бот позиция: {BotOwner.Position}");
        }

        public override void Stop()
        {
            BotStatusMap.Remove(BotOwner.name);
        }

        // ════════════════════════════════════════════════════════════════════════
        public override void Update(CustomLayer.ActionData data)
        {
            try
            {
                if (_phase == Phase.Done || BotOwner?.Mover == null)
                {
                    Complete(false);
                    return;
                }

                // ── Таймаут навигации ──────────────────────────────────────────
                float elapsed = Time.time - _startTime;
                if (elapsed > LifePMCConfig.ObjectiveTimeout.Value)
                {
                    Log.LogWarning($"[LifePMC] {BotOwner.name} ТАЙМАУТ [{_point.ZoneName}] " +
                                   $"({elapsed:F0}с / {LifePMCConfig.ObjectiveTimeout.Value:F0}с)");
                    Complete(false);
                    return;
                }

                switch (_phase)
                {
                    case Phase.Navigate: UpdateNavigate(elapsed); break;
                    case Phase.Wander:   UpdateWander();          break;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[LifePMC] Update exception ({BotOwner?.name}): {ex.Message}\n{ex.StackTrace}");
                Complete(false);
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Phase: Navigate — спринт к основной точке
        // ────────────────────────────────────────────────────────────────────────
        private void UpdateNavigate(float elapsed)
        {
            float dist = Vector3.Distance(BotOwner.Position, _point.Position);

            if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
            {
                _lastDistLogTime = Time.time;
                Log.LogInfo($"[LifePMC] {BotOwner.name} → [{_point.ZoneName}]  " +
                            $"dist={dist:F1}м  moving={BotOwner.Mover.IsMoving}  " +
                            $"sprint={BotOwner.Mover.Sprinting}  elapsed={elapsed:F0}с");
            }

            if (CheckStuck(dist, REACH_DIST)) return;

            if (dist < REACH_DIST)
            {
                OnArriveAtMainPoint(elapsed);
                return;
            }

            SendGoTo(_point.Position);
            ApplySprintPosture();
        }

        private void OnArriveAtMainPoint(float elapsed)
        {
            BotOwner.Mover.Sprint(false);
            Log.LogInfo($"[LifePMC] ✓ {BotOwner.name} ДОСТИГ [{_point.ZoneName}]! (за {elapsed:F0}с)");

            _locationDuration = _point.WaitTime > 0f
                ? _point.WaitTime
                : LifePMCConfig.DefaultWaitTime.Value;
            _locationEndTime = Time.time + _locationDuration;

            _subs = PointLoader.GetSubPoints(_point.Id);

            if (_subs != null && _subs.Count > 0)
            {
                ShuffleList(_subs);
                _subIndex        = 0;
                _subLoopCount    = 0;
                _wanderTargetSet = false;
                _phase           = Phase.Wander;
                SetStatus($"↳ {_point.ZoneName}");
                Log.LogInfo($"[LifePMC] {BotOwner.name} начинает блуждание: " +
                            $"{_subs.Count} саб-точек, проведёт здесь {_locationDuration:F0}с");
            }
            else
            {
                // Нет саб-точек — авто-блуждание в радиусе AUTO_WANDER_RADIUS
                _subs            = null;
                _wanderTargetSet = false;
                _phase           = Phase.Wander;
                SetStatus($"~ {_point.ZoneName}");
                Log.LogInfo($"[LifePMC] {BotOwner.name} авто-блуждание [{_point.ZoneName}] " +
                            $"r={AUTO_WANDER_RADIUS:F0}м  {_locationDuration:F0}с (саб-точек нет)");
            }

            _lastDistLogTime = -999f;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Phase: Wander — обход саб-точек до истечения времени
        // ────────────────────────────────────────────────────────────────────────
        private void UpdateWander()
        {
            float timeLeft = _locationEndTime - Time.time;

            // ── Время вышло → уходим ──────────────────────────────────────────
            if (timeLeft <= 0f)
            {
                Log.LogInfo($"[LifePMC] {BotOwner.name} время на [{_point.ZoneName}] истекло " +
                            $"(провёл {_locationDuration:F0}с)");
                Complete(false);
                return;
            }

            // ── Ожидание на "stay" саб-точке ──────────────────────────────────
            if (_waitingAtSub)
            {
                if (Time.time >= _subWaitEndTime || Time.time >= _locationEndTime)
                {
                    _waitingAtSub = false;
                    Log.LogInfo($"[LifePMC] {BotOwner.name} закончил стоять на саб-точке {_subIndex + 1}");
                    AdvanceSub();
                }
                else if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
                {
                    _lastDistLogTime = Time.time;
                    float stayLeft   = _subWaitEndTime - Time.time;
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ⏸ стоит на саб {_subIndex + 1}/{_subs.Count}  " +
                                $"ещё={stayLeft:F0}с  timeLeft={timeLeft:F0}с");
                }
                return;
            }

            // ── Нет саб-точек — авто-блуждание в радиусе от основной точки ────
            if (_subs == null || _subs.Count == 0)
            {
                if (!_wanderTargetSet)
                {
                    if (!TryPickAutoWanderTarget())
                    {
                        // NavMesh не найден в радиусе — логируем редко и ждём
                        if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
                        {
                            _lastDistLogTime = Time.time;
                            Log.LogWarning($"[LifePMC] {BotOwner.name} авто-блуждание: " +
                                           $"нет NavMesh  осталось={timeLeft:F0}с");
                        }
                        return;
                    }
                }

                float autoDist = Vector3.Distance(BotOwner.Position, _wanderTarget);

                if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
                {
                    _lastDistLogTime = Time.time;
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ~ [{_point.ZoneName}]  " +
                                $"dist={autoDist:F1}м  осталось={timeLeft:F0}с");
                }

                // Анти-застрев для авто-блуждания
                if (Time.time - _lastPosTime >= STUCK_INTERVAL)
                {
                    float moved  = Vector3.Distance(BotOwner.Position, _lastPos);
                    _lastPos     = BotOwner.Position;
                    _lastPosTime = Time.time;
                    if (moved < STUCK_DIST && autoDist > WANDER_REACH)
                    {
                        Log.LogWarning($"[LifePMC] {BotOwner.name} застрял при авто-блуждании, " +
                                       $"выбираю новую точку");
                        _wanderTargetSet = false;
                        return;
                    }
                }

                if (autoDist < WANDER_REACH)
                {
                    TryTriggerLootScan();
                    _wanderTargetSet = false;   // выберем новую случайную точку
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ~ достиг авто-точки, иду дальше");
                    return;
                }

                SendGoTo(_wanderTarget);
                ApplyWanderPosture();
                return;
            }

            // ── Выбираем следующую цель если ещё не выбрана ────────────────────
            if (!_wanderTargetSet)
            {
                PickWanderTarget();
                return;
            }

            float dist = Vector3.Distance(BotOwner.Position, _wanderTarget);

            if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
            {
                _lastDistLogTime = Time.time;
                var curSub  = _subs[_subIndex % _subs.Count];
                string icon = curSub.IsStay ? "⏸" : "▶";
                Log.LogInfo($"[LifePMC] {BotOwner.name} {icon} саб [{_subIndex + 1}/{_subs.Count}]  " +
                            $"dist={dist:F1}м  осталось={timeLeft:F0}с");
            }

            // Анти-застрев при блуждании (мягкий: переходим к следующей точке)
            if (Time.time - _lastPosTime >= STUCK_INTERVAL)
            {
                float moved = Vector3.Distance(BotOwner.Position, _lastPos);
                _lastPos     = BotOwner.Position;
                _lastPosTime = Time.time;
                if (moved < STUCK_DIST && dist > WANDER_REACH)
                {
                    Log.LogWarning($"[LifePMC] {BotOwner.name} застрял у саб {_subIndex + 1}, " +
                                   $"переходим к следующей (сдвиг={moved:F2}м)");
                    AdvanceSub();
                    return;
                }
            }

            // ── Добрались до саб-точки ────────────────────────────────────────
            if (dist < WANDER_REACH)
            {
                var arrivedSub = _subs[_subIndex % _subs.Count];
                TryTriggerLootScan();

                if (arrivedSub.IsStay)
                {
                    // "stay" — подошли близко, теперь ждём
                    float waitDur    = arrivedSub.WaitTime > 0f
                        ? arrivedSub.WaitTime
                        : LifePMCConfig.SubPointWaitTime.Value;
                    _subWaitEndTime  = Time.time + waitDur;
                    _waitingAtSub    = true;
                    _lastDistLogTime = -999f;
                    SetStatus($"⏸ {_point.ZoneName} [{_subIndex + 1}/{_subs.Count}]");
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ⏸ стоит на саб {_subIndex + 1} " +
                                $"({arrivedSub.Type})  {waitDur:F0}с");
                }
                else
                {
                    // "pass" — просто прошли мимо
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ▶ прошёл саб {_subIndex + 1}");
                    AdvanceSub();
                }
                return;
            }

            SendGoTo(_wanderTarget);
            ApplyWanderPosture();
        }

        /// <summary>
        /// Выбирает случайную NavMesh-позицию в радиусе AUTO_WANDER_RADIUS от основной точки.
        /// Не менее AUTO_WANDER_MIN_R метров от центра, чтобы бот реально двигался.
        /// Возвращает false если не удалось найти позицию за 15 попыток.
        /// </summary>
        private bool TryPickAutoWanderTarget()
        {
            Vector3 center = _point.Position;
            for (int attempt = 0; attempt < 15; attempt++)
            {
                float   angle     = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float   radius    = UnityEngine.Random.Range(AUTO_WANDER_MIN_R, AUTO_WANDER_RADIUS);
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * radius,
                    0f,
                    Mathf.Sin(angle) * radius);

                if (NavMesh.SamplePosition(candidate, out NavMeshHit hit, 5f, NavMesh.AllAreas))
                {
                    _wanderTarget    = hit.position;
                    _wanderTargetSet = true;
                    _lastGoToTime    = -999f;
                    _lastDistLogTime = -999f;
                    _lastPos         = BotOwner.Position;
                    _lastPosTime     = Time.time;

                    float dist = Vector3.Distance(BotOwner.Position, _wanderTarget);
                    SetStatus($"~ {_point.ZoneName}");
                    Log.LogInfo($"[LifePMC] {BotOwner.name} ~ авто-точка  dist={dist:F1}м  " +
                                $"r={radius:F1}м  осталось={(int)(_locationEndTime - Time.time)}с");
                    return true;
                }
            }
            return false;
        }

        /// <summary>Выбираем позицию у текущей саб-точки. Тип влияет на смещение.</summary>
        private void PickWanderTarget()
        {
            var sub = _subs[_subIndex % _subs.Count];

            // "stay" — подходим вплотную (маленькое смещение), "pass" — обходим рядом
            Vector3 offset = sub.IsStay
                ? RandomHorizontalOffset(0.3f, 1.2f)
                : RandomHorizontalOffset(1.5f, 3.5f);

            _wanderTarget    = sub.Position + offset;
            _wanderTargetSet = true;
            _waitingAtSub    = false;
            _lastGoToTime    = -999f;
            _lastDistLogTime = -999f;
            _lastPos         = BotOwner.Position;
            _lastPosTime     = Time.time;

            float dist    = Vector3.Distance(BotOwner.Position, _wanderTarget);
            string icon   = sub.IsStay ? "⏸" : "▶";
            SetStatus($"{icon} {_point.ZoneName} [{_subIndex + 1}/{_subs.Count}]");
            Log.LogInfo($"[LifePMC] {BotOwner.name} {icon} → саб {_subIndex + 1}/{_subs.Count}  " +
                        $"type={sub.Type}  dist={dist:F1}м  осталось={(int)(_locationEndTime - Time.time)}с");
        }

        /// <summary>Переходим к следующей саб-точке (циклически с перетасовкой).</summary>
        private void AdvanceSub()
        {
            _subIndex++;
            if (_subIndex >= _subs.Count)
            {
                _subIndex = 0;
                _subLoopCount++;
                ShuffleList(_subs);
                Log.LogInfo($"[LifePMC] {BotOwner.name} завершил круг {_subLoopCount} по саб-точкам, " +
                            $"осталось={(int)(_locationEndTime - Time.time)}с");
            }
            _wanderTargetSet = false;
        }

        // ────────────────────────────────────────────────────────────────────────
        // Хелперы
        // ────────────────────────────────────────────────────────────────────────

        private void SendGoTo(Vector3 target)
        {
            bool timeExpired = Time.time - _lastGoToTime > GOTO_INTERVAL;
            bool deviated    = Vector3.Distance(_lastGoToTarget, target) > GOTO_DEVIATION;
            if (timeExpired || deviated)
            {
                BotOwner.Mover.GoToPoint(target, false, 1f);
                _lastGoToTime   = Time.time;
                _lastGoToTarget = target;
            }
            if (BotOwner.Mover.IsMoving)
                BotOwner.Steering.LookToMovingDirection();
        }

        /// <summary>Спринт на полной скорости — движение к основной точке.</summary>
        private void ApplySprintPosture()
        {
            if (Time.time - _lastPostureTime > POSTURE_INTERVAL)
            {
                BotOwner.Mover.SetPose(1f);
                BotOwner.Mover.SetTargetMoveSpeed(1f);
                if (!BotOwner.Mover.Sprinting)
                    BotOwner.Mover.Sprint(true);
                _lastPostureTime = Time.time;
            }
        }

        /// <summary>Медленный шаг при блуждании — исследование, лутание.</summary>
        private void ApplyWanderPosture()
        {
            if (Time.time - _lastPostureTime > POSTURE_INTERVAL)
            {
                BotOwner.Mover.SetPose(1f);
                BotOwner.Mover.SetTargetMoveSpeed(0.5f);
                BotOwner.Mover.Sprint(false);
                _lastPostureTime = Time.time;
            }
        }

        /// <summary>
        /// Проверяет застревание. Возвращает true и вызывает Complete если застрял (только Navigate).
        /// </summary>
        private bool CheckStuck(float distToTarget, float reachDist)
        {
            if (Time.time - _lastPosTime < STUCK_INTERVAL) return false;

            float moved  = Vector3.Distance(BotOwner.Position, _lastPos);
            _lastPos     = BotOwner.Position;
            _lastPosTime = Time.time;

            if (moved < STUCK_DIST && distToTarget > reachDist)
            {
                Log.LogWarning($"[LifePMC] {BotOwner.name} ЗАСТРЯЛ! " +
                               $"сдвиг={moved:F2}м за {STUCK_INTERVAL}с  " +
                               $"до цели={distToTarget:F1}м  pos={BotOwner.Position}");
                Complete(wasStuck: true);
                return true;
            }
            return false;
        }

        /// <summary>Случайное горизонтальное смещение — бот подходит РЯДОМ с точкой, не точно на неё.</summary>
        private static Vector3 RandomHorizontalOffset(float minR, float maxR)
        {
            float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
            float r     = UnityEngine.Random.Range(minR, maxR);
            return new Vector3(Mathf.Cos(angle) * r, 0f, Mathf.Sin(angle) * r);
        }

        /// <summary>Вызывает LootingBots.External.ForceBotToScanLoot через reflection (soft dep).</summary>
        private void TryTriggerLootScan()
        {
            if (!_lootingBotsChecked)
            {
                _lootingBotsChecked = true;
                try
                {
                    bool found = false;
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        if (asm.GetName().Name != "skwizzy.LootingBots") continue;
                        found = true;
                        var t = asm.GetType("LootingBots.External");
                        if (t != null)
                        {
                            _forceScanMethod = t.GetMethod("ForceBotToScanLoot",
                                BindingFlags.Public | BindingFlags.Static);
                        }
                        Log.LogInfo(_forceScanMethod != null
                            ? "[LifePMC] LootingBots: ForceBotToScanLoot найден ✓"
                            : "[LifePMC] LootingBots: метод ForceBotToScanLoot не найден в LootingBots.External");
                        break;
                    }
                    if (!found)
                        Log.LogInfo("[LifePMC] LootingBots не установлен — лут-сканирование пропускается");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[LifePMC] LootingBots init error: {ex.Message}");
                }
            }

            if (_forceScanMethod != null)
            {
                try
                {
                    _forceScanMethod.Invoke(null, new object[] { BotOwner });
                    Log.LogInfo($"[LifePMC] {BotOwner.name} → ForceBotToScanLoot вызван");
                }
                catch (Exception ex)
                {
                    Log.LogWarning($"[LifePMC] ForceBotToScanLoot error: {ex.Message}");
                }
            }
        }

        private static void ShuffleList<T>(List<T> list)
        {
            for (int i = list.Count - 1; i > 0; i--)
            {
                int j = UnityEngine.Random.Range(0, i + 1);
                T tmp = list[i]; list[i] = list[j]; list[j] = tmp;
            }
        }

        private void Complete(bool wasStuck = false)
        {
            _phase = Phase.Done;
            try { BotOwner?.Mover?.Sprint(false); } catch { }
            BotStatusMap.Remove(BotOwner.name);
            _layer?.OnObjectiveComplete(wasStuck);
        }

        private void SetStatus(string status)
        {
            BotStatusMap[BotOwner.name] = status;
        }
    }
}
