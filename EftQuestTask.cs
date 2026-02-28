using System;
using System.Collections.Generic;
using System.Linq;
using Comfort.Common;
using EFT;
using EFT.Interactive;
using EFT.Quests;
using UnityEngine;

namespace ScavTaskMod
{
    // Тип цели для задания «убить»
    public enum EftKillTargetType { PMC, Boss }

    // ── Данные отдельных задач ────────────────────────────────────────
    public class EftVisitTask
    {
        public string   QuestName;
        public string   ZoneId;
        public Vector3  ZonePosition;
        public int      AssignedBotId    = -1;  // слот для Scav-бота
        public int      AssignedPmcBotId = -1;  // слот для PMC-бота (независимый)
    }

    public class EftKillTask
    {
        public string             QuestName;
        public EftKillTargetType  TargetType;
        public string             BossRole;   // не null только для Boss
        public int                AssignedBotId    = -1;
        public int                AssignedPmcBotId = -1;
    }

    public class EftFindTask
    {
        public string         QuestName;
        public string[]       TemplateIds;
        public bool           IsCategory;
        public List<Vector3>  LootPositions = new List<Vector3>();
        public int            AssignedBotId    = -1;
        public int            AssignedPmcBotId = -1;
    }

    // ── Менеджер задач ────────────────────────────────────────────────
    public static class EftQuestTaskManager
    {
        public static bool IsInitialized = false;

        public static readonly List<EftVisitTask> VisitTasks = new List<EftVisitTask>();
        public static readonly List<EftKillTask>  KillTasks  = new List<EftKillTask>();
        public static readonly List<EftFindTask>  FindTasks  = new List<EftFindTask>();

        private static readonly Dictionary<string, Vector3> _zonePositions
            = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        private static float _lootRefreshTime = 0f;
        private const float LOOT_REFRESH_INTERVAL = 60f;

        // Зоны, недоступные по навмешу — не выдаём ботам (сбрасывается каждый рейд)
        public static readonly HashSet<string> BlockedVisitZoneIds =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ── Инициализация в начале рейда ──────────────────────────────
        public static void Initialize()
        {
            IsInitialized = false;
            VisitTasks.Clear();
            KillTasks.Clear();
            FindTasks.Clear();
            _zonePositions.Clear();
            BlockedVisitZoneIds.Clear();

            try
            {
                BuildZonePositions();
                ParsePlayerQuests();

                // Если квестов на убийство нет — добавляем 5 синтетических (убить PMC)
                if (KillTasks.Count == 0)
                {
                    for (int i = 0; i < 5; i++)
                        KillTasks.Add(new EftKillTask
                        {
                            QuestName  = $"[Synthetic] Kill PMC #{i + 1}",
                            TargetType = EftKillTargetType.PMC,
                            BossRole   = null
                        });
                    ScavTaskPlugin.Log.LogInfo("[EftQuestTask] Kill=0 — added 5 synthetic PMC kill tasks");
                }

                RefreshLootPositions(force: true);
                IsInitialized = true;

                ScavTaskPlugin.Log.LogInfo(
                    $"[EftQuestTask] Initialized: Visit={VisitTasks.Count} " +
                    $"Kill={KillTasks.Count} Find={FindTasks.Count}");
            }
            catch (Exception ex)
            {
                ScavTaskPlugin.Log.LogError($"[EftQuestTask] Init error: {ex}");
            }
        }

        // Сбор всех TriggerWithId — зоны квестов на карте
        private static void BuildZonePositions()
        {
            var triggers = UnityEngine.Object.FindObjectsOfType<TriggerWithId>();
            foreach (var t in triggers)
            {
                if (!string.IsNullOrEmpty(t.Id) && !_zonePositions.ContainsKey(t.Id))
                    _zonePositions[t.Id] = t.transform.position;
            }
            ScavTaskPlugin.Log.LogDebug(
                $"[EftQuestTask] Zones on map: {_zonePositions.Count}");
        }

