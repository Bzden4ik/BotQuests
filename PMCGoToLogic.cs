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

        // ── Fika WIO sync (soft dependency, reflection only) ───────────────────
        private static bool       _fikaChecked;
        private static bool       _fikaReady;
        private static Type       _fikaServerType;
        private static Type       _fikaPlayerType;    // FikaPlayer — для чтения NetId бота
        private static FieldInfo  _fikaNetIdField;    // FikaPlayer.NetId (int)
        private static MethodInfo _fikaFromValue;     // WorldInteractionPacket.FromValue
        private static MethodInfo _fikaSend;          // FikaServer.SendNetReusable<CommonPlayerPacket>
        private static Type       _fikaCommonPktType;
        private static FieldInfo  _fcp_NetId, _fcp_Type, _fcp_SubPacket;
        private static object     _fikaDelivery;      // DeliveryMethod.ReliableOrdered
        private static object     _fikaSubType;       // ECommonSubPacketType.WorldInteraction
        private static object     _eTypeOpen;         // EInteractionType.Open
        private static object     _eStageIgnore;      // EInteractionStage.Ignore

        // ── Фазы ──────────────────────────────────────────────────────────────
        private enum Phase
        {
            Interact,  // Идём к точке взаимодействия и взаимодействуем
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

        // ── Взаимодействие ────────────────────────────────────────────────────
        private InteractPoint _interactPoint;
        private bool          _interactDone;
        private bool          _interactReserved; // слот зарезервирован этим ботом

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

            _point = PointLoader.SelectPoint();  // может быть null если нет основных точек

            _startTime       = Time.time;
            _lastPos         = BotOwner.Position;
            _lastPosTime     = Time.time;
            _lastGoToTime    = -999f;
            _lastDistLogTime = -999f;
            _subs             = null;
            _wanderTargetSet  = false;
            _waitingAtSub     = false;
            _interactDone     = false;
            _interactReserved = false;

            // ── Проверяем доступные точки взаимодействия ──────────────────────
            var interactList = PointLoader.GetInteractPoints();
            _interactPoint = null;
            if (interactList.Count > 0)
            {
                // Если quest-точек нет — interact всегда обязателен (chance не применяем)
                bool forceInteract = (_point == null);
                if (forceInteract || UnityEngine.Random.value <= LifePMCConfig.InteractChance.Value)
                {
                    // Берём только те точки что в пределах MaxInteractDist,
                    // сортируем по дистанции — ближайшие имеют приоритет в занятии слота.
                    float maxDist = LifePMCConfig.MaxInteractDist.Value;
                    var candidates = new List<InteractPoint>();
                    foreach (var ip in interactList)
                    {
                        float d = Vector3.Distance(BotOwner.Position, ip.Position);
                        if (d <= maxDist) candidates.Add(ip);
                    }
                    candidates.Sort((a, b) =>
                        Vector3.Distance(BotOwner.Position, a.Position)
                            .CompareTo(Vector3.Distance(BotOwner.Position, b.Position)));

                    // Пробуем зарезервировать слот у ближайшей доступной точки
                    foreach (var candidate in candidates)
                    {
                        if (PointLoader.TryReserveInteract(candidate.Id, BotOwner.name))
                        {
                            _interactPoint    = candidate;
                            _interactReserved = true;
                            break;
                        }
                    }

                    if (_interactPoint == null && candidates.Count > 0)
                        Log.LogInfo($"[LifePMC] {BotOwner.name} все слоты взаимодействия заняты " +
                                    $"— иду к обычному заданию");
                    else if (candidates.Count == 0 && interactList.Count > 0)
                        Log.LogInfo($"[LifePMC] {BotOwner.name} нет interact-точек в радиусе " +
                                    $"{maxDist:F0}м — иду к обычному заданию");
                }
            }

            // Нет ни основных точек ни interact — завершаем сразу
            if (_point == null && _interactPoint == null)
            {
                Log.LogWarning($"[LifePMC] {BotOwner.name} Start(): нет ни основных точек ни взаимодействий!");
                SetStatus("нет точек");
                _phase = Phase.Done;
                return;
            }

            BotOwner.Mover.SetTargetMoveSpeed(1f);
            BotOwner.Mover.Sprint(true);

            if (_interactPoint != null)
            {
                _phase = Phase.Interact;
                float idist = Vector3.Distance(BotOwner.Position, _interactPoint.Position);
                SetStatus($"[!] {_interactPoint.Description}");
                Log.LogInfo($"[LifePMC] ► {BotOwner.name} идёт к взаимодействию [{_interactPoint.Description}]");
                Log.LogInfo($"[LifePMC]   Цель: ({_interactPoint.X:F1},{_interactPoint.Y:F1},{_interactPoint.Z:F1})  dist={idist:F1}м");
            }
            else
            {
                _phase = Phase.Navigate;
            }

            if (_point != null)
            {
                float dist = Vector3.Distance(BotOwner.Position, _point.Position);
                SetStatus(_interactPoint != null ? $"[!] {_interactPoint.Description}" : $"→ {_point.ZoneName}");
                Log.LogInfo($"[LifePMC] ► {BotOwner.name} НАЧИНАЕТ движение к [{_point.ZoneName}]");
                Log.LogInfo($"[LifePMC]   Цель: ({_point.X:F1}, {_point.Y:F1}, {_point.Z:F1})");
                Log.LogInfo($"[LifePMC]   Дистанция: {dist:F1}м  |  WaitTime: {_point.WaitTime:F0}с");
                Log.LogInfo($"[LifePMC]   Бот позиция: {BotOwner.Position}");
            }
            else
            {
                Log.LogInfo($"[LifePMC] {BotOwner.name} только взаимодействие (основных точек нет)");
            }
        }

        public override void Stop()
        {
            ReleaseInteractSlot();
            BotStatusMap.Remove(BotOwner.name);
        }

        /// <summary>
        /// Освобождает слот взаимодействия для этого бота.
        /// Вызывается при: бой (слой деактивирован), смерть бота, завершение задания.
        /// После боя BigBrain создаст новый Start() — бот снова займёт слот если доступен.
        /// </summary>
        private void ReleaseInteractSlot()
        {
            if (_interactReserved && _interactPoint != null)
            {
                PointLoader.ReleaseInteract(_interactPoint.Id, BotOwner.name);
                _interactReserved = false;
            }
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

                // ── Мгновенный хендофф SAIN при обнаружении угрозы ────────────
                // HaveEnemy или IsUnderFire — немедленно отдаём управление SAIN.
                // IsActive() уже делает это каждый тик, но Complete() здесь
                // гарантирует что бот останавливает движение к точке прямо сейчас.
                try
                {
                    var mem = BotOwner.Memory;
                    if (mem != null && (mem.HaveEnemy || mem.IsUnderFire))
                    {
                        Complete(false);
                        return;
                    }
                }
                catch { }

                // ── Таймаут навигации ──────────────────────────────────────────
                float elapsed = Time.time - _startTime;
                if (elapsed > LifePMCConfig.ObjectiveTimeout.Value)
                {
                    string targetName = _point?.ZoneName ?? _interactPoint?.Description ?? "?";
                    Log.LogWarning($"[LifePMC] {BotOwner.name} ТАЙМАУТ [{targetName}] " +
                                   $"({elapsed:F0}с / {LifePMCConfig.ObjectiveTimeout.Value:F0}с)");
                    Complete(false);
                    return;
                }

                switch (_phase)
                {
                    case Phase.Interact:  UpdateInteract(elapsed);  break;
                    case Phase.Navigate:  UpdateNavigate(elapsed);  break;
                    case Phase.Wander:    UpdateWander();           break;
                }
            }
            catch (Exception ex)
            {
                Log.LogError($"[LifePMC] Update exception ({BotOwner?.name}): {ex.Message}\n{ex.StackTrace}");
                Complete(false);
            }
        }

        // ────────────────────────────────────────────────────────────────────────
        // Phase: Interact — идём к точке взаимодействия, взаимодействуем
        // ────────────────────────────────────────────────────────────────────────
        private void UpdateInteract(float elapsed)
        {
            if (_interactPoint == null) { _phase = Phase.Navigate; return; }

            float dist = Vector3.Distance(BotOwner.Position, _interactPoint.Position);

            if (Time.time - _lastDistLogTime > DIST_LOG_INTERVAL)
            {
                _lastDistLogTime = Time.time;
                Log.LogInfo($"[LifePMC] {BotOwner.name} [!] → [{_interactPoint.Description}]  " +
                            $"dist={dist:F1}м  elapsed={elapsed:F0}с");
            }

            // Анти-застрев
            if (CheckStuck(dist, REACH_DIST)) return;

            if (dist < REACH_DIST)
            {
                // Попытка взаимодействия
                bool ok = TryInteractWithObject(_interactPoint.Position, _interactPoint.SearchRadius);
                if (ok)
                {
                    Log.LogInfo($"[LifePMC] ✓ {BotOwner.name} ВЗАИМОДЕЙСТВОВАЛ с [{_interactPoint.Description}]");
                    if (_interactPoint.OneShot)
                        PointLoader.MarkInteractTriggered(_interactPoint.Id);
                }
                else
                {
                    Log.LogWarning($"[LifePMC] {BotOwner.name} не нашёл интерактивный объект у [{_interactPoint.Description}]  " +
                                   $"(r={_interactPoint.SearchRadius:F1}м)");
                }

                // Переходим к основному заданию (или завершаем если основных точек нет)
                if (_point != null)
                {
                    _phase           = Phase.Navigate;
                    _lastGoToTime    = -999f;
                    _lastDistLogTime = -999f;
                    _lastPos         = BotOwner.Position;
                    _lastPosTime     = Time.time;
                    SetStatus($"→ {_point.ZoneName}");
                    Log.LogInfo($"[LifePMC] {BotOwner.name} продолжает к [{_point.ZoneName}]");
                }
                else
                {
                    Log.LogInfo($"[LifePMC] {BotOwner.name} взаимодействие выполнено, основных точек нет — завершаем");
                    Complete(false);
                }
                return;
            }

            SendGoTo(_interactPoint.Position);
            ApplySprintPosture();
        }

        /// <summary>
        /// Ищет WorldInteractiveObject в радиусе searchRadius от pos и вызывает Interact().
        /// Использует reflection для поддержки обновлений EFT без перекомпиляции.
        /// Возвращает true если взаимодействие выполнено успешно.
        /// </summary>
        private bool TryInteractWithObject(Vector3 pos, float searchRadius)
        {
            try
            {
                // ── Способ 1: точное имя GameObject (записано через WIOPatch в PointEditor) ──
                // ВАЖНО: на карте может быть несколько GO с одним именем (напр. несколько рубильников).
                // Проверяем расстояние — берём только тот что в пределах searchRadius*4 от точки.
                string targetName = _interactPoint.TargetName ?? "";
                if (!string.IsNullOrEmpty(targetName))
                {
                    // Ищем среди ВСЕХ GO с этим именем ближайший к позиции interact-точки
                    GameObject bestNamed  = null;
                    float      bestDist   = float.MaxValue;
                    float      maxAllowed = searchRadius * 4f;

                    // FindObjectsOfType слишком дорого; используем сферу большого радиуса
                    Collider[] wideHits = Physics.OverlapSphere(pos, maxAllowed, ~0, QueryTriggerInteraction.Collide);
                    var seenIds = new HashSet<int>();
                    foreach (var col in wideHits)
                    {
                        if (col == null) continue;
                        Transform t = col.transform;
                        while (t != null)
                        {
                            if (seenIds.Add(t.gameObject.GetInstanceID())
                                && t.gameObject.name == targetName)
                            {
                                float d = Vector3.Distance(pos, t.position);
                                if (d < bestDist) { bestDist = d; bestNamed = t.gameObject; }
                            }
                            t = t.parent;
                        }
                    }

                    Log.LogInfo($"[LifePMC] {BotOwner.name} Поиск по имени [{targetName}] в r={maxAllowed:F0}м → " +
                                (bestNamed != null
                                    ? $"НАЙДЕН  dist={bestDist:F1}м  pos={bestNamed.transform.position}"
                                    : "НЕ НАЙДЕН в радиусе"));

                    if (bestNamed != null)
                    {
                        // Пробуем сам GO и его родителей
                        Transform tr = bestNamed.transform;
                        while (tr != null)
                        {
                            if (TryInteractGO(tr.gameObject)) return true;
                            tr = tr.parent;
                        }
                        Log.LogWarning($"[LifePMC] {BotOwner.name} GO [{targetName}] найден (dist={bestDist:F1}м), " +
                                       "но Interact() не сработал — пробую сферу");
                        // НЕ возвращаем false — fallthrough к сфере
                    }
                    // Fallthrough: объект не найден по имени / interact провалился — пробуем сферой
                }

                // ── Способ 2: Physics.OverlapSphere (fallback если имя не задано / не найдено) ──
                // ~0 = все слои; QueryTriggerInteraction.Collide = включая триггеры
                Collider[] hits = Physics.OverlapSphere(pos, searchRadius, ~0, QueryTriggerInteraction.Collide);
                Log.LogInfo($"[LifePMC] {BotOwner.name} TryInteract сфера: pos={pos}  " +
                            $"r={searchRadius}м  коллайдеров={hits.Length}");

                // Собираем все уникальные GO + родителей
                // (WorldInteractiveObject обычно висит на родителе коллайдера)
                var checkedGoIds = new HashSet<int>();
                var goList       = new List<GameObject>();

                foreach (var col in hits)
                {
                    if (col == null) continue;
                    Transform t = col.transform;
                    while (t != null)
                    {
                        if (checkedGoIds.Add(t.gameObject.GetInstanceID()))
                            goList.Add(t.gameObject);
                        t = t.parent;
                    }
                }
                Log.LogInfo($"[LifePMC] {BotOwner.name}   уникальных GO={goList.Count}");

                // Диагностика: все уникальные типы MonoBehaviour-компонентов в сфере
                var allTypes = new HashSet<string>();
                foreach (var go in goList)
                    foreach (var c in go.GetComponents<MonoBehaviour>())
                        if (c != null) allTypes.Add(c.GetType().Name);
                Log.LogInfo($"[LifePMC] {BotOwner.name}   типы компонентов: {string.Join(", ", allTypes)}");

                foreach (var go in goList)
                    if (TryInteractGO(go)) return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[LifePMC] TryInteractWithObject exception: {ex.Message}");
            }
            return false;
        }

        /// <summary>
        /// Пытается вызвать Interact() на MonoBehaviour с Operatable=true
        /// на конкретном GameObject.
        /// Если Operatable не найден — пробует любой метод Interact(1 arg).
        /// Возвращает true при успехе.
        /// </summary>
        private bool TryInteractGO(GameObject go)
        {
            if (go == null) return false;

            MonoBehaviour[] components = go.GetComponents<MonoBehaviour>();
            if (components == null) return false;

            // ── Проход 1: ищем компонент с Operatable=true ──
            // ВАЖНО: Operatable — это ПОЛЕ (public bool Operatable = true),
            // а не property! Используем GetField + FlattenHierarchy.
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var compType = comp.GetType();

                // Operatable объявлен в WorldInteractiveObject — нужен FlattenHierarchy
                FieldInfo opField;
                try
                {
                    opField = compType.GetField("Operatable",
                        BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                }
                catch { continue; }
                if (opField == null) continue;

                bool operable;
                try { operable = (bool)opField.GetValue(comp); }
                catch { continue; }

                Log.LogInfo($"[LifePMC]   Operatable={operable}  go={go.name}  comp={compType.Name}");
                if (!operable) continue;

                if (CallInteract(comp, compType, go.name)) return true;
            }

            // ── Проход 2: ищем любой Interact(1 arg) если Operatable не нашли ──
            // (на случай если свойство переименовано в данной версии EFT)
            foreach (var comp in components)
            {
                if (comp == null) continue;
                var compType = comp.GetType();

                MethodInfo m;
                try
                {
                    m = compType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                .FirstOrDefault(x => x.Name == "Interact" && x.GetParameters().Length == 1);
                }
                catch { continue; }
                if (m == null) continue;

                Log.LogInfo($"[LifePMC]   [fallback] нашёл Interact без Operatable: " +
                            $"go={go.name}  comp={compType.Name}  " +
                            $"param={m.GetParameters()[0].ParameterType.Name}");

                if (CallInteract(comp, compType, go.name)) return true;
            }

            return false;
        }

        /// <summary>Вызывает Interact(InteractionResult) через reflection. true = успех.</summary>
        private bool CallInteract(MonoBehaviour comp, Type compType, string goName)
        {
            MethodInfo interactMethod;
            try
            {
                interactMethod = compType.GetMethods(BindingFlags.Public | BindingFlags.Instance)
                                         .FirstOrDefault(m => m.Name == "Interact"
                                                           && m.GetParameters().Length == 1);
            }
            catch { return false; }
            if (interactMethod == null) return false;

            var parameters = interactMethod.GetParameters();
            var paramType  = parameters[0].ParameterType;

            // Строим аргумент для Interact(InteractionResult).
            // ВАЖНО: InteractionResult — это CLASS (не struct), IsValueType=false,
            // поэтому строим через конструктор независимо от типа.
            object arg = null;
            try
            {
                bool argBuilt = false;
                foreach (var ctor in paramType.GetConstructors())
                {
                    var ctorParams = ctor.GetParameters();
                    if (ctorParams.Length == 1 && ctorParams[0].ParameterType.IsEnum)
                    {
                        var enumType  = ctorParams[0].ParameterType;
                        var enumNames = Enum.GetNames(enumType);
                        Log.LogInfo($"[LifePMC]   EInteractionType: {string.Join(", ", enumNames)}");
                        object enumVal = null;
                        // EInteractionType: Open, Close, Unlock, Breach, Lock, GoIn, GoOut
                        // Для рубильника нужен Open. "Switch" в enum НЕ существует.
                        foreach (var name in new[] { "Open", "Unlock" })
                        {
                            if (Array.IndexOf(enumNames, name) >= 0)
                            { enumVal = Enum.Parse(enumType, name); break; }
                        }
                        if (enumVal == null && enumNames.Length > 0)
                            enumVal = Enum.Parse(enumType, enumNames[0]);
                        if (enumVal != null)
                        {
                            arg = ctor.Invoke(new object[] { enumVal });
                            argBuilt = true;
                            Log.LogInfo($"[LifePMC]   arg: {enumVal}  тип={paramType.Name}");
                        }
                        break;
                    }
                }
                if (!argBuilt && paramType.IsValueType)
                    arg = Activator.CreateInstance(paramType);
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[LifePMC]   ошибка построения arg: {ex.Message}");
                arg = null;
            }

            // Устанавливаем InteractingPlayer перед Interact() —
            // WorldInteractiveObject.method_3() обращается к InteractingPlayer.ProfileId
            // (итерирует TriggersMap), и если InteractingPlayer=null → NullReferenceException
            try
            {
                var ipProp = compType.GetProperty("InteractingPlayer",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                if (ipProp != null && ipProp.CanWrite)
                {
                    var botPlayer = BotOwner.GetPlayer;
                    if (botPlayer != null)
                    {
                        ipProp.SetValue(comp, botPlayer);
                        Log.LogInfo($"[LifePMC]   InteractingPlayer → {botPlayer.ProfileId}");
                    }
                }
            }
            catch (Exception exIp)
            {
                Log.LogWarning($"[LifePMC]   InteractingPlayer set: {exIp.Message}");
            }

            try
            {
                interactMethod.Invoke(comp, new object[] { arg });
                Log.LogInfo($"[LifePMC] ✓ {BotOwner.name} Interact() → {goName} ({compType.Name})");
                TryBroadcastFikaInteract(comp);
                return true;
            }
            catch (Exception ex)
            {
                var inner = ex.InnerException ?? ex;
                Log.LogWarning($"[LifePMC]   Interact() на {goName} ({compType.Name}): " +
                               $"{inner.GetType().Name}: {inner.Message}");
                if (inner.StackTrace != null)
                    Log.LogWarning($"[LifePMC]   Stack: {inner.StackTrace.Split('\n')[0].Trim()}");
                return false;
            }
        }

        // ── Fika: инициализация reflection-кэша ──────────────────────────────────
        private static bool EnsureFika()
        {
            if (_fikaChecked) return _fikaReady;
            _fikaChecked = true;
            try
            {
                Assembly fikaAsm = null, gameAsm = null;
                foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                {
                    if (a.GetName().Name == "Fika.Core")       fikaAsm = a;
                    else if (a.GetName().Name == "Assembly-CSharp") gameAsm = a;
                }
                if (fikaAsm == null || gameAsm == null) return false;

                _fikaServerType    = fikaAsm.GetType("Fika.Core.Networking.FikaServer");
                _fikaCommonPktType = fikaAsm.GetType(
                    "Fika.Core.Networking.Packets.Player.Common.CommonPlayerPacket");
                var wioType    = fikaAsm.GetType(
                    "Fika.Core.Networking.Packets.Player.Common.SubPackets.WorldInteractionPacket");
                var eSubType   = fikaAsm.GetType(
                    "Fika.Core.Networking.Packets.Player.Common.ECommonSubPacketType");
                var delivType  = fikaAsm.GetType(
                    "Fika.Core.Networking.LiteNetLib.DeliveryMethod");
                var eiType     = gameAsm.GetType("EFT.EInteractionType");
                var eisType    = gameAsm.GetType("EFT.EInteractionStage");

                if (_fikaServerType == null || _fikaCommonPktType == null ||
                    wioType == null || eSubType == null || delivType == null ||
                    eiType == null || eisType == null)
                    return false;

                _fcp_NetId     = _fikaCommonPktType.GetField("NetId",    BindingFlags.Public | BindingFlags.Instance);
                _fcp_Type      = _fikaCommonPktType.GetField("Type",     BindingFlags.Public | BindingFlags.Instance);
                _fcp_SubPacket = _fikaCommonPktType.GetField("SubPacket", BindingFlags.Public | BindingFlags.Instance);

                _fikaFromValue = wioType.GetMethod("FromValue", BindingFlags.Static | BindingFlags.Public);

                _fikaSend = _fikaServerType
                    .GetMethods(BindingFlags.Public | BindingFlags.Instance)
                    .FirstOrDefault(m => m.Name == "SendNetReusable" && m.IsGenericMethod
                                        && m.GetParameters().Length == 4)
                    ?.MakeGenericMethod(_fikaCommonPktType);

                if (_fcp_NetId == null || _fcp_Type == null || _fcp_SubPacket == null ||
                    _fikaFromValue == null || _fikaSend == null)
                    return false;

                // FikaPlayer.NetId — читаем NetId бота (на клиенте нужен валидный NetId для TryGetValue)
                _fikaPlayerType  = fikaAsm.GetType("Fika.Core.Main.Players.FikaPlayer");
                _fikaNetIdField  = _fikaPlayerType?.GetField("NetId",
                    BindingFlags.Public | BindingFlags.Instance);

                // EInteractionType.Open=0, EInteractionStage.Ignore=2
                _eTypeOpen    = Enum.ToObject(eiType,   0);
                _eStageIgnore = Enum.ToObject(eisType,  2);
                // DeliveryMethod.ReliableOrdered=2, ECommonSubPacketType.WorldInteraction=1
                _fikaDelivery = Enum.ToObject(delivType, (byte)2);
                _fikaSubType  = Enum.ToObject(eSubType,  (byte)1);

                _fikaReady = true;
                Log.LogInfo("[LifePMC] Fika.Core найден — WIO sync включён");
                return true;
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[LifePMC] Fika init: {ex.Message}");
                return false;
            }
        }

        /// <summary>
        /// После успешного Interact() на headless-сервере отправляет
        /// CommonPlayerPacket(WorldInteraction, Ignore) всем Fika-клиентам,
        /// чтобы они локально вызвали wio.Interact() и включили лампы.
        /// ВАЖНО: NetId в пакете должен быть валидным (NetId бота),
        /// иначе клиент не найдёт игрока в Players dict и Execute() не вызовется.
        /// </summary>
        private void TryBroadcastFikaInteract(MonoBehaviour comp)
        {
            try
            {
                if (!EnsureFika()) return;

                // Ищем FikaServer в сцене (не кэшируем — может смениться между рейдами)
                var server = UnityEngine.Object.FindObjectOfType(_fikaServerType);
                if (server == null) return; // Не headless-сервер

                // Читаем NetId бота: BotOwner.GetPlayer является FikaPlayer (CoopBot),
                // у которого есть публичное поле int NetId
                int netId = 0;
                try
                {
                    var botPlayer = BotOwner.GetPlayer;
                    if (botPlayer != null && _fikaNetIdField != null &&
                        _fikaPlayerType != null && _fikaPlayerType.IsInstanceOfType(botPlayer))
                    {
                        netId = (int)_fikaNetIdField.GetValue(botPlayer);
                    }
                }
                catch { /* оставляем netId=0 */ }

                // Получаем WIO.Id с компонента
                var idField = comp.GetType().GetField("Id",
                    BindingFlags.Public | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
                var wioId = idField != null ? (string)idField.GetValue(comp) : null;
                if (string.IsNullOrEmpty(wioId))
                {
                    Log.LogWarning("[LifePMC] FikaSync: WIO.Id не найден");
                    return;
                }

                // WorldInteractionPacket.FromValue(wioId, Open, Ignore, null)
                var subPkt = _fikaFromValue.Invoke(null,
                    new object[] { wioId, _eTypeOpen, _eStageIgnore, null });
                if (subPkt == null) return;

                // Строим CommonPlayerPacket { NetId=botNetId, Type=WorldInteraction, SubPacket=subPkt }
                var pkt = Activator.CreateInstance(_fikaCommonPktType);
                _fcp_NetId.SetValue(pkt, netId);
                _fcp_Type.SetValue(pkt, _fikaSubType);
                _fcp_SubPacket.SetValue(pkt, subPkt);

                // FikaServer.SendNetReusable<CommonPlayerPacket>(ref pkt, ReliableOrdered, broadcast=true, null)
                _fikaSend.Invoke(server, new object[] { pkt, _fikaDelivery, true, null });

                Log.LogInfo($"[LifePMC] ✓ Fika WIO sync: Id={wioId} NetId={netId} → клиентам");
            }
            catch (Exception ex)
            {
                Log.LogWarning($"[LifePMC] Fika sync: {ex.Message}");
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
            // LookToMovingDirection() убран намеренно:
            // принудительный lock взгляда вперёд мешает EFT/SAIN сканировать угрозы.
            // GoToPoint() управляет движением — SAIN управляет головой.
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
            ReleaseInteractSlot();
            BotStatusMap.Remove(BotOwner.name);
            _layer?.OnObjectiveComplete(wasStuck);
        }

        private void SetStatus(string status)
        {
            BotStatusMap[BotOwner.name] = status;
        }
    }
}
