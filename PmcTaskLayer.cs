using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using EFT.Interactive;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    public enum PmcTaskType
    {
        None     = 0,
        BossHunt = 1,  // охота на босса (до PMC_BOSS_HUNT_MAX ботов на карту)
        EftVisit = 2,  // посетить зону квеста
        EftKill  = 3,  // убить цель (Скавы или босс)
        EftFind  = 4   // найти предмет
    }

    public class PmcTaskData : CustomLayer.ActionData
    {
        public PmcTaskType       TaskType;
        public Vector3           TargetPos;
        public bool              HasTarget;
        // HuntBoss sub-state
        public bool              Searching;
        public float             SearchUntil;
        public Vector3           SearchCenter;
        // EFT квесты
        public string            EftZoneId;
        public EftKillTargetType EftKillTarget;
        public string            EftBossRole;
        public EftFindTask       EftFindRef;
    }

    public class PmcTaskLayer : CustomLayer
    {
        // ── Статика ─────────────────────────────────────────────────────────
        public static readonly Dictionary<int, PmcTaskLayer> LayersByBotId =
            new Dictionary<int, PmcTaskLayer>();

        // Максимум PMC-ботов с BossHunt одновременно
        private const int PMC_BOSS_HUNT_MAX = 3;

        // Lane diversity: zoneIndex → список botId-ов идущих в эту зону
        // Несколько ботов могут идти в одну зону, но каждый получает свой lane
        // (боковое смещение цели), чтобы NavMesh прокладывал разные маршруты
        private static readonly Dictionary<int, List<int>> _bossZoneAssignments =
            new Dictionary<int, List<int>>();

        // Боковое смещение на маршруте: lane 0 = центр, 1 = +LANE_OFFSET вбок, 2 = -LANE_OFFSET, …
        private const float LANE_OFFSET = 12f;

        // Кеш мин и лимит пути (дублируется из ScavTaskLayer — instance-независимо)
        private static MineDirectional[] _cachedMines    = null;
        private static float             _minesCheckTime = 0f;
        private const  float             MINES_CACHE_TTL = 60f;
        private const  float             MINE_DANGER_SQR = 16f;
        private const  float             MAX_PATH_LENGTH = 1500f;

        // Переиспользуемый объект NavMeshPath — избегаем GC-аллокаций в IsPathSafe
        private static readonly NavMeshPath _sharedNavPath = new NavMeshPath();

        // SAIN CanBotQuest reflection
        private static MethodInfo _canBotQuestMethod  = null;
        private static bool       _sainReflectionDone = false;

        // ── Состояние экземпляра ─────────────────────────────────────────────
        private PmcTaskType _task         = PmcTaskType.None;
        private bool        _taskComplete = false;
        private float       _cooldownUntil    = 0f;
        private Vector3     _cachedTargetPos;
        private bool        _hasCachedTarget  = false;
        private float       _nextTargetUpdate = 0f;
        private const float TARGET_UPDATE_INT = 2f;
        private const float REACH_DIST        = 10f;

        private float       _taskTimeout  = 0f;
        private const float TASK_TIMEOUT  = 90f;

        // Сколько секунд нет цели — досрочный сброс задания
        private float       _noTargetSince    = 0f;
        private bool        _noTargetTracking = false;
        private const float NO_TARGET_ABANDON = 20f;

        // Текущие EFT задания
        private EftVisitTask _currentEftVisit = null;
        private EftKillTask  _currentEftKill  = null;
        private EftFindTask  _currentEftFind  = null;

        // BossHunt: подтверждение убийства через OnPlayerDead
        private bool                  _bossKillConfirmed = false;
        private readonly List<Player> _trackedBosses     = new List<Player>();
        private int                   _assignedZoneIndex = -1;
        private int                   _laneIndex         = 0;  // 0=центр, 1=+сторона, 2=-сторона

        // EftKill: подтверждение убийства Скава через OnPlayerDead
        private bool                  _killConfirmed  = false;
        private readonly List<Player> _trackedTargets = new List<Player>();

        // ── Конструктор ──────────────────────────────────────────────────────
        public PmcTaskLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            LayersByBotId[botOwner.Id] = this;
        }

        public override string GetName() => "PmcTask";

        // Доступ из оверлея
        public PmcTaskType CurrentTask    => _task;
        public bool        IsInCooldown   => Time.time < _cooldownUntil;
        public float       CooldownRemain => Mathf.Max(0f, _cooldownUntil - Time.time);
        public Vector3     CachedTarget   => _hasCachedTarget ? _cachedTargetPos : Vector3.zero;

        // ── IsActive ─────────────────────────────────────────────────────────
        public override bool IsActive()
        {
            if (BotOwner == null || BotOwner.IsDead) return false;

            // BossHunt: убийство босса/свиты подтверждено — завершаем
            if (_task == PmcTaskType.BossHunt && _bossKillConfirmed)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] BossHunt: boss/guard kill confirmed → complete");
                MarkTaskComplete();
                return false;
            }

            // EftKill: убийство цели подтверждено
            if (_task == PmcTaskType.EftKill && _killConfirmed)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] EftKill: kill confirmed → complete");
                MarkTaskComplete();
                return false;
            }

            if (IsBotInCombat())
            {
                // Продлеваем таймаут пока бот в бою
                if (_task == PmcTaskType.EftKill || _task == PmcTaskType.BossHunt)
                    _taskTimeout = Time.time + TASK_TIMEOUT;
                return false;
            }

            if (BotOwner.Medecine.FirstAid.Have2Do ||
                BotOwner.Medecine.SurgicalKit.HaveWork)
                return false;

            if (Time.time < _cooldownUntil) return false;

            // Берём новое задание если нет или завершено
            if (_task == PmcTaskType.None || _taskComplete)
            {
                _task             = PickBestTask();
                _taskComplete     = false;
                _hasCachedTarget  = false;
                _taskTimeout      = Time.time + TASK_TIMEOUT;
                _noTargetTracking = false;

                if (_task == PmcTaskType.None)
                {
                    _cooldownUntil = Time.time + 5f;
                    return false;
                }

                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] assigned task: {_task}");
            }

            // Таймаут — задание зависло
            if (_taskTimeout > 0f && Time.time > _taskTimeout)
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[PmcTask] [{BotOwner.Id}] task {_task} timed out → force complete");
                MarkTaskComplete();
                return false;
            }

            Vector3 foundTarget;
            if (!TryFindTarget(out foundTarget))
            {
                if (!_noTargetTracking)
                {
                    _noTargetTracking = true;
                    _noTargetSince    = Time.time;
                }
                else if (Time.time - _noTargetSince > NO_TARGET_ABANDON)
                {
                    ScavTaskPlugin.Log.LogWarning(
                        $"[PmcTask] [{BotOwner.Id}] task {_task}: no target for {NO_TARGET_ABANDON}s → abandon");
                    MarkTaskComplete();
                }
                return false;
            }
            _noTargetTracking = false;

            return true;
        }

        // ── GetNextAction ─────────────────────────────────────────────────────
        public override CustomLayer.Action GetNextAction()
        {
            Vector3 target;
            if (!TryFindTarget(out target))
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[PmcTask] [{BotOwner.Id}] GetNextAction: no target → force complete");
                MarkTaskComplete();
                return new CustomLayer.Action(typeof(PmcTaskLogic), "NoTarget",
                    new PmcTaskData { TaskType = _task, HasTarget = false });
            }

            var ad = new PmcTaskData { TaskType = _task, TargetPos = target, HasTarget = true };
            if (_task == PmcTaskType.EftKill && _currentEftKill != null)
            {
                ad.EftKillTarget = _currentEftKill.TargetType;
                ad.EftBossRole   = _currentEftKill.BossRole;
            }
            else if (_task == PmcTaskType.EftFind && _currentEftFind != null)
            {
                ad.EftFindRef = _currentEftFind;
            }
            return new CustomLayer.Action(typeof(PmcTaskLogic), _task.ToString(), ad);
        }

        public override bool IsCurrentActionEnding() => _taskComplete;

        // ── MarkTaskComplete ──────────────────────────────────────────────────
        public void MarkTaskComplete()
        {
            // Освобождаем lane в зоне BossHunt
            if (_task == PmcTaskType.BossHunt && _assignedZoneIndex >= 0)
            {
                List<int> bots;
                if (_bossZoneAssignments.TryGetValue(_assignedZoneIndex, out bots))
                {
                    bots.Remove(BotOwner.Id);
                    if (bots.Count == 0) _bossZoneAssignments.Remove(_assignedZoneIndex);
                }
                _assignedZoneIndex = -1;
                _laneIndex         = 0;
            }

            StopBossTracking();
            StopKillTracking();

            EftQuestTaskManager.ReleasePmcTasksForBot(BotOwner.Id);
            _currentEftVisit = null;
            _currentEftKill  = null;
            _currentEftFind  = null;

            _taskComplete    = true;
            _task            = PmcTaskType.None;
            _hasCachedTarget = false;
            float cd = UnityEngine.Random.Range(10f, 30f);
            _cooldownUntil   = Time.time + cd;
            ScavTaskPlugin.Log.LogInfo(
                $"[PmcTask] [{BotOwner.Id}] task complete, cooldown {cd:F0}s");
        }

        // ── TryFindTarget ─────────────────────────────────────────────────────
        public bool TryFindTarget(out Vector3 target)
        {
            if (_hasCachedTarget && Time.time < _nextTargetUpdate)
            {
                target = _cachedTargetPos;
                return true;
            }

            bool   found = false;
            target = Vector3.zero;

            switch (_task)
            {
                case PmcTaskType.BossHunt: found = TryGetBossHuntTarget(out target); break;
                case PmcTaskType.EftVisit: found = TryGetEftVisitTarget(out target); break;
                case PmcTaskType.EftKill:  found = TryGetEftKillTarget(out target);  break;
                case PmcTaskType.EftFind:  found = TryGetEftFindTarget(out target);  break;
            }

            if (found)
            {
                _cachedTargetPos  = target;
                _hasCachedTarget  = true;
                _nextTargetUpdate = Time.time + TARGET_UPDATE_INT;
            }
            else
            {
                _hasCachedTarget = false;
            }
            return found;
        }

        // ── PickBestTask ──────────────────────────────────────────────────────
        private PmcTaskType PickBestTask()
        {
            Vector3 tmp;
            int alive = CountAlivePmcsInLayer();

            // BossHunt: минимум 1, максимум PMC_BOSS_HUNT_MAX (3).
            // При alive=1 → cap=1, alive=2 → cap=1, alive=3-5 → cap=2, alive=6+ → cap=3.
            int bossHuntActive = CountActiveTaskType(PmcTaskType.BossHunt);
            int bossHuntCap    = Mathf.Clamp(1 + (alive - 1) / 3, 1, PMC_BOSS_HUNT_MAX);

            // Остальные типы: по одному слоту на каждые ~3 живых ботов
            int visitCap = Mathf.Max(1, alive / 3);
            int killCap  = Mathf.Max(1, alive / 3);
            int findCap  = Mathf.Max(1, alive / 3);

            int visitActive = CountActiveTaskType(PmcTaskType.EftVisit);
            int killActive  = CountActiveTaskType(PmcTaskType.EftKill);
            int findActive  = CountActiveTaskType(PmcTaskType.EftFind);

            // BossHunt — наивысший приоритет, всегда не менее 1 бота
            if (!ScavTaskLayer.NoBossOnMap && bossHuntActive < bossHuntCap)
            {
                _task = PmcTaskType.BossHunt;
                if (TryGetBossHuntTarget(out tmp)) return PmcTaskType.BossHunt;
            }

            if (EftQuestTaskManager.IsInitialized)
            {
                // Выбираем тип с наименьшей заполненностью относительно своей квоты,
                // чтобы равномерно распределить ботов по разным заданиям.
                // Порядок сравнения: наименее «заполненный» тип идёт первым.
                // Ratio = active / cap; меньше → приоритетнее.

                var order = new (PmcTaskType type, float ratio)[]
                {
                    (PmcTaskType.EftVisit, visitActive  / (float)visitCap),
                    (PmcTaskType.EftKill,  killActive   / (float)killCap),
                    (PmcTaskType.EftFind,  findActive   / (float)findCap),
                };
                System.Array.Sort(order, (a, b) => a.ratio.CompareTo(b.ratio));

                foreach (var entry in order)
                {
                    _task = entry.type;
                    switch (entry.type)
                    {
                        case PmcTaskType.EftVisit:
                            if (visitActive < visitCap && TryGetEftVisitTarget(out tmp))
                                return PmcTaskType.EftVisit;
                            break;
                        case PmcTaskType.EftKill:
                            if (killActive < killCap && TryGetEftKillTarget(out tmp))
                                return PmcTaskType.EftKill;
                            break;
                        case PmcTaskType.EftFind:
                            if (findActive < findCap && TryGetEftFindTarget(out tmp))
                                return PmcTaskType.EftFind;
                            break;
                    }
                }

                // Фолбэк: берём любое доступное задание без квоты
                _task = PmcTaskType.EftVisit;
                if (TryGetEftVisitTarget(out tmp)) return PmcTaskType.EftVisit;
                _task = PmcTaskType.EftKill;
                if (TryGetEftKillTarget(out tmp)) return PmcTaskType.EftKill;
                _task = PmcTaskType.EftFind;
                if (TryGetEftFindTarget(out tmp)) return PmcTaskType.EftFind;
            }

            _task = PmcTaskType.None;
            return PmcTaskType.None;
        }

        // ── BossHunt: несколько ботов могут идти в одну зону, но разными путями ─
        // Каждый бот получает lane index → боковое смещение цели → разный NavMesh-маршрут
        private bool TryGetBossHuntTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (ScavTaskLayer.NoBossOnMap) return false;

            ScavTaskLayer.EnsureBossZonesInitialized();
            var zones = ScavTaskLayer.GetBossZones();
            if (zones == null || zones.Count == 0) return false;

            // Если уже назначена зона и она ещё не обыскана — возвращаем цель с тем же lane
            if (_assignedZoneIndex >= 0 && _assignedZoneIndex < zones.Count)
            {
                if (!ScavTaskLayer.IsZoneSearched(zones[_assignedZoneIndex]))
                {
                    pos = ComputeLaneTarget(zones[_assignedZoneIndex]);
                    return pos != Vector3.zero;
                }
                // Зона обыскана — освобождаем lane и ищем новую
                ReleaseLane(_assignedZoneIndex);
                _assignedZoneIndex = -1;
                _laneIndex         = 0;
            }

            // Чистим мёртвые записи
            CleanBossZoneAssignments();

            bool    anyUnsearched = false;
            Vector3 nearest       = Vector3.zero;
            float   nearestDist   = float.MaxValue;
            int     nearestIdx    = -1;

            // Шаг 1: находим ближайшую необысканную зону ТОЛЬКО по дистанции (без NavMesh.CalculatePath)
            for (int i = 0; i < zones.Count; i++)
            {
                if (ScavTaskLayer.IsZoneSearched(zones[i])) continue;
                anyUnsearched = true;

                NavMeshHit hit;
                Vector3 snapPos = NavMesh.SamplePosition(zones[i], out hit, 15f, NavMesh.AllAreas)
                    ? hit.position : zones[i];

                float d = Vector3.Distance(BotOwner.Position, snapPos);
                if (d < nearestDist) { nearestDist = d; nearest = snapPos; nearestIdx = i; }
            }

            if (!anyUnsearched) { ScavTaskLayer.NoBossOnMap = true; return false; }
            if (nearest == Vector3.zero) return false;

            // Шаг 2: IsPathSafe только для выбранной зоны (было N вызовов — стало 1)
            if (!IsPathSafe(nearest)) return false;

            // Назначаем lane: позиция в списке ботов этой зоны
            List<int> zoneBots;
            if (!_bossZoneAssignments.TryGetValue(nearestIdx, out zoneBots))
            {
                zoneBots = new List<int>();
                _bossZoneAssignments[nearestIdx] = zoneBots;
            }
            if (!zoneBots.Contains(BotOwner.Id))
                zoneBots.Add(BotOwner.Id);

            _assignedZoneIndex = nearestIdx;
            _laneIndex         = zoneBots.IndexOf(BotOwner.Id);

            if (_trackedBosses.Count == 0)
                StartBossTracking();

            pos = ComputeLaneTarget(zones[nearestIdx]);
            return pos != Vector3.zero;
        }

        // Вычисляем смещённую цель на основе lane index бота:
        //   lane 0 → центр зоны
        //   lane 1 → +LANE_OFFSET вбок
        //   lane 2 → -LANE_OFFSET вбок
        //   lane 3 → +2*LANE_OFFSET вбок, и т.д.
        private Vector3 ComputeLaneTarget(Vector3 zoneCenter)
        {
            NavMeshHit baseHit;
            Vector3 basePos = NavMesh.SamplePosition(zoneCenter, out baseHit, 15f, NavMesh.AllAreas)
                ? baseHit.position : zoneCenter;

            if (_laneIndex == 0) return basePos;

            // Боковое направление: перпендикуляр к вектору бот→зона в горизонтальной плоскости
            Vector3 toZone = (basePos - BotOwner.Position);
            toZone.y = 0f;
            float len = toZone.magnitude;
            Vector3 forward = len > 0.1f ? toZone / len : Vector3.forward;
            Vector3 right   = Vector3.Cross(Vector3.up, forward).normalized;

            // lane 1 → +right, lane 2 → -right, lane 3 → +2*right, …
            float sign  = (_laneIndex % 2 == 1) ? 1f : -1f;
            float mult  = Mathf.Ceil(_laneIndex / 2f);
            Vector3 offset = right * (sign * mult * LANE_OFFSET);

            Vector3 candidate = basePos + offset;
            NavMeshHit offsetHit;
            if (NavMesh.SamplePosition(candidate, out offsetHit, LANE_OFFSET, NavMesh.AllAreas))
                return offsetHit.position;

            return basePos; // если смещение не на NavMesh — идём по центру
        }

        private void ReleaseLane(int zoneIdx)
        {
            List<int> bots;
            if (_bossZoneAssignments.TryGetValue(zoneIdx, out bots))
            {
                bots.Remove(BotOwner.Id);
                if (bots.Count == 0) _bossZoneAssignments.Remove(zoneIdx);
            }
        }

        private static void CleanBossZoneAssignments()
        {
            var deadZones = new List<int>();
            foreach (var kv in _bossZoneAssignments)
            {
                kv.Value.RemoveAll(botId =>
                {
                    PmcTaskLayer layer;
                    return !LayersByBotId.TryGetValue(botId, out layer)
                        || layer.BotOwner == null || layer.BotOwner.IsDead;
                });
                if (kv.Value.Count == 0) deadZones.Add(kv.Key);
            }
            foreach (var k in deadZones) _bossZoneAssignments.Remove(k);
        }

        // Подписка на смерти боссов и свиты (любая boss-роль)
        private void StartBossTracking()
        {
            StopBossTracking();
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.AllAlivePlayersList == null) return;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.IsAI) continue;
                if (!player.HealthController.IsAlive) continue;
                var bo = player.AIData?.BotOwner;
                if (bo == null) continue;
                var role = bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!ScavTaskLayer.IsBossRole(role)) continue;
                player.OnPlayerDead += OnTrackedBossDead;
                _trackedBosses.Add(player);
            }
            ScavTaskPlugin.Log.LogInfo(
                $"[PmcTask] [{BotOwner.Id}] BossHunt: tracking {_trackedBosses.Count} boss/guard targets");
        }

        private void StopBossTracking()
        {
            foreach (var p in _trackedBosses)
                if (p != null) p.OnPlayerDead -= OnTrackedBossDead;
            _trackedBosses.Clear();
            _bossKillConfirmed = false;
        }

        private void OnTrackedBossDead(Player player, IPlayer lastAggressor,
            DamageInfoStruct dmg, EBodyPart part)
        {
            if (player != null) player.OnPlayerDead -= OnTrackedBossDead;
            _trackedBosses.Remove(player);

            if (lastAggressor?.ProfileId == BotOwner.ProfileId)
            {
                _bossKillConfirmed = true;
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] BossHunt: killed '{player?.name}' → quest complete");
            }

            // Если убиты ВСЕ отслеживаемые боссы/свита — квест выполнен
            if (_trackedBosses.Count == 0 && !_bossKillConfirmed)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] BossHunt: all tracked bosses dead → complete");
                _bossKillConfirmed = true;
            }
        }

        // ── EftVisit ──────────────────────────────────────────────────────────
        private bool TryGetEftVisitTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftVisit == null)
            {
                if (!EftQuestTaskManager.TryGetVisitTaskForPmc(
                    BotOwner.Position, BotOwner.Id, out _currentEftVisit))
                    return false;

                // Проверяем доступность зоны
                NavMeshHit checkHit;
                Vector3 checkPos = NavMesh.SamplePosition(
                    _currentEftVisit.ZonePosition, out checkHit, 15f, NavMesh.AllAreas)
                    ? checkHit.position : _currentEftVisit.ZonePosition;

                if (!IsPathSafe(checkPos))
                {
                    EftQuestTaskManager.BlockedVisitZoneIds.Add(_currentEftVisit.ZoneId);
                    EftQuestTaskManager.ReleasePmcTasksForBot(BotOwner.Id);
                    _currentEftVisit = null;
                    return false;
                }
            }

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(
                _currentEftVisit.ZonePosition, out hit, 15f, NavMesh.AllAreas)
                ? hit.position : _currentEftVisit.ZonePosition;
            return pos != Vector3.zero;
        }

        // ── EftKill ───────────────────────────────────────────────────────────
        private bool TryGetEftKillTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftKill == null)
            {
                if (!EftQuestTaskManager.TryGetKillTaskForPmc(BotOwner.Id, out _currentEftKill))
                    return false;
                StartKillTracking();
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] EftKill: quest='{_currentEftKill.QuestName}'" +
                    $" type={_currentEftKill.TargetType}");
            }

            // Квест «убить босса» → идём к зоне спавна босса
            if (_currentEftKill.TargetType == EftKillTargetType.Boss)
                return TryGetBossHuntTarget(out pos);

            // Квест «убить» (любой тип, кроме босса) → PMC идёт охотиться на Скавов
            return TryGetDynamicScavTarget(out pos);
        }

        // Находим ближайшего живого Scav-бота
        private bool TryGetDynamicScavTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.AllAlivePlayersList == null) return false;

            Player nearest    = null;
            float nearestDist = float.MaxValue;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.IsAI) continue;
                if (!player.HealthController.IsAlive) continue;
                var bo = player.AIData?.BotOwner;
                if (bo == null) continue;
                var role = bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!IsScavRole(role)) continue;

                float d = Vector3.Distance(BotOwner.Position, player.Position);
                if (d < nearestDist) { nearestDist = d; nearest = player; }
            }

            if (nearest == null) return false;

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(nearest.Position, out hit, 10f, NavMesh.AllAreas)
                ? hit.position : nearest.Position;
            return pos != Vector3.zero;
        }

        // Подписка на смерти Скав-целей (EftKill)
        private void StartKillTracking()
        {
            StopKillTracking();
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.AllAlivePlayersList == null) return;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.IsAI) continue;
                if (!player.HealthController.IsAlive) continue;
                var bo = player.AIData?.BotOwner;
                if (bo == null) continue;
                var role = bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!IsScavRole(role)) continue;
                player.OnPlayerDead += OnTrackedTargetDead;
                _trackedTargets.Add(player);
            }
            ScavTaskPlugin.Log.LogInfo(
                $"[PmcTask] [{BotOwner.Id}] EftKill: tracking {_trackedTargets.Count} Scav targets");
        }

        private void StopKillTracking()
        {
            foreach (var p in _trackedTargets)
                if (p != null) p.OnPlayerDead -= OnTrackedTargetDead;
            _trackedTargets.Clear();
            _killConfirmed = false;
        }

        private void OnTrackedTargetDead(Player player, IPlayer lastAggressor,
            DamageInfoStruct dmg, EBodyPart part)
        {
            if (player != null) player.OnPlayerDead -= OnTrackedTargetDead;
            _trackedTargets.Remove(player);

            if (lastAggressor?.ProfileId == BotOwner.ProfileId)
            {
                _killConfirmed = true;
                ScavTaskPlugin.Log.LogInfo(
                    $"[PmcTask] [{BotOwner.Id}] EftKill: killed '{player?.name}' → quest complete");
            }
        }

        // ── EftFind ───────────────────────────────────────────────────────────
        private bool TryGetEftFindTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftFind == null)
            {
                if (!EftQuestTaskManager.TryGetFindTaskForPmc(
                    BotOwner.Position, BotOwner.Id, out _currentEftFind))
                    return false;
            }

            EftQuestTaskManager.RefreshLootPositions();
            if (_currentEftFind.LootPositions.Count == 0) return false;

            Vector3 nearest    = Vector3.zero;
            float   nearestDst = float.MaxValue;
            foreach (var lp in _currentEftFind.LootPositions)
            {
                float d = Vector3.Distance(BotOwner.Position, lp);
                if (d < nearestDst) { nearestDst = d; nearest = lp; }
            }
            if (nearest == Vector3.zero) return false;

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(nearest, out hit, 15f, NavMesh.AllAreas)
                ? hit.position : nearest;
            return pos != Vector3.zero;
        }

        // ── Счётчики ──────────────────────────────────────────────────────────
        private static int CountAlivePmcsInLayer()
        {
            int n = 0;
            foreach (var kv in LayersByBotId)
                if (kv.Value?.BotOwner != null && !kv.Value.BotOwner.IsDead) n++;
            return Mathf.Max(1, n);
        }

        private static int CountActiveTaskType(PmcTaskType type)
        {
            int n = 0;
            foreach (var kv in LayersByBotId)
            {
                if (kv.Value?.BotOwner == null || kv.Value.BotOwner.IsDead) continue;
                if (kv.Value._task == type && !kv.Value._taskComplete) n++;
            }
            return n;
        }

        // ── IsPathSafe (дублируется из ScavTaskLayer) ─────────────────────────
        private bool IsPathSafe(Vector3 dest)
        {
            if (!NavMesh.CalculatePath(BotOwner.Position, dest, NavMesh.AllAreas, _sharedNavPath))
                return false;
            if (_sharedNavPath.status != NavMeshPathStatus.PathComplete) return false;

            var corners  = _sharedNavPath.corners;
            float totalLen = 0f;
            for (int i = 1; i < corners.Length; i++)
                totalLen += Vector3.Distance(corners[i - 1], corners[i]);
            if (totalLen > MAX_PATH_LENGTH) return false;

            if (_cachedMines == null || Time.time > _minesCheckTime)
            {
                try   { _cachedMines = UnityEngine.Object.FindObjectsOfType<MineDirectional>(); }
                catch { _cachedMines = new MineDirectional[0]; }
                _minesCheckTime = Time.time + MINES_CACHE_TTL;
            }

            if (_cachedMines != null)
                foreach (var mine in _cachedMines)
                {
                    if (mine == null) continue;
                    Vector3 minePos = mine.transform.position;
                    for (int i = 1; i < corners.Length; i++)
                        if (DistSqrPointSegment(minePos, corners[i - 1], corners[i]) < MINE_DANGER_SQR)
                            return false;
                }
            return true;
        }

        private static float DistSqrPointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab    = b - a;
            float   lenSqr = ab.sqrMagnitude;
            if (lenSqr < 0.0001f) return (p - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr);
            return (p - (a + t * ab)).sqrMagnitude;
        }

        // ── IsBotInCombat (дублируется из ScavTaskLayer) ─────────────────────
        private static void InitSainReflection()
        {
            if (_sainReflectionDone) return;
            _sainReflectionDone = true;
            try
            {
                var t = Type.GetType("SAIN.Plugin.External, SAIN");
                _canBotQuestMethod = t?.GetMethod("CanBotQuest",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch { }
        }

        private bool IsBotInCombat()
        {
            InitSainReflection();
            if (_canBotQuestMethod != null)
            {
                try
                {
                    var result = _canBotQuestMethod.Invoke(
                        null, new object[] { BotOwner, BotOwner.Position, 0.33f });
                    if (result is bool canQuest) return !canQuest;
                }
                catch { }
            }
            try
            {
                var mem = BotOwner.Memory;
                if (mem == null) return false;
                // Враг виден или был виден недавно (расширено до 30 сек вместо 10)
                if (mem.GoalEnemy != null &&
                    Time.time - mem.GoalEnemy.TimeLastSeen < 30f)                   return true;
                if (mem.IsUnderFire)                                                return true;
            }
            catch { }
            return false;
        }

        // ── Роли ─────────────────────────────────────────────────────────────
        // Всё что не PMC и не босс — считается Скавом (цель PMC при EftKill)
        private static bool IsScavRole(WildSpawnType role)
        {
            string n = role.ToString();
            return !n.StartsWith("pmc", StringComparison.OrdinalIgnoreCase)
                && !n.Equals("pmcBot",       StringComparison.OrdinalIgnoreCase)
                && !n.Equals("ExUsec",       StringComparison.OrdinalIgnoreCase)
                && !n.Equals("ArenaFighter", StringComparison.OrdinalIgnoreCase)
                && !ScavTaskLayer.IsBossRole(role);
        }

        // ── Сброс при начале нового рейда ─────────────────────────────────────
        public static void Reset()
        {
            _bossZoneAssignments.Clear();
            foreach (var kv in LayersByBotId)
            {
                if (kv.Value == null) continue;
                kv.Value._assignedZoneIndex = -1;
                kv.Value._laneIndex         = 0;
                kv.Value._bossKillConfirmed = false;
                kv.Value._killConfirmed     = false;
            }
        }
    }
}