        // Парсим активные квесты игрока для текущей локации
        private static void ParsePlayerQuests()
        {
            var gw = Singleton<GameWorld>.Instance;
            if (gw?.MainPlayer == null) return;

            string locationId = gw.LocationId?.ToLower();
            if (locationId != null && locationId.StartsWith("factory4")) locationId = "factory4";
            if (locationId != null && locationId.StartsWith("sandbox"))  locationId = "sandbox";

            var qc = gw.MainPlayer.AbstractQuestControllerClass;
            if (qc?.Quests == null) return;

            // Дедупликация по имени квеста на тип задачи
            var seenVisit = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenKill  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var seenFind  = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var quest in qc.Quests.ToList())
            {
                if (quest == null) continue;
                var status = quest.QuestStatus;
                if (status != EQuestStatus.Started &&
                    status != EQuestStatus.AvailableForFinish) continue;

                var tmpl = quest.Template;
                if (tmpl == null) continue;

                // Фильтр по локации
                string questLoc = tmpl.LocationId?.ToLower();
                bool isAny = string.IsNullOrEmpty(questLoc) || questLoc == "any";
                if (!isAny && !string.IsNullOrEmpty(locationId) && questLoc != locationId)
                    continue;

                string questName = tmpl.Name ?? tmpl.Id ?? quest.Id.ToString();

                if (!tmpl.Conditions.ContainsKey(EQuestStatus.AvailableForFinish)) continue;

                foreach (var cond in tmpl.Conditions[EQuestStatus.AvailableForFinish])
                {
                    if (cond == null) continue;

                    // ── Посещение зоны ────────────────────────────────
                    if (cond is ConditionVisitPlace visitCond)
                    {
                        TryAddVisitTask(questName, visitCond.target, seenVisit);
                    }
                    else if (cond is ConditionInZone inZone)
                    {
                        if (inZone.zoneIds != null)
                            foreach (var zid in inZone.zoneIds)
                                TryAddVisitTask(questName, zid, seenVisit);
                    }
                    else if (cond is ConditionLeaveItemAtLocation leaveItem)
                    {
                        TryAddVisitTask(questName, leaveItem.zoneId, seenVisit);
                    }
                    else if (cond is ConditionPlaceBeacon beacon)
                    {
                        TryAddVisitTask(questName, beacon.zoneId, seenVisit);
                    }

                    // ── Убийства ──────────────────────────────────────
                    else if (cond is ConditionKills killCond)
                    {
                        if (seenKill.Contains(questName)) continue;
                        seenKill.Add(questName);
                        string bossRole;
                        EftKillTargetType killType = ResolveKillType(
                            killCond.target, killCond.savageRole, out bossRole);
                        KillTasks.Add(new EftKillTask
                        {
                            QuestName  = questName,
                            TargetType = killType,
                            BossRole   = bossRole
                        });
                    }

                    // ── Найти предмет ─────────────────────────────────
                    else if (cond is ConditionFindItem findCond)
                    {
                        if (findCond.target == null || findCond.target.Length == 0) continue;
                        if (seenFind.Contains(questName)) continue;
                        seenFind.Add(questName);
                        FindTasks.Add(new EftFindTask
                        {
                            QuestName   = questName,
                            TemplateIds = findCond.target,
                            IsCategory  = findCond.TargetIsCategory
                        });
                    }
                    else if (cond is ConditionHandoverItem handover)
                    {
                        // «Принести торговцу» — для бота это «найти предмет»
                        if (handover.target == null || handover.target.Length == 0) continue;
                        if (seenFind.Contains(questName)) continue;
                        seenFind.Add(questName);
                        FindTasks.Add(new EftFindTask
                        {
                            QuestName   = questName,
                            TemplateIds = handover.target,
                            IsCategory  = false
                        });
                    }
                }
            }
        }

        private static void TryAddVisitTask(string questName, string zoneId, HashSet<string> seen)
        {
            if (string.IsNullOrEmpty(zoneId)) return;
            string key = questName + "|" + zoneId;
            if (seen.Contains(key)) return;
            seen.Add(key);

            Vector3 pos;
            if (!_zonePositions.TryGetValue(zoneId, out pos)) return; // зоны нет на карте

            VisitTasks.Add(new EftVisitTask
            {
                QuestName    = questName,
                ZoneId       = zoneId,
                ZonePosition = pos
            });
        }

