using DrakiaXYZ.BigBrain.Brains;
using EFT;
using UnityEngine;
using UnityEngine.AI;

namespace ScavTaskMod
{
    // Логика выполнения задания PMC-бота.
    // Структура идентична ScavTaskLogic — навигация, поиск, stuck detection.
    public class PmcTaskLogic : CustomLogic
    {
        private const float REACH_DIST        = 10f;
        private const float PATH_RECALC_INT   = 2f;
        private const float SPRINT_MIN_DIST   = 20f;
        private const float SEARCH_DURATION   = 30f;
        private const float SEARCH_RADIUS     = 25f;
        private const float SEARCH_REACH_DIST = 4f;
        private const float PMC_REACH_DIST    = 18f;

        private float   _nextPathRecalc = 0f;
        private Vector3 _lastTargetPos;
        private bool    _pathSet        = false;
        private float   _pathInvalidUntil   = 0f;
        private const float PATH_INVALID_COOLDOWN = 5f;
        private float   _lastPathInvalidLog = -999f;
        private const float PATH_INVALID_LOG_INT  = 10f;

        // BossHunt / EftVisit / EftFind: sub-state поиска в зоне
        private bool    _searching    = false;
        private float   _searchUntil = 0f;
        private Vector3 _searchCenter;
        private Vector3 _currentSearchPt;
        private bool    _searchPtSet  = false;
        private float   _nextSearchPt = 0f;

        // EftVisit / EftFind: блуждание в зоне
        private bool  _eftWandering   = false;
        private float _eftWanderUntil = 0f;

        // Stuck detection
        private Vector3 _stuckCheckPos;
        private float   _stuckCheckTime   = 0f;
        private const float STUCK_CHECK_INTERVAL = 5f;
        private const float STUCK_MIN_MOVE       = 0.8f;
        private int     _stuckCount    = 0;
        private const int STUCK_MAX    = 3;

        public PmcTaskLogic(BotOwner botOwner) : base(botOwner) { }

        public override void Start()
        {
            _pathSet        = false;
            _nextPathRecalc = 0f;
            _searching      = false;
            _searchPtSet    = false;
            _eftWandering   = false;
            _stuckCount     = 0;
            _stuckCheckTime = Time.time + STUCK_CHECK_INTERVAL;
            _stuckCheckPos  = Vector3.zero;
            base.Start();
        }

        public override void Stop()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            base.Stop();
        }

        public override void Update(CustomLayer.ActionData data)
        {
            if (BotOwner == null || BotOwner.IsDead) return;

            var td = data as PmcTaskData;
            if (td == null || !td.HasTarget)
            {
                var layer = GetLayer();
                if (layer == null) { StopAndComplete(); return; }
                Vector3 fallback;
                if (layer.CurrentTask == PmcTaskType.None ||
                    !layer.TryFindTarget(out fallback))
                {
                    StopAndComplete();
                    return;
                }
                td = new PmcTaskData
                {
                    TaskType  = layer.CurrentTask,
                    TargetPos = fallback,
                    HasTarget = true
                };
            }

            if (CheckStuck()) return;

            switch (td.TaskType)
            {
                case PmcTaskType.BossHunt: UpdateBossHunt(td); break;
                case PmcTaskType.EftVisit: UpdateEftVisit(td); break;
                case PmcTaskType.EftKill:  UpdateEftKill(td);  break;
                case PmcTaskType.EftFind:  UpdateEftFind(td);  break;
                default: StopAndComplete(); break;
            }
        }

        // ── BossHunt: идём к зоне → обыскиваем 30 сек ────────────────────────
        private void UpdateBossHunt(PmcTaskData td)
        {
            float distToTarget = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_searching)
            {
                bool pathOk  = NavigateTo(td.TargetPos, REACH_DIST);
                bool arrived = distToTarget <= REACH_DIST;
                bool pathBlocked = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && distToTarget > REACH_DIST && distToTarget < 80f);

