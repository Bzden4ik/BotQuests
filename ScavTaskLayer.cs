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
    public enum ScavTaskType
    {
        None          = 0,
        SpawnRush     = 1,  // идти к месту спавна игрока и убить его при встрече
        HuntBoss      = 2,  // обыскивать споты спавна босса
        CheckPMCSpawn = 3,  // проверить ближайший спавн PMC
        EftVisit      = 4,  // реальный квест EFT: посетить зону
        EftKill       = 5,  // реальный квест EFT: убить PMC или босса
        EftFind       = 6   // реальный квест EFT: найти предмет
    }

    public class ScavTaskData : CustomLayer.ActionData
    {
        public ScavTaskType TaskType;
        public Vector3      TargetPos;
        public bool         HasTarget;
        // Для HuntBoss — sub-state
        public bool         Searching;
        public float        SearchUntil;
        public Vector3      SearchCenter;

        // EftVisit — id квестовой зоны
        public string            EftZoneId;
        // EftKill — тип цели
        public EftKillTargetType EftKillTarget;
        public string            EftBossRole;
        // EftFind — ссылка на задачу (позиции лута)
        public EftFindTask       EftFindRef;
    }

    public class ScavTaskLayer : CustomLayer
    {
        // ── Статические данные общие для всех ботов ────────────────────
        public static readonly Dictionary<int, ScavTaskLayer> LayersByBotId =
            new Dictionary<int, ScavTaskLayer>();

        // Позиция спавна главного игрока (захватывается в начале рейда)
        public static Vector3 PlayerSpawnPos;
        public static bool    PlayerSpawnKnown = false;

        // Глобальный реестр обысканных зон босса: (позиция, время истечения)
        private static readonly List<(Vector3 pos, float expiry)> _searchedBossAreas =
            new List<(Vector3, float)>();
        private const float SearchedAreaRadius  = 40f;  // радиус "обысканной" зоны
        private const float SearchedAreaExpiry  = 300f; // через 5 мин снова считается необысканной

        // Только один бот одновременно может иметь SpawnRush
        private static int _spawnRushOwnerBotId = -1;

        // SAIN API — CanBotQuest (инициализируется один раз)
        private static MethodInfo _canBotQuestMethod  = null;
        private static bool       _sainReflectionDone = false;

        // HuntBoss: зоны спавна боссов (инициализируется один раз за рейд)
        private static List<Vector3> _bossSpawnZones = null;
        public  static bool          NoBossOnMap     = false;

        // Кеш статических мин для проверки безопасности пути
        private static MineDirectional[] _cachedMines    = null;
        private static float             _minesCheckTime = 0f;
        private const  float             MINES_CACHE_TTL = 60f;
        private const  float             MINE_DANGER_SQR = 16f;  // 4 м radius
        private const  float             MAX_PATH_LENGTH = 1500f; // макс длина маршрута в метрах

        // Переиспользуемый объект NavMeshPath — избегаем GC-аллокаций в IsPathSafe
        private static readonly NavMeshPath _sharedNavPath = new NavMeshPath();

        // ── История задач (для оверлея) ──────────────────────────────
        // Текстовая история выполненных заданий (самое свежее — первое)
        public static readonly List<string> TaskHistoryLines = new List<string>();
        private const int MAX_HISTORY = 20;

        // ── Состояние слоя ─────────────────────────────────────────────
        private ScavTaskType _task          = ScavTaskType.None;
        private bool         _taskComplete  = false;
        private float        _cooldownUntil = 0f;
        private Vector3      _cachedTargetPos;
        private bool         _hasCachedTarget   = false;
        private float        _nextTargetUpdate  = 0f;
        private const float  TARGET_UPDATE_INT  = 2f;
        private const float  REACH_DIST         = 10f;

        // Фиксированная цель для CheckPMCSpawn — не двигается после назначения задания
        private bool    _hasFixedTarget = false;
        private Vector3 _fixedTarget;
        // Таймаут: если задание висит дольше N секунд — принудительно завершить
        private float   _taskTimeout    = 0f;
        private const float TASK_TIMEOUT = 90f;

        // Если TryFindTarget возвращает false дольше этого — досрочно бросаем задание
        private float _noTargetSince     = 0f;
        private bool  _noTargetTracking  = false;
        private const float NO_TARGET_ABANDON = 20f;

        // ── Текущие EFT квест-задания (одно на тип, null = не назначено) ──
        private EftVisitTask _currentEftVisit = null;
        private EftKillTask  _currentEftKill  = null;
        private EftFindTask  _currentEftFind  = null;

        // EftKill: подтверждение убийства через Player.OnPlayerDead
        private bool              _killConfirmed = false;
        private readonly List<Player> _trackedPMCs = new List<Player>();

        // ── BigBrain init ──────────────────────────────────────────────
        public ScavTaskLayer(BotOwner botOwner, int priority) : base(botOwner, priority)
        {
            LayersByBotId[botOwner.Id] = this;
        }

        public override string GetName() => "ScavTask";

        // Оверлей читает напрямую без вызова IsActive()
        public ScavTaskType CurrentTask    => _task;
        public bool         IsInCooldown   => Time.time < _cooldownUntil;
        public float        CooldownRemain => Mathf.Max(0f, _cooldownUntil - Time.time);
        // Текущая кешированная цель (для телепорта камеры в оверлее)
        public Vector3      CachedTarget   => _hasCachedTarget ? _cachedTargetPos : Vector3.zero;

        // ── IsActive ───────────────────────────────────────────────────
        public override bool IsActive()
        {
            if (BotOwner == null || BotOwner.IsDead) return false;

            // EftKill: если убийство подтверждено — завершаем задание сразу (даже если были в бою)
            if (_task == ScavTaskType.EftKill && _killConfirmed)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] EftKill: kill confirmed → completing");
                MarkTaskComplete();
                return false;
            }

            if (IsBotInCombat())
            {
                // EftKill: сбрасываем таймаут пока бот в бою — квест не должен истечь во время боя
                if (_task == ScavTaskType.EftKill)
                    _taskTimeout = Time.time + TASK_TIMEOUT;
                return false;
            }

            if (BotOwner.Medecine.FirstAid.Have2Do ||
                BotOwner.Medecine.SurgicalKit.HaveWork)
                return false;

            if (Time.time < _cooldownUntil) return false;

            // Берём новое задание только если старое завершено или его нет
            if (_task == ScavTaskType.None || _taskComplete)
            {
                if (_task == ScavTaskType.SpawnRush &&
                    _spawnRushOwnerBotId == BotOwner.Id)
                    _spawnRushOwnerBotId = -1;

                _task            = PickBestTask();
                _taskComplete    = false;
                _hasCachedTarget = false;
                _hasFixedTarget  = false;   // сброс фиксированной цели при новом задании
                _taskTimeout     = Time.time + TASK_TIMEOUT;
                _noTargetTracking = false;  // сброс счётчика отсутствия цели

                if (_task == ScavTaskType.None)
                {
                    _cooldownUntil = Time.time + 5f;  // не спамим каждый кадр если нет задач
                    return false;
                }

                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] assigned task: {_task}");
            }

            // Таймаут — задание зависло, принудительно сбрасываем
            if (_taskTimeout > 0f && Time.time > _taskTimeout)
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[ScavTaskMod] [{BotOwner.Id}] task {_task} timed out after {TASK_TIMEOUT}s → force complete");
                MarkTaskComplete();
                return false;
            }

            Vector3 foundTarget;
            if (!TryFindTarget(out foundTarget))
            {
                // Досрочно бросаем задание если нет цели дольше NO_TARGET_ABANDON секунд.
                // Это предотвращает зависание бота на месте при EftKill/EftFind без целей.
                if (!_noTargetTracking)
                {
                    _noTargetTracking = true;
                    _noTargetSince    = Time.time;
                }
                else if (Time.time - _noTargetSince > NO_TARGET_ABANDON)
                {
                    ScavTaskPlugin.Log.LogWarning(
                        $"[ScavTaskMod] [{BotOwner.Id}] task {_task}: no target for {NO_TARGET_ABANDON}s → abandon");
                    MarkTaskComplete();
                }
                return false;
            }
            _noTargetTracking = false;  // цель найдена — сбрасываем счётчик

            return true;
        }

        // ── GetNextAction ──────────────────────────────────────────────
        public override CustomLayer.Action GetNextAction()
        {
            Vector3 target;
            if (!TryFindTarget(out target))
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[ScavTaskMod] [{BotOwner.Id}] GetNextAction: no target, completing task");
                MarkTaskComplete();
                return new CustomLayer.Action(typeof(ScavTaskLogic), "NoTarget",
                    new ScavTaskData { TaskType = _task, HasTarget = false });
            }

            var actionData = new ScavTaskData { TaskType = _task, TargetPos = target, HasTarget = true };
            if (_task == ScavTaskType.EftKill && _currentEftKill != null)
            {
                actionData.EftKillTarget = _currentEftKill.TargetType;
                actionData.EftBossRole   = _currentEftKill.BossRole;
            }
            else if (_task == ScavTaskType.EftFind && _currentEftFind != null)
            {
                actionData.EftFindRef = _currentEftFind;
            }
            return new CustomLayer.Action(typeof(ScavTaskLogic), _task.ToString(), actionData);
        }

        // ── IsCurrentActionEnding ─────────────────────────────────────
        public override bool IsCurrentActionEnding()
        {
            if (_taskComplete)       return true;
            if (IsBotInCombat())     return true;

            Vector3 target;
            if (!TryFindTarget(out target))
            {
                ScavTaskPlugin.Log.LogWarning(
                    $"[ScavTaskMod] [{BotOwner.Id}] ActionEnding: TryFindTarget failed → completing task={_task}");
                MarkTaskComplete();
                return true;
            }

            float dist = Vector3.Distance(BotOwner.Position, target);
            // EftVisit/EftFind/EftKill завершаются из Logic после отработки таймера/поиска
            bool distCompletes = _task != ScavTaskType.HuntBoss
                              && _task != ScavTaskType.EftVisit
                              && _task != ScavTaskType.EftFind
                              && _task != ScavTaskType.EftKill;
            if (dist <= REACH_DIST && distCompletes)
            {
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] reached target dist={dist:F1}m → completing task={_task}");
                MarkTaskComplete();
                return true;
            }

            return false;
        }

        public override void Stop()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            base.Stop();
        }

        // ── MarkTaskComplete (вызывается из Logic) ────────────────────
        public void MarkTaskComplete()
        {
            StopKillTracking();   // снимаем подписку OnPlayerDead если была

            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] MarkTaskComplete: task was {_task}");

            // Запись в историю
            if (_task != ScavTaskType.None)
            {
                string line = $"{BotOwner.name} — {_task}";
                TaskHistoryLines.Insert(0, line);
                if (TaskHistoryLines.Count > MAX_HISTORY)
                    TaskHistoryLines.RemoveAt(TaskHistoryLines.Count - 1);
            }

            if (_task == ScavTaskType.SpawnRush &&
                _spawnRushOwnerBotId == BotOwner.Id)
                _spawnRushOwnerBotId = -1;

            // Освобождаем EFT задания чтобы другие боты могли их взять
            EftQuestTaskManager.ReleaseTasksForBot(BotOwner.Id);
            _currentEftVisit = null;
            _currentEftKill  = null;
            _currentEftFind  = null;

            _taskComplete    = true;
            _task            = ScavTaskType.None;
            _hasCachedTarget = false;
            float cd = UnityEngine.Random.Range(10f, 30f);
            _cooldownUntil   = Time.time + cd;
            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] cooldown {cd:F0}s until {_cooldownUntil:F0}");
        }


        // Вызывается из Logic чтобы пометить зону босса как обысканную
        public static void MarkBossAreaSearched(Vector3 pos)
        {
            // Удаляем старые записи в радиусе
            _searchedBossAreas.RemoveAll(
                e => Vector3.Distance(e.pos, pos) < SearchedAreaRadius);
            _searchedBossAreas.Add((pos, Time.time + SearchedAreaExpiry));
            ScavTaskPlugin.Log.LogDebug(
                $"[ScavTaskMod] Boss area marked searched at {pos}");
        }

        // ── TryFindTarget ─────────────────────────────────────────────
        public bool TryFindTarget(out Vector3 target)
        {
            if (_hasCachedTarget && Time.time < _nextTargetUpdate)
            {
                target = _cachedTargetPos;
                return true;
            }

            bool found = false;
            target = Vector3.zero;

            switch (_task)
            {
                case ScavTaskType.SpawnRush:
                    found = TryGetSpawnRushTarget(out target);
                    break;
                case ScavTaskType.HuntBoss:
                    found = TryGetHuntBossTarget(out target);
                    break;
                case ScavTaskType.CheckPMCSpawn:
                    found = TryGetPMCTarget(out target);
                    break;
                case ScavTaskType.EftVisit:
                    found = TryGetEftVisitTarget(out target);
                    break;
                case ScavTaskType.EftKill:
                    found = TryGetEftKillTarget(out target);
                    break;
                case ScavTaskType.EftFind:
                    found = TryGetEftFindTarget(out target);
                    break;
            }

            if (found)
            {
                _cachedTargetPos   = target;
                _hasCachedTarget   = true;
                _nextTargetUpdate  = Time.time + TARGET_UPDATE_INT;
            }
            else
            {
                _hasCachedTarget = false;
            }

            return found;
        }

        // ── SpawnRush: идти к месту спавна игрока ─────────────────────
        // Только ближайший к спавну бот берёт это задание
        private bool TryGetSpawnRushTarget(out Vector3 pos)
        {
            pos = Vector3.zero;

            if (!PlayerSpawnKnown) return false;

            // Если игрок мёртв — задание теряет смысл
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null ||
                !gw.MainPlayer.HealthController.IsAlive) return false;

            // Snap точки спавна на NavMesh один раз
            NavMeshHit spawnSnap;
            Vector3 spawnNavPos = NavMesh.SamplePosition(PlayerSpawnPos, out spawnSnap, 10f, NavMesh.AllAreas)
                ? spawnSnap.position
                : PlayerSpawnPos;

            // Проверяем что именно мы — назначенный бот
            if (_spawnRushOwnerBotId == BotOwner.Id)
            {
                pos = spawnNavPos;
                return true;
            }

            // Пробуем занять слот если он свободен
            if (_spawnRushOwnerBotId != -1) return false;

            // Ищем ближайшего к спавну бота среди всех без задания
            float myDist = Vector3.Distance(BotOwner.Position, spawnNavPos);

            foreach (var kv in LayersByBotId)
            {
                if (kv.Key == BotOwner.Id) continue;
                var other = kv.Value;
                if (other == null || other.BotOwner == null ||
                    other.BotOwner.IsDead) continue;
                float otherDist = Vector3.Distance(other.BotOwner.Position, spawnNavPos);
                if (otherDist < myDist) return false;
            }

            if (!IsPathSafe(spawnNavPos)) return false;

            _spawnRushOwnerBotId = BotOwner.Id;
            pos = spawnNavPos;
            return true;
        }

        // ── HuntBoss: идти к ближайшей необысканной зоне спавна босса ─
        private bool TryGetHuntBossTarget(out Vector3 pos)
        {
            pos = Vector3.zero;

            if (NoBossOnMap) return false;

            // Инициализируем зоны спавна боссов один раз за рейд
            if (_bossSpawnZones == null)
                InitBossSpawnZones();
            if (_bossSpawnZones == null || _bossSpawnZones.Count == 0) return false;

            // Чистим просроченные записи
            _searchedBossAreas.RemoveAll(e => Time.time > e.expiry);

            Vector3 nearest      = Vector3.zero;
            float   nearestDist  = float.MaxValue;
            bool    anyUnsearched = false;

            // Шаг 1: находим ближайшую необысканную зону ТОЛЬКО по дистанции (дёшево, без NavMesh.CalculatePath)
            foreach (var zonePos in _bossSpawnZones)
            {
                bool searched = false;
                foreach (var s in _searchedBossAreas)
                    if (Vector3.Distance(s.pos, zonePos) < SearchedAreaRadius)
                    { searched = true; break; }
                if (searched) continue;

                anyUnsearched = true;

                NavMeshHit hit;
                Vector3 snapPos = NavMesh.SamplePosition(zonePos, out hit, 15f, NavMesh.AllAreas)
                    ? hit.position : zonePos;

                float d = Vector3.Distance(BotOwner.Position, snapPos);
                if (d < nearestDist) { nearestDist = d; nearest = snapPos; }
            }

            if (!anyUnsearched)
            {
                NoBossOnMap = true;
                ScavTaskPlugin.Log.LogInfo("[ScavTaskMod] Все зоны боссов обысканы — босса нет на карте");
                return false;
            }

            if (nearest == Vector3.zero) return false;

            // Шаг 2: IsPathSafe только для выбранной зоны (было N вызовов — стало 1)
            if (!IsPathSafe(nearest)) return false;

            pos = nearest;
            return true;
        }

        private static void InitBossSpawnZones()
        {
            _bossSpawnZones = new List<Vector3>();
            try
            {
                var zones = UnityEngine.Object.FindObjectsOfType<BotZone>();
                foreach (var zone in zones)
                {
                    if (zone == null || !zone.CanSpawnBoss) continue;
                    _bossSpawnZones.Add(zone.CenterOfSpawnPoints);
                }
            }
            catch { }
            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] Зоны спавна боссов: {_bossSpawnZones.Count}");
        }

        // ── Публичный доступ к зонам боссов (используется PmcTaskLayer) ──
        public static void EnsureBossZonesInitialized()
        {
            if (_bossSpawnZones == null) InitBossSpawnZones();
        }

        public static IReadOnlyList<Vector3> GetBossZones()
        {
            EnsureBossZonesInitialized();
            return _bossSpawnZones;
        }

        // Проверка: помечена ли зона как обысканная (доступна для PmcTaskLayer)
        public static bool IsZoneSearched(Vector3 pos)
        {
            _searchedBossAreas.RemoveAll(e => Time.time > e.expiry);
            foreach (var s in _searchedBossAreas)
                if (Vector3.Distance(s.pos, pos) < SearchedAreaRadius) return true;
            return false;
        }

        // ── CheckPMCSpawn: бот идёт проверить место где видел PMC ──────
        // Цель фиксируется при первом нахождении и НЕ обновляется —
        // иначе PMC убегает и бот никогда не достигает REACH_DIST.
        private bool TryGetPMCTarget(out Vector3 pos)
        {
            // Уже зафиксировали цель — возвращаем её без пересчёта
            if (_hasFixedTarget)
            {
                pos = _fixedTarget;
                return true;
            }

            pos = Vector3.zero;
            var gw = Singleton<GameWorld>.Instance;
            if (gw == null) return false;

            Player closestPMC = null;
            float  closestDist = float.MaxValue;
            int    totalAI = 0, pmcFound = 0;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.IsAI) continue;
                totalAI++;
                if (!player.HealthController.IsAlive) continue;
                var bo = player.AIData?.BotOwner;
                if (bo == null) continue;

                var role = bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault;
                if (!IsPMCRole(role)) continue;
                pmcFound++;

                float d = Vector3.Distance(BotOwner.Position, player.Position);
                if (d < closestDist) { closestDist = d; closestPMC = player; }
            }

            // CheckPMCSpawn только если PMC в радиусе 200м
            if (closestPMC == null || closestDist > 200f)
                return false;

            // Snap на NavMesh
            NavMeshHit hit;
            pos = NavMesh.SamplePosition(closestPMC.Position, out hit, 10f, NavMesh.AllAreas)
                ? hit.position
                : closestPMC.Position;

            // Проверяем доступность пути до PMC
            if (!IsPathSafe(pos))
            {
                ScavTaskPlugin.Log.LogDebug(
                    $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC: path to {pos} unsafe → skip");
                return false;
            }

            // Фиксируем цель — больше не обновляем
            _fixedTarget    = pos;
            _hasFixedTarget = true;
            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC target FIXED at {pos} (dist={closestDist:F0}m)");
            return true;
        }

        // ── EFT квест: посетить зону ──────────────────────────────────
        private bool TryGetEftVisitTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftVisit == null)
            {
                if (!EftQuestTaskManager.TryGetVisitTask(BotOwner.Position, BotOwner.Id, out _currentEftVisit))
                    return false;

                // Проверяем доступность зоны при первом назначении
                NavMeshHit checkHit;
                Vector3 checkPos = NavMesh.SamplePosition(_currentEftVisit.ZonePosition, out checkHit, 15f, NavMesh.AllAreas)
                    ? checkHit.position : _currentEftVisit.ZonePosition;

                if (!IsPathSafe(checkPos))
                {
                    ScavTaskPlugin.Log.LogDebug(
                        $"[ScavTaskMod] EftVisit zone '{_currentEftVisit.ZoneId}' unreachable → blocked globally");
                    EftQuestTaskManager.BlockedVisitZoneIds.Add(_currentEftVisit.ZoneId);
                    EftQuestTaskManager.ReleaseTasksForBot(BotOwner.Id);
                    _currentEftVisit = null;
                    return false;
                }
            }

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(_currentEftVisit.ZonePosition, out hit, 15f, NavMesh.AllAreas)
                ? hit.position
                : _currentEftVisit.ZonePosition;
            return pos != Vector3.zero;
        }

        // ── EFT квест: убить цель ─────────────────────────────────────
        // PMC  → динамически ищем ближайшего живого PMC каждые TARGET_UPDATE_INT сек
        // Boss → идти к зонам спавна босса (как HuntBoss)
        private bool TryGetEftKillTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftKill == null)
            {
                if (!EftQuestTaskManager.TryGetKillTask(BotOwner.Id, out _currentEftKill))
                    return false;
                StartKillTracking();
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] EftKill: quest='{_currentEftKill.QuestName}' type={_currentEftKill.TargetType}");
            }

            return _currentEftKill.TargetType == EftKillTargetType.Boss
                ? TryGetHuntBossTarget(out pos)
                : TryGetDynamicPMCTarget(out pos);
        }

        // Динамический поиск PMC — без фиксации, обновляется через кеш (TARGET_UPDATE_INT)
        private bool TryGetDynamicPMCTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.AllAlivePlayersList == null) return false;

            Player nearest    = null;
            float nearestDist = float.MaxValue;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.HealthController.IsAlive) continue;

                bool isTarget = !player.IsAI; // живой PMC-игрок
                if (!isTarget && player.IsAI)
                {
                    var bo = player.AIData?.BotOwner;
                    if (bo != null && IsPMCRole(
                            bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault))
                        isTarget = true;
                }
                if (!isTarget) continue;

                float d = Vector3.Distance(BotOwner.Position, player.Position);
                if (d < nearestDist) { nearestDist = d; nearest = player; }
            }

            if (nearest == null) return false;

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(nearest.Position, out hit, 10f, NavMesh.AllAreas)
                ? hit.position
                : nearest.Position;
            return pos != Vector3.zero;
        }

        // ── EftKill: подписка на Player.OnPlayerDead для всех живых PMC ──
        private void StartKillTracking()
        {
            StopKillTracking();
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.AllAlivePlayersList == null) return;

            foreach (var player in gw.AllAlivePlayersList)
            {
                if (player == null || !player.HealthController.IsAlive) continue;

                bool isTarget = !player.IsAI;
                if (!isTarget && player.IsAI)
                {
                    var bo = player.AIData?.BotOwner;
                    if (bo != null && IsPMCRole(
                            bo.Profile?.Info?.Settings?.Role ?? WildSpawnType.assault))
                        isTarget = true;
                }
                if (!isTarget) continue;

                player.OnPlayerDead += OnTrackedPlayerDead;
                _trackedPMCs.Add(player);
            }

            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] EftKill: tracking {_trackedPMCs.Count} PMC targets");
        }

        private void StopKillTracking()
        {
            foreach (var p in _trackedPMCs)
                if (p != null) p.OnPlayerDead -= OnTrackedPlayerDead;
            _trackedPMCs.Clear();
            _killConfirmed = false;
        }

        private void OnTrackedPlayerDead(Player player, IPlayer lastAggressor,
            DamageInfoStruct dmg, EBodyPart part)
        {
            if (player != null)
                player.OnPlayerDead -= OnTrackedPlayerDead;
            _trackedPMCs.Remove(player);

            // Проверяем что убийца — именно наш бот
            if (lastAggressor?.ProfileId == BotOwner.ProfileId)
            {
                _killConfirmed = true;
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] [{BotOwner.Id}] EftKill: killed '{player?.name}' → quest will complete");
            }
        }

        // ── EFT квест: найти предмет ──────────────────────────────────
        private bool TryGetEftFindTarget(out Vector3 pos)
        {
            pos = Vector3.zero;
            if (!EftQuestTaskManager.IsInitialized) return false;

            if (_currentEftFind == null)
            {
                if (!EftQuestTaskManager.TryGetFindTask(BotOwner.Position, BotOwner.Id, out _currentEftFind))
                    return false;
            }

            // Освежаем позиции лута если нужно
            EftQuestTaskManager.RefreshLootPositions();
            if (_currentEftFind.LootPositions.Count == 0) return false;

            // Ближайшая позиция лута
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
                ? hit.position
                : nearest;
            return pos != Vector3.zero;
        }

        // ── Вспомогательные счётчики (используются в PickBestTask) ───
        // Количество живых Скавов с нашим слоем (основа для расчёта квот)
        private static int CountAliveScavsInLayer()
        {
            int n = 0;
            foreach (var kv in LayersByBotId)
                if (kv.Value?.BotOwner != null && !kv.Value.BotOwner.IsDead) n++;
            return Mathf.Max(1, n);
        }

        // Сколько ботов сейчас активно выполняют данный тип задания
        private static int CountActiveTaskType(ScavTaskType type)
        {
            int n = 0;
            foreach (var kv in LayersByBotId)
            {
                var l = kv.Value;
                if (l?.BotOwner != null && !l.BotOwner.IsDead && l.CurrentTask == type) n++;
            }
            return n;
        }

        // ── Выбор лучшего задания ─────────────────────────────────────
        private ScavTaskType PickBestTask()
        {
            Vector3 tmp;

            // 1. SpawnRush — один бот, реакция на спавн игрока
            _task = ScavTaskType.SpawnRush;
            if (TryGetSpawnRushTarget(out tmp))
                return ScavTaskType.SpawnRush;

            // Квоты пропорционально живым Скавам:
            //   alive=12 → Visit≤4, Kill≤2, Find≤2 — остальные ~4 бота на PMC/Boss
            int alive    = CountAliveScavsInLayer();
            int visitCap = Mathf.Max(2, alive / 3);  // ~33%
            int killCap  = Mathf.Max(1, alive / 5);  // ~20%
            int findCap  = Mathf.Max(1, alive / 5);  // ~20%

            if (EftQuestTaskManager.IsInitialized)
            {
                // Счёт идёт ДО установки _task — текущий бот не попадает в счётчик
                if (CountActiveTaskType(ScavTaskType.EftVisit) < visitCap)
                {
                    _task = ScavTaskType.EftVisit;
                    if (TryGetEftVisitTarget(out tmp)) return ScavTaskType.EftVisit;
                }

                if (CountActiveTaskType(ScavTaskType.EftKill) < killCap)
                {
                    _task = ScavTaskType.EftKill;
                    if (TryGetEftKillTarget(out tmp)) return ScavTaskType.EftKill;
                }

                if (CountActiveTaskType(ScavTaskType.EftFind) < findCap)
                {
                    _task = ScavTaskType.EftFind;
                    if (TryGetEftFindTarget(out tmp)) return ScavTaskType.EftFind;
                }
            }

            // Боты вне EFT-квоты → CheckPMCSpawn / HuntBoss
            _task = ScavTaskType.CheckPMCSpawn;
            if (TryGetPMCTarget(out tmp)) return ScavTaskType.CheckPMCSpawn;

            _task = ScavTaskType.HuntBoss;
            if (TryGetHuntBossTarget(out tmp)) return ScavTaskType.HuntBoss;

            // PMC/Boss тоже нет — фолбэк на EFT без квоты
            if (EftQuestTaskManager.IsInitialized)
            {
                _task = ScavTaskType.EftVisit;
                if (TryGetEftVisitTarget(out tmp)) return ScavTaskType.EftVisit;

                _task = ScavTaskType.EftFind;
                if (TryGetEftFindTarget(out tmp)) return ScavTaskType.EftFind;
            }

            _task = ScavTaskType.None;
            return ScavTaskType.None;
        }

        // ── Проверка безопасности пути ────────────────────────────────
        // Возвращает false если NavMesh не строит полный путь, путь слишком длинный
        // или рядом с сегментами пути находится статическая мина.
        private bool IsPathSafe(Vector3 dest)
        {
            // 1. NavMesh: путь должен быть полным (PathComplete)
            // Используем переиспользуемый объект _sharedNavPath чтобы не аллоцировать GC каждый вызов
            if (!NavMesh.CalculatePath(BotOwner.Position, dest, NavMesh.AllAreas, _sharedNavPath))
                return false;
            if (_sharedNavPath.status != NavMeshPathStatus.PathComplete)
                return false;

            // 2. Длина пути — не идём слишком далеко
            var corners  = _sharedNavPath.corners;
            float totalLen = 0f;
            for (int i = 1; i < corners.Length; i++)
                totalLen += Vector3.Distance(corners[i - 1], corners[i]);
            if (totalLen > MAX_PATH_LENGTH) return false;

            // 3. Статические мины вдоль маршрута (кеш обновляется раз в MINES_CACHE_TTL сек)
            if (_cachedMines == null || Time.time > _minesCheckTime)
            {
                try   { _cachedMines = UnityEngine.Object.FindObjectsOfType<MineDirectional>(); }
                catch { _cachedMines = new MineDirectional[0]; }
                _minesCheckTime = Time.time + MINES_CACHE_TTL;
            }

            if (_cachedMines != null)
            {
                foreach (var mine in _cachedMines)
                {
                    if (mine == null) continue;
                    Vector3 minePos = mine.transform.position;
                    for (int i = 1; i < corners.Length; i++)
                    {
                        if (DistSqrPointSegment(minePos, corners[i - 1], corners[i]) < MINE_DANGER_SQR)
                            return false;
                    }
                }
            }

            return true;
        }

        // Квадрат расстояния от точки P до отрезка AB
        private static float DistSqrPointSegment(Vector3 p, Vector3 a, Vector3 b)
        {
            Vector3 ab    = b - a;
            float   lenSqr = ab.sqrMagnitude;
            if (lenSqr < 0.0001f) return (p - a).sqrMagnitude;
            float t = Mathf.Clamp01(Vector3.Dot(p - a, ab) / lenSqr);
            return (p - (a + t * ab)).sqrMagnitude;
        }

        // ── Проверка боя ──────────────────────────────────────────────
        private static void InitSainReflection()
        {
            if (_sainReflectionDone) return;
            _sainReflectionDone = true;
            try
            {
                var t = Type.GetType("SAIN.Plugin.External, SAIN");
                _canBotQuestMethod = t?.GetMethod("CanBotQuest",
                    BindingFlags.Public | BindingFlags.Static);
                ScavTaskPlugin.Log.LogInfo(
                    $"[ScavTaskMod] SAIN.CanBotQuest: {(_canBotQuestMethod != null ? "OK" : "не найден — fallback")}");
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
                    if (result is bool canQuest)
                        return !canQuest;
                }
                catch { }
            }

            // Fallback: нативные EFT проверки
            try
            {
                var mem = BotOwner.Memory;
                if (mem == null) return false;
                // Враг виден или был виден недавно (расширено до 30 сек вместо 10)
                if (mem.GoalEnemy != null &&
                    Time.time - mem.GoalEnemy.TimeLastSeen < 30f)           return true;
                if (mem.IsUnderFire)                                        return true;
            }
            catch { }

            return false;
        }

        // ── Роли ──────────────────────────────────────────────────────
        public static bool IsBossRole(WildSpawnType role)
        {
            string n = role.ToString();
            return n.StartsWith("boss", StringComparison.OrdinalIgnoreCase)
                || n == "Killa" || n == "Tagilla"
                || n == "Knight" || n == "BigPipe" || n == "BirdEye"
                || n.StartsWith("follower", StringComparison.OrdinalIgnoreCase);
        }

        private static bool IsPMCRole(WildSpawnType role)
        {
            string n = role.ToString();
            return n.StartsWith("pmc", StringComparison.OrdinalIgnoreCase)
                || n.Equals("pmcBot", StringComparison.OrdinalIgnoreCase)
                || n == "ExUsec" || n == "ArenaFighter";
        }
    }
}