        // Определяем тип цели убийства:
        //   "Savage" / пусто → ботов-Скавов просят убить «дикого» → Скав-бот идёт убивать PMC
        //   "AnyPmc" / "pmcBot" / "ExUsec" → PMC
        //   boss-роли → Boss
        private static EftKillTargetType ResolveKillType(
            string target, string[] savageRoles, out string bossRole)
        {
            bossRole = null;

            // Сначала проверяем savageRole — там может быть конкретная роль
            if (savageRoles != null)
            {
                foreach (var r in savageRoles)
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    string rl = r.ToLower();
                    if (rl.StartsWith("boss") || rl == "knight" || rl == "bigpipe"
                        || rl == "birdeye" || rl.StartsWith("follower"))
                    {
                        bossRole = r;
                        return EftKillTargetType.Boss;
                    }
                    // Скаву приказано убить другого Скава → переводим в «убить PMC»
                    // (любые другие savageRole = стандартный Скав)
                }
            }

            // Проверяем target
            if (!string.IsNullOrEmpty(target))
            {
                string tl = target.ToLower();
                if (tl.StartsWith("boss") || tl == "knight" || tl == "bigpipe"
                    || tl == "birdeye" || tl.StartsWith("follower"))
                {
                    bossRole = target;
                    return EftKillTargetType.Boss;
                }
                if (tl == "anyPmc" || tl == "anyPmc" || tl.StartsWith("pmc")
                    || tl == "exusec" || tl == "arenafighter")
                    return EftKillTargetType.PMC;
            }

