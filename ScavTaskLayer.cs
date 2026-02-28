using Comfort.Common;
using DrakiaXYZ.BigBrain.Brains;
using EFT;
using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    public enum ScavTaskType
    {
        None        = 0,
        SpawnRush   = 1,   // идти к месту спавна игрока и убить его при встрече
        HuntBoss    = 2,   // обыскивать споты спавна босса
        CheckPMCSpawn = 3  // проверить ближайший спавн PMC
    }

    public class ScavTaskData : CustomLayer.ActionData
    {
        public ScavTaskType TaskType;
        public Vector3      TargetPos;
        public bool         HasTarget;
        // Для HuntBoss — sub-state
        public bool         Searching;     // true = уже прибыли, обыскиваем
        public float        SearchUntil;   // Time.time до которого ищем
        public Vector3      SearchCenter;  // центр зоны поиска
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

        // ── Путь бота и история задач (для оверлея) ───────────────────
        // Bot id → список записанных позиций
        public static readonly Dictionary<int, List<Vector3>> BotPaths =
            new Dictionary<int, List<Vector3>>();
        private const int MAX_PATH_POINTS = 150;

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
            if (IsBotInCombat())                     return false;

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

                if (BotPaths.TryGetValue(BotOwner.Id, out var oldPath))
                    oldPath.Clear();

                _task            = PickBestTask();
                _taskComplete    = false;
                _hasCachedTarget = false;
                _hasFixedTarget  = false;   // сброс фиксированной цели при новом задании
                _taskTimeout     = Time.time + TASK_TIMEOUT;

                if (_task == ScavTaskType.None) return false;

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
            if (!TryFindTarget(out foundTarget)) return false;

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

            return new CustomLayer.Action(typeof(ScavTaskLogic), _task.ToString(),
                new ScavTaskData { TaskType = _task, TargetPos = target, HasTarget = true });
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
            if (dist <= REACH_DIST && _task != ScavTaskType.HuntBoss)
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

            _taskComplete    = true;
            _task            = ScavTaskType.None;
            _hasCachedTarget = false;
            float cd = UnityEngine.Random.Range(10f, 30f);
            _cooldownUntil   = Time.time + cd;
            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] cooldown {cd:F0}s until {_cooldownUntil:F0}");
        }

        // ── RecordPathPoint (вызывается из Logic каждую секунду) ──────
        public static void RecordPathPoint(int botId, Vector3 pos)
        {
            if (!BotPaths.TryGetValue(botId, out var list))
            {
                list = new List<Vector3>();
                BotPaths[botId] = list;
            }
            if (list.Count == 0 || Vector3.Distance(list[list.Count - 1], pos) > 2.5f)
            {
                list.Add(pos);
                if (list.Count > MAX_PATH_POINTS)
                    list.RemoveAt(0);
            }
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

            // Ищем ближайшую необысканную зону
            Vector3 nearest    = Vector3.zero;
            float   nearestDist = float.MaxValue;

            foreach (var zonePos in _bossSpawnZones)
            {
                bool searched = false;
                foreach (var s in _searchedBossAreas)
                    if (Vector3.Distance(s.pos, zonePos) < SearchedAreaRadius)
                    { searched = true; break; }
                if (searched) continue;

                float d = Vector3.Distance(BotOwner.Position, zonePos);
                if (d < nearestDist) { nearestDist = d; nearest = zonePos; }
            }

            if (nearest == Vector3.zero)
            {
                // Все зоны обысканы — босса нет на карте, квест больше не выдаём
                NoBossOnMap = true;
                ScavTaskPlugin.Log.LogInfo("[ScavTaskMod] Все зоны боссов обысканы — босса нет на карте");
                return false;
            }

            NavMeshHit hit;
            pos = NavMesh.SamplePosition(nearest, out hit, 15f, NavMesh.AllAreas)
                ? hit.position
                : nearest;
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
            {
                ScavTaskPlugin.Log.LogDebug(
                    $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC fail: totalAI={totalAI} pmcs={pmcFound} closestDist={closestDist:F0}m");
                return false;
            }

            // Snap на NavMesh и сразу фиксируем — больше не обновляем
            NavMeshHit hit;
            pos = NavMesh.SamplePosition(closestPMC.Position, out hit, 10f, NavMesh.AllAreas)
                ? hit.position
                : closestPMC.Position;

            _fixedTarget    = pos;
            _hasFixedTarget = true;
            ScavTaskPlugin.Log.LogInfo(
                $"[ScavTaskMod] [{BotOwner.Id}] CheckPMC target FIXED at {pos} (dist={closestDist:F0}m)");
            return true;
        }

        // ── Выбор лучшего задания ─────────────────────────────────────
        private ScavTaskType PickBestTask()
        {
            // 1. SpawnRush — если спавн известен и никто не взял
            Vector3 tmp;
            _task = ScavTaskType.SpawnRush;
            if (TryGetSpawnRushTarget(out tmp))
                return ScavTaskType.SpawnRush;

            // 2. CheckPMCSpawn — если PMC рядом
            _task = ScavTaskType.CheckPMCSpawn;
            if (TryGetPMCTarget(out tmp))
                return ScavTaskType.CheckPMCSpawn;

            // 3. HuntBoss — если есть необысканные зоны спавна босса
            _task = ScavTaskType.HuntBoss;
            if (TryGetHuntBossTarget(out tmp))
                return ScavTaskType.HuntBoss;

            _task = ScavTaskType.None;
            return ScavTaskType.None;
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
                if (mem.GoalEnemy != null && mem.GoalEnemy.IsVisible)       return true;
                if (mem.GoalEnemy != null &&
                    Time.time - mem.GoalEnemy.TimeLastSeen < 10f)           return true;
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