                if (arrived || pathBlocked)
                {
                    if (pathBlocked && !arrived)
                        ScavTaskPlugin.Log.LogInfo(
                            $"[PmcTask] [{BotOwner.Id}] BossHunt: path blocked dist={distToTarget:F1}m → search from here");

                    _searching    = true;
                    _searchUntil  = Time.time + SEARCH_DURATION;
                    _searchCenter = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet  = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                }
            }
            else
            {
                // Поиск в зоне 30 секунд
                if (Time.time >= _searchUntil)
                {
                    ScavTaskLayer.MarkBossAreaSearched(_searchCenter);
                    ScavTaskPlugin.Log.LogInfo(
                        $"[PmcTask] [{BotOwner.Id}] BossHunt: zone searched → complete");
                    StopAndComplete();
                    return;
                }

                float distToPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToPt <= SEARCH_REACH_DIST || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, SEARCH_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 8f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── EftVisit: прийти в зону → блуждать 60 сек ────────────────────────
        private const float EFT_VISIT_DURATION = 60f;
        private const float EFT_VISIT_RADIUS   = 20f;

        private void UpdateEftVisit(PmcTaskData td)
        {
            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_eftWandering)
            {
                bool pathOk  = NavigateTo(td.TargetPos, REACH_DIST);
                bool arrived = dist <= REACH_DIST;
                bool pathStuck = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && dist > REACH_DIST && dist < 60f);

                if (arrived || pathStuck)
                {
                    _eftWandering   = true;
                    _eftWanderUntil = Time.time + EFT_VISIT_DURATION;
                    _searchCenter   = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet    = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                    ScavTaskPlugin.Log.LogInfo(
                        $"[PmcTask] [{BotOwner.Id}] EftVisit: arrived, wandering {EFT_VISIT_DURATION}s");
                }
            }
            else
            {
                if (Time.time >= _eftWanderUntil)
                {
                    ScavTaskPlugin.Log.LogInfo(
                        $"[PmcTask] [{BotOwner.Id}] EftVisit: complete");
                    StopAndComplete();
                    return;
                }

                float distToPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToPt <= SEARCH_REACH_DIST || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, EFT_VISIT_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 8f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── EftKill: PMC охотится на цель (Скав или босс) ─────────────────────
        private void UpdateEftKill(PmcTaskData td)
        {
            // Если цель — босс, используем логику поиска зоны
            if (td.EftKillTarget == EftKillTargetType.Boss)
            {
                UpdateBossHunt(td);
                return;
            }

            // Идём к Скаву — SAIN перехватит управление при виде врага
            bool pathOk = NavigateTo(td.TargetPos, PMC_REACH_DIST);
            if (!pathOk) return;

            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);
            if (dist <= PMC_REACH_DIST)
                BotOwner.Mover.Stop();
        }

        // ── EftFind: идём к луту → обыскиваем зону 30 сек ───────────────────
        private const float EFT_FIND_DURATION = 30f;
        private const float EFT_FIND_RADIUS   = 15f;

        private void UpdateEftFind(PmcTaskData td)
        {
            float dist = Vector3.Distance(BotOwner.Position, td.TargetPos);

            if (!_eftWandering)
            {
                bool pathOk  = NavigateTo(td.TargetPos, REACH_DIST);
                bool arrived = dist <= REACH_DIST;
                bool pathStuck = !pathOk
                    || (!BotOwner.Mover.IsMoving && !BotOwner.Mover.HasPathAndNoComplete
                        && _pathSet && dist > REACH_DIST && dist < 60f);

                if (arrived || pathStuck)
                {
                    _eftWandering   = true;
                    _eftWanderUntil = Time.time + EFT_FIND_DURATION;
                    _searchCenter   = arrived ? td.TargetPos : BotOwner.Position;
                    _searchPtSet    = false;
                    BotOwner.Mover.Stop();
                    BotOwner.Mover.Sprint(false);
                    ScavTaskPlugin.Log.LogInfo(
                        $"[PmcTask] [{BotOwner.Id}] EftFind: at loot area, searching {EFT_FIND_DURATION}s");
                }
            }
            else
            {
                if (Time.time >= _eftWanderUntil)
                {
                    ScavTaskPlugin.Log.LogInfo(
                        $"[PmcTask] [{BotOwner.Id}] EftFind: search complete");
                    StopAndComplete();
                    return;
                }

                float distToPt = _searchPtSet
                    ? Vector3.Distance(BotOwner.Position, _currentSearchPt)
                    : float.MaxValue;

                if (!_searchPtSet || distToPt <= SEARCH_REACH_DIST || Time.time >= _nextSearchPt)
                {
                    Vector3 pt;
                    if (TryRandomNavPoint(_searchCenter, EFT_FIND_RADIUS, out pt))
                    {
                        _currentSearchPt = pt;
                        _searchPtSet     = true;
                        _nextSearchPt    = Time.time + 6f;
                        _pathSet         = false;
                    }
                }

                if (_searchPtSet)
                    NavigateTo(_currentSearchPt, SEARCH_REACH_DIST, sprint: false);
            }
        }

        // ── Навигация ─────────────────────────────────────────────────────────
        private bool NavigateTo(Vector3 target, float reachDist, bool sprint = true)
        {
            float dist       = Vector3.Distance(BotOwner.Position, target);
            bool targetMoved = Vector3.Distance(target, _lastTargetPos) > 3f;

            if (!_pathSet || targetMoved || Time.time >= _nextPathRecalc)
            {
                Vector3 navTarget = target;
                NavMeshHit snapHit;
                if (NavMesh.SamplePosition(target, out snapHit, 10f, NavMesh.AllAreas))
                    navTarget = snapHit.position;

                if (Time.time < _pathInvalidUntil) return false;

                var status = BotOwner.Mover.GoToPoint(navTarget, false, reachDist);
                if (status == NavMeshPathStatus.PathInvalid)
                {
                    if (Time.time - _lastPathInvalidLog >= PATH_INVALID_LOG_INT)
                    {
                        ScavTaskPlugin.Log.LogWarning(
                            $"[PmcTask] [{BotOwner.Id}] NavigateTo: PathInvalid to {navTarget}");
                        _lastPathInvalidLog = Time.time;
                    }
                    _pathInvalidUntil = Time.time + PATH_INVALID_COOLDOWN;
                    return false;
                }
                _pathSet          = true;
                _pathInvalidUntil = 0f;
                _lastTargetPos    = target;
                _nextPathRecalc   = Time.time + PATH_RECALC_INT;
            }

            BotOwner.Mover.SetPose(1f);
            BotOwner.SetTargetMoveSpeed(1f);
            // Не форсируем взгляд по направлению движения если бот засёк врага —
            // иначе мы перебиваем SAIN и бот не может повернуться к цели
            if (BotOwner.Memory?.GoalEnemy == null)
                BotOwner.Steering.LookToMovingDirection();

            bool shouldSprint = sprint && dist > SPRINT_MIN_DIST;
            if (BotOwner.Mover.Sprinting != shouldSprint)
                BotOwner.Mover.Sprint(shouldSprint);

            return true;
        }

        // ── Stuck detection ───────────────────────────────────────────────────
        private bool CheckStuck()
        {
            if (Time.time < _stuckCheckTime) return false;
            _stuckCheckTime = Time.time + STUCK_CHECK_INTERVAL;

            float moved = Vector3.Distance(BotOwner.Position, _stuckCheckPos);
            _stuckCheckPos = BotOwner.Position;

            bool hasPendingPath = _pathSet && BotOwner.Mover.HasPathAndNoComplete;
            if (hasPendingPath && moved < STUCK_MIN_MOVE)
            {
                _stuckCount++;
                ScavTaskPlugin.Log.LogWarning(
                    $"[PmcTask] [{BotOwner.Id}] Stuck ({_stuckCount}/{STUCK_MAX}) moved={moved:F2}m");

                if (_stuckCount >= STUCK_MAX)
                {
                    ScavTaskPlugin.Log.LogWarning(
                        $"[PmcTask] [{BotOwner.Id}] Stuck limit → force complete");
                    StopAndComplete();
                    return true;
                }
                _pathSet = false;
            }
            else
            {
                _stuckCount = 0;
            }
            return false;
        }

        // ── Вспомогательные ──────────────────────────────────────────────────
        private void StopAndComplete()
        {
            BotOwner.Mover.Stop();
            BotOwner.Mover.Sprint(false);
            GetLayer()?.MarkTaskComplete();
        }

        private PmcTaskLayer GetLayer()
        {
            PmcTaskLayer layer;
            PmcTaskLayer.LayersByBotId.TryGetValue(BotOwner.Id, out layer);
            return layer;
        }

        private static bool TryRandomNavPoint(Vector3 center, float radius, out Vector3 result)
        {
            for (int i = 0; i < 8; i++)
            {
                float angle = UnityEngine.Random.Range(0f, 360f) * Mathf.Deg2Rad;
                float dist  = UnityEngine.Random.Range(5f, radius);
                Vector3 candidate = center + new Vector3(
                    Mathf.Cos(angle) * dist, 0f, Mathf.Sin(angle) * dist);
                NavMeshHit hit;
                if (NavMesh.SamplePosition(candidate, out hit, 5f, NavMesh.AllAreas))
                {
                    result = hit.position;
                    return true;
                }
            }
            result = center;
            return false;
        }
    }
}