            // "Savage" и всё остальное → Скав-бот переосмысляет задачу как «убить PMC»
            return EftKillTargetType.PMC;
        }

        // ── Обновление позиций лута (периодически) ────────────────────
        public static void RefreshLootPositions(bool force = false)
        {
            if (!force && Time.time < _lootRefreshTime) return;
            _lootRefreshTime = Time.time + LOOT_REFRESH_INTERVAL;

            if (FindTasks.Count == 0) return;

            // Собираем позиции лута по TemplateId
            var byTemplate = new Dictionary<string, List<Vector3>>(
                StringComparer.OrdinalIgnoreCase);
            var containerPositions = new List<Vector3>();

            try
            {
                var lootItems = UnityEngine.Object.FindObjectsOfType<LootItem>();
                foreach (var li in lootItems)
                {
                    if (li?.Item == null) continue;
                    if (li.Item.QuestItem) continue; // квестовые предметы пропускаем
                    string tid = li.TemplateId;
                    if (string.IsNullOrEmpty(tid)) continue;
                    if (!byTemplate.ContainsKey(tid))
                        byTemplate[tid] = new List<Vector3>();
                    byTemplate[tid].Add(li.transform.position);
                }

                var containers = UnityEngine.Object.FindObjectsOfType<LootableContainer>();
                foreach (var lc in containers)
                {
                    if (lc == null || !lc.IsInitialized) continue;
                    containerPositions.Add(lc.transform.position);
                }
            }
            catch (Exception ex)
            {
                ScavTaskPlugin.Log.LogError($"[EftQuestTask] Loot refresh error: {ex.Message}");
                return;
            }

            foreach (var task in FindTasks)
            {
                task.LootPositions.Clear();

                if (task.IsCategory)
                {
                    // Категория предметов → ищем в контейнерах
                    task.LootPositions.AddRange(containerPositions);
                }
                else
                {
                    foreach (var tid in task.TemplateIds)
                    {
                        List<Vector3> positions;
                        if (byTemplate.TryGetValue(tid, out positions))
                            task.LootPositions.AddRange(positions);
                    }
                    // Запасной вариант — добавляем контейнеры (предмет может быть внутри)
                    if (task.LootPositions.Count < 3)
                        task.LootPositions.AddRange(containerPositions.Take(15));
                }
            }

            ScavTaskPlugin.Log.LogDebug(
                $"[EftQuestTask] Loot refreshed. Find tasks: {FindTasks.Count}");
        }

        public static void Tick()
        {
            if (!IsInitialized) return;
            RefreshLootPositions();
        }

        // ── Получение задачи для бота ─────────────────────────────────

        public static bool TryGetVisitTask(Vector3 botPos, int botId, out EftVisitTask task)
        {
            task = null;
            EftVisitTask best = null;
            float bestDist = float.MaxValue;
            foreach (var t in VisitTasks)
            {
                if (t.AssignedBotId != -1 && t.AssignedBotId != botId) continue;
                if (BlockedVisitZoneIds.Contains(t.ZoneId)) continue;
                float d = Vector3.Distance(botPos, t.ZonePosition);
                if (d < bestDist) { bestDist = d; best = t; }
            }
            if (best == null) return false;
            best.AssignedBotId = botId;
            task = best;
            return true;
        }

        public static bool TryGetKillTask(int botId, out EftKillTask task)
        {
            task = null;
            foreach (var t in KillTasks)
            {
                if (t.AssignedBotId != -1 && t.AssignedBotId != botId) continue;
                t.AssignedBotId = botId;
                task = t;
                return true;
            }
            return false;
        }

        public static bool TryGetFindTask(Vector3 botPos, int botId, out EftFindTask task)
        {
            task = null;
            EftFindTask best = null;
            float bestDist = float.MaxValue;
            foreach (var t in FindTasks)
            {
                if (t.AssignedBotId != -1 && t.AssignedBotId != botId) continue;
                if (t.LootPositions.Count == 0) continue;
                float d = t.LootPositions.Min(p => Vector3.Distance(botPos, p));
                if (d < bestDist) { bestDist = d; best = t; }
            }
            if (best == null) return false;
            best.AssignedBotId = botId;
            task = best;
            return true;
        }

        // Освобождение задач когда Scav-бот завершил или умер
        public static void ReleaseTasksForBot(int botId)
        {
            foreach (var t in VisitTasks) if (t.AssignedBotId == botId) t.AssignedBotId = -1;
            foreach (var t in KillTasks)  if (t.AssignedBotId == botId) t.AssignedBotId = -1;
            foreach (var t in FindTasks)  if (t.AssignedBotId == botId) t.AssignedBotId = -1;
        }

        // Сколько Scav-ботов назначено на каждый тип задания
        public static int GetVisitAssignedCount() => VisitTasks.Count(t => t.AssignedBotId != -1);
        public static int GetKillAssignedCount()  => KillTasks.Count(t => t.AssignedBotId != -1);
        public static int GetFindAssignedCount()  => FindTasks.Count(t => t.AssignedBotId != -1);

        // ── PMC-боты: независимые слоты (не конкурируют со Scav-ботами) ──

        public static bool TryGetVisitTaskForPmc(Vector3 botPos, int botId, out EftVisitTask task)
        {
            task = null;
            EftVisitTask best = null;
            float bestDist = float.MaxValue;
            foreach (var t in VisitTasks)
            {
                if (t.AssignedPmcBotId != -1 && t.AssignedPmcBotId != botId) continue;
                if (BlockedVisitZoneIds.Contains(t.ZoneId)) continue;
                float d = Vector3.Distance(botPos, t.ZonePosition);
                if (d < bestDist) { bestDist = d; best = t; }
            }
            if (best == null) return false;
            best.AssignedPmcBotId = botId;
            task = best;
            return true;
        }

        public static bool TryGetKillTaskForPmc(int botId, out EftKillTask task)
        {
            task = null;
            foreach (var t in KillTasks)
            {
                if (t.AssignedPmcBotId != -1 && t.AssignedPmcBotId != botId) continue;
                t.AssignedPmcBotId = botId;
                task = t;
                return true;
            }
            return false;
        }

        public static bool TryGetFindTaskForPmc(Vector3 botPos, int botId, out EftFindTask task)
        {
            task = null;
            EftFindTask best = null;
            float bestDist = float.MaxValue;
            foreach (var t in FindTasks)
            {
                if (t.AssignedPmcBotId != -1 && t.AssignedPmcBotId != botId) continue;
                if (t.LootPositions.Count == 0) continue;
                float d = t.LootPositions.Min(p => Vector3.Distance(botPos, p));
                if (d < bestDist) { bestDist = d; best = t; }
            }
            if (best == null) return false;
            best.AssignedPmcBotId = botId;
            task = best;
            return true;
        }

        // Освобождение слотов PMC-бота
        public static void ReleasePmcTasksForBot(int botId)
        {
            foreach (var t in VisitTasks) if (t.AssignedPmcBotId == botId) t.AssignedPmcBotId = -1;
            foreach (var t in KillTasks)  if (t.AssignedPmcBotId == botId) t.AssignedPmcBotId = -1;
            foreach (var t in FindTasks)  if (t.AssignedPmcBotId == botId) t.AssignedPmcBotId = -1;
        }
    }
}
