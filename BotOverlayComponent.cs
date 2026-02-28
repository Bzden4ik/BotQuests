using System;
using System.Collections.Generic;
using System.Reflection;
using Comfort.Common;
using EFT;
using UnityEngine;
using UnityEngine.Rendering;

namespace BotOverlay
{
    /// <summary>
    /// Рисует оверлей с состоянием ботов, путями (3D GL-линии в мире), историей задач.
    /// F9  — показать/скрыть оверлей
    /// F8  — показать/скрыть мёртвых ботов
    /// F7  — показать/скрыть пути ботов (3D линии в мире)
    /// [/] — перебор ботов (телепорт камеры)
    /// 9/0 — перебор целей заданий (телепорт камеры)
    /// </summary>
    public class BotOverlayComponent : MonoBehaviour
    {
        // ────────────────── Настройки UI ──────────────────
        private const float PANEL_X     = 10f;
        private const float PANEL_Y     = 10f;
        private const float PANEL_W     = 470f;
        private const float HIST_X      = 490f;   // панель истории правее основной
        private const float HIST_W      = 310f;
        private const float ROW_H       = 17f;
        private const float PAD         = 6f;
        private const float UPDATE_INT  = 0.25f;

        private bool  _visible      = true;
        private bool  _showDead     = false;
        private bool  _showPaths    = true;   // F7
        private float _nextUpdate   = 0f;

        private readonly List<BotEntry> _entries  = new List<BotEntry>();
        private List<BotEntry>          _aliveBots = new List<BotEntry>();
        private List<BotEntry>          _taskBots  = new List<BotEntry>();

        // Постоянные номера ботов: BotId → порядковый номер
        private static readonly Dictionary<int, int> _botNumbers = new Dictionary<int, int>();
        private static int _nextBotNumber = 1;

        // Keyboard nav
        private int _selectedBotIdx  = -1;   // индекс в _aliveBots
        private int _selectedTaskIdx = -1;   // индекс в _taskBots

        // GUI стили
        private GUIStyle _boxStyle, _titleStyle, _rowStyle;
        private bool     _stylesReady;

        // GL для рисования путей
        private Material _glMaterial;

        // Цвета по типу решения
        private static readonly (string key, Color col)[] _decisionPalette =
        {
            ("attack",   new Color(1f, 0.2f, 0.2f)),
            ("shoot",    new Color(1f, 0.3f, 0.3f)),
            ("run",      new Color(1f, 0.6f, 0.1f)),
            ("grenade",  new Color(1f, 0.5f, 0.0f)),
            ("heal",     new Color(0.3f, 1f, 0.5f)),
            ("cover",    new Color(0.4f, 0.8f, 1f)),
            ("search",   new Color(1f, 1f, 0.3f)),
            ("patrol",   new Color(0.75f, 0.75f, 0.75f)),
            ("peaceful", new Color(0.7f, 0.95f, 0.7f)),
            ("stand",    new Color(0.7f, 0.7f, 1f)),
            ("lay",      new Color(0.6f, 0.9f, 1f)),
        };

        // Цвета путей по типу задания
        private static readonly Dictionary<string, Color> _taskPathColors =
            new Dictionary<string, Color>
            {
                { "SpawnRush",    new Color(1f,   0.25f, 0.25f, 0.9f) },
                { "HuntBoss",     new Color(0.9f, 0.35f, 1f,   0.9f) },
                { "CheckPMCSpawn",new Color(0.2f, 0.8f,  1f,   0.9f) },
            };

        // ─── Рефлексивный доступ к ScavTaskLayer ──────────
        private static bool        _scavReflectionInit     = false;
        private static object      _scavLayersByBotIdDict  = null;
        private static Type        _scavLayerType          = null;
        private static PropertyInfo _scavCurrentTaskProp   = null;
        private static PropertyInfo _scavIsInCooldownProp  = null;
        private static PropertyInfo _scavCachedTargetProp  = null;
        private static FieldInfo   _scavBotPathsField      = null;
        private static FieldInfo   _scavHistoryField       = null;

        private static void EnsureScavReflection()
        {
            if (_scavReflectionInit) return;
            _scavReflectionInit = true;
            try
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    _scavLayerType = asm.GetType("ScavTaskMod.ScavTaskLayer", false);
                    if (_scavLayerType != null) break;
                }
                if (_scavLayerType == null) return;

                var dictField = _scavLayerType.GetField("LayersByBotId",
                    BindingFlags.Public | BindingFlags.Static);
                _scavLayersByBotIdDict = dictField?.GetValue(null);

                _scavCurrentTaskProp  = _scavLayerType.GetProperty("CurrentTask",
                    BindingFlags.Public | BindingFlags.Instance);
                _scavIsInCooldownProp = _scavLayerType.GetProperty("IsInCooldown",
                    BindingFlags.Public | BindingFlags.Instance);
                _scavCachedTargetProp = _scavLayerType.GetProperty("CachedTarget",
                    BindingFlags.Public | BindingFlags.Instance);

                _scavBotPathsField = _scavLayerType.GetField("BotPaths",
                    BindingFlags.Public | BindingFlags.Static);
                _scavHistoryField  = _scavLayerType.GetField("TaskHistoryLines",
                    BindingFlags.Public | BindingFlags.Static);
            }
            catch (Exception ex)
            {
                BotOverlayPlugin.LogSource.LogWarning(
                    "[BotOverlay] ScavTask reflection init failed: " + ex.Message);
            }
        }

        // ─── FreeCam телепорт ──────────────────────────────
        private static void TeleportFreecamToBot(Vector3 pos)
        {
            try
            {
                Type freecamType = null;
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    freecamType = asm.GetType("Terkoiz.Freecam.Freecam", false);
                    if (freecamType != null) break;
                }
                if (freecamType == null) return;

                var freecamObj = UnityEngine.Object.FindObjectOfType(freecamType) as MonoBehaviour;
                if (freecamObj == null) return;

                var isActiveField = freecamType.GetField("IsActive",
                    BindingFlags.Public | BindingFlags.Instance);
                bool isActive = isActiveField != null && (bool)isActiveField.GetValue(freecamObj);
                if (!isActive) return;

                freecamObj.transform.position = pos + new Vector3(0f, 1.7f, 0f);
                BotOverlayPlugin.LogSource.LogInfo($"[BotOverlay] Camera → {pos}");
            }
            catch (Exception ex)
            {
                BotOverlayPlugin.LogSource.LogWarning(
                    "[BotOverlay] TeleportFreecam: " + ex.Message);
            }
        }

        // ─── Lifecycle ────────────────────────────────────

        private void OnEnable()
        {
            Camera.onPostRender += OnCameraPostRender;
        }

        private void OnDisable()
        {
            Camera.onPostRender -= OnCameraPostRender;
        }

        // Вызывается после каждого рендера камеры — рисуем пути в 3D мире
        private void OnCameraPostRender(Camera cam)
        {
            if (!_showPaths) return;
            // Рисуем только для главной камеры (или свободной камеры freecam)
            if (cam != Camera.main) return;
            DrawWorldPaths(cam);
        }

        // ─── Методы вызываемые из плагина ────────────────

        public void DoUpdate()
        {
            if (Input.GetKeyDown(KeyCode.F9)) _visible   = !_visible;
            if (Input.GetKeyDown(KeyCode.F8)) _showDead  = !_showDead;
            if (Input.GetKeyDown(KeyCode.F7)) _showPaths = !_showPaths;

            // Клавиатурная навигация
            if (Input.GetKeyDown(KeyCode.LeftBracket))  CycleBot(-1);
            if (Input.GetKeyDown(KeyCode.RightBracket)) CycleBot(+1);
            if (Input.GetKeyDown(KeyCode.Alpha9))        CycleTask(-1);
            if (Input.GetKeyDown(KeyCode.Alpha0))        CycleTask(+1);

            if (_visible && Time.time >= _nextUpdate)
            {
                _nextUpdate = Time.time + UPDATE_INT;
                RefreshData();
            }
        }

        public void DoGUI()
        {
            if (!_visible) return;
            EnsureStyles();
            DrawOverlay();
            DrawTaskHistory();
        }

        // ─── Навигация по ботам ───────────────────────────

        private void CycleBot(int dir)
        {
            if (_aliveBots.Count == 0) return;
            _selectedBotIdx = (_selectedBotIdx + dir + _aliveBots.Count) % _aliveBots.Count;
            TeleportFreecamToBot(_aliveBots[_selectedBotIdx].Position);
        }

        private void CycleTask(int dir)
        {
            if (_taskBots.Count == 0) return;
            _selectedTaskIdx = (_selectedTaskIdx + dir + _taskBots.Count) % _taskBots.Count;
            var tb = _taskBots[_selectedTaskIdx];
            Vector3 dest = tb.TaskTargetPos != Vector3.zero ? tb.TaskTargetPos : tb.Position;
            TeleportFreecamToBot(dest);
        }

        // ─── Сбор данных о ботах ──────────────────────────

        private void RefreshData()
        {
            _entries.Clear();
            try
            {
                var world = Singleton<GameWorld>.Instance;
                if (world == null) return;

                foreach (var player in world.AllAlivePlayersList)
                {
                    if (player == null || player.IsYourPlayer || !player.IsAI) continue;
                    var botOwner = player.AIData?.BotOwner;
                    if (botOwner == null) continue;

                    _entries.Add(new BotEntry
                    {
                        Name            = SafeName(player),
                        Role            = SafeRole(player),
                        Decision        = SafeDecision(botOwner),
                        LayerName       = SafeLayerName(botOwner),
                        HasEnemy        = SafeHasEnemy(botOwner),
                        HpPct           = SafeHp(player),
                        IsDead          = false,
                        Position        = player.Position,
                        BotId           = botOwner.Id,
                        BotNumber       = GetOrAssignBotNumber(botOwner.Id),
                        IsSleeping      = SafeIsSleeping(botOwner),
                        HasScavLayer    = SafeScavHasLayer(botOwner),
                        ScavLayerActive = SafeScavIsActive(botOwner),
                        ScavTask        = SafeScavTask(botOwner),
                        TaskTargetPos   = SafeScavTaskTarget(botOwner),
                    });
                }

                if (_showDead)
                {
                    foreach (var player in world.AllPlayersEverExisted)
                    {
                        if (player == null || player.IsYourPlayer || !player.IsAI) continue;
                        bool dead = player.HealthController == null
                                 || !player.HealthController.IsAlive;
                        if (!dead) continue;
                        if (_entries.Exists(e => e.Name == SafeName(player) && !e.IsDead)) continue;
                        _entries.Add(new BotEntry
                        {
                            Name = SafeName(player), Role = SafeRole(player),
                            Decision = "—", IsDead = true
                        });
                    }
                }

                _entries.Sort((a, b) =>
                {
                    if (a.IsDead != b.IsDead) return a.IsDead ? 1 : -1;
                    return string.Compare(a.Name, b.Name, StringComparison.Ordinal);
                });

                _aliveBots = _entries.FindAll(e => !e.IsDead);
                _taskBots  = _entries.FindAll(e =>
                    !e.IsDead && e.HasScavLayer &&
                    !string.IsNullOrEmpty(e.ScavTask) &&
                    !e.ScavTask.Contains("(cd)") &&
                    e.TaskTargetPos != Vector3.zero);

                // Зажимаем индексы
                if (_selectedBotIdx  >= _aliveBots.Count) _selectedBotIdx  = _aliveBots.Count  - 1;
                if (_selectedTaskIdx >= _taskBots.Count)  _selectedTaskIdx = _taskBots.Count - 1;
            }
            catch (Exception ex)
            {
                BotOverlayPlugin.LogSource.LogError("[BotOverlay] RefreshData: " + ex.Message);
            }
        }

        // ─── Безопасные геттеры ───────────────────────────

        private static string SafeName(Player p)
        {
            try { return p.Profile?.Nickname ?? p.name ?? "???"; } catch { return "???"; }
        }
        private static string SafeRole(Player p)
        {
            try { return p.Profile?.Info?.Settings?.Role.ToString() ?? "?"; } catch { return "?"; }
        }
        private static string SafeDecision(BotOwner b)
        {
            try
            {
                var sn = b.Brain?.GetStateName;
                if (!string.IsNullOrEmpty(sn)) return sn;
                var ld = b.Brain?.LastDecision;
                if (ld != null) return ld.ToString();
                return "—";
            }
            catch { return "—"; }
        }
        private static string SafeLayerName(BotOwner b)
        {
            try
            {
                var layer = b.Brain?.BaseBrain?.CurLayerInfo;
                return layer != null ? layer.Name() : string.Empty;
            }
            catch { return string.Empty; }
        }
        private static bool SafeHasEnemy(BotOwner b) { try { return b.Memory?.GoalEnemy != null; } catch { return false; } }

        private static bool SafeIsSleeping(BotOwner b)
        {
            try { return b.StandBy?.StandByType != BotStandByType.active; }
            catch { return false; }
        }

        private static int GetOrAssignBotNumber(int botId)
        {
            if (!_botNumbers.TryGetValue(botId, out int num))
            {
                num = _nextBotNumber++;
                _botNumbers[botId] = num;
            }
            return num;
        }
        private static float SafeHp(Player p)
        {
            try
            {
                if (p.HealthController == null) return 0f;
                float cur = 0f, max = 0f;
                foreach (EBodyPart part in Enum.GetValues(typeof(EBodyPart)))
                {
                    if (part == EBodyPart.Common) continue;
                    var hp = p.HealthController.GetBodyPartHealth(part);
                    cur += hp.Current; max += hp.Maximum;
                }
                return max > 0f ? cur / max * 100f : 0f;
            }
            catch { return 0f; }
        }

        // ─── ScavTask геттеры ──────────────────────────────

        private static bool SafeScavHasLayer(BotOwner b)
        {
            try
            {
                EnsureScavReflection();
                if (_scavLayersByBotIdDict == null) return false;
                return ((System.Collections.IDictionary)_scavLayersByBotIdDict).Contains(b.Id);
            }
            catch { return false; }
        }

        private static bool SafeScavIsActive(BotOwner b)
        {
            try
            {
                var layerInfo = b.Brain?.BaseBrain?.CurLayerInfo;
                return layerInfo?.Name() == "ScavTask";
            }
            catch { return false; }
        }

        private static string SafeScavTask(BotOwner b)
        {
            try
            {
                EnsureScavReflection();
                if (_scavLayersByBotIdDict == null || _scavCurrentTaskProp == null) return string.Empty;
                var dict  = (System.Collections.IDictionary)_scavLayersByBotIdDict;
                var layer = dict[b.Id];
                if (layer == null) return string.Empty;

                string taskName = _scavCurrentTaskProp.GetValue(layer)?.ToString() ?? string.Empty;
                if (taskName == "None") return string.Empty;

                bool inCd = _scavIsInCooldownProp != null &&
                            (bool)(_scavIsInCooldownProp.GetValue(layer) ?? false);
                return inCd ? $"{taskName}(cd)" : taskName;
            }
            catch { return string.Empty; }
        }

        private static Vector3 SafeScavTaskTarget(BotOwner b)
        {
            try
            {
                EnsureScavReflection();
                if (_scavLayersByBotIdDict == null || _scavCachedTargetProp == null) return Vector3.zero;
                var dict  = (System.Collections.IDictionary)_scavLayersByBotIdDict;
                var layer = dict[b.Id];
                if (layer == null) return Vector3.zero;
                return (Vector3)_scavCachedTargetProp.GetValue(layer);
            }
            catch { return Vector3.zero; }
        }

        private string GetBotTaskNameById(int botId)
        {
            try
            {
                if (_scavLayersByBotIdDict == null || _scavCurrentTaskProp == null) return string.Empty;
                var dict  = (System.Collections.IDictionary)_scavLayersByBotIdDict;
                var layer = dict[(object)botId];
                return layer == null ? string.Empty : (_scavCurrentTaskProp.GetValue(layer)?.ToString() ?? string.Empty);
            }
            catch { return string.Empty; }
        }

        // ─── Рисование основного оверлея ─────────────────

        private void DrawOverlay()
        {
            int shown = _entries.Count;
            float panelH = PAD * 2 + ROW_H * 2 + (ROW_H + 2f) * shown + 4f;
            var  panelRect = new Rect(PANEL_X, PANEL_Y, PANEL_W, panelH);

            GUI.Box(panelRect, GUIContent.none, _boxStyle);

            float x = PANEL_X + PAD;
            float y = PANEL_Y + PAD;

            // Заголовок
            string selBotHint  = _selectedBotIdx  >= 0 && _selectedBotIdx  < _aliveBots.Count
                ? $"→{_aliveBots[_selectedBotIdx].Name}" : "";
            string selTaskHint = _selectedTaskIdx >= 0 && _selectedTaskIdx < _taskBots.Count
                ? $"→{_taskBots[_selectedTaskIdx].ScavTask}" : "";

            GUI.Label(new Rect(x, y, PANEL_W - PAD * 2, ROW_H),
                $"BOT OVERLAY [{shown}]  F9=hide F8=dead  [/]=бот{(selBotHint!=""?" "+selBotHint:"")}  9/0=цель{(selTaskHint!=""?" "+selTaskHint:"")}",
                _titleStyle);
            y += ROW_H + 2f;
            DrawHLine(x, y, PANEL_W - PAD * 2);
            y += 5f;

            if (shown == 0)
            {
                GUI.Label(new Rect(x, y, PANEL_W - PAD * 2, ROW_H),
                    "  нет живых ботов в мире", _rowStyle);
                return;
            }

            int aliveIdx = 0;
            for (int i = 0; i < shown; i++)
            {
                var e = _entries[i];

                // Фон строки
                Color bg;
                if (!e.IsDead && _selectedBotIdx >= 0 && aliveIdx == _selectedBotIdx)
                    bg = new Color(0.1f, 0.35f, 0.1f, 0.75f);   // выделенный бот
                else if (!e.IsDead && e.IsSleeping)
                    bg = new Color(0.05f, 0.05f, 0.18f, 0.80f);  // спящий — тёмно-синеватый
                else
                    bg = i % 2 == 0
                        ? new Color(0.05f, 0.05f, 0.1f, 0.65f)
                        : new Color(0.1f,  0.1f,  0.15f, 0.45f);

                FillRect(new Rect(PANEL_X + 2f, y, PANEL_W - 4f, ROW_H), bg);

                if (e.IsDead)
                {
                    GUI.color = new Color(0.5f, 0.5f, 0.5f);
                    GUI.Label(new Rect(x, y, PANEL_W - PAD * 2, ROW_H),
                        $"  ✕  {e.Name}  [{e.Role}]  DEAD", _rowStyle);
                    GUI.color = Color.white;
                }
                else
                {
                    // Клик → телепорт к боту
                    if (GUI.Button(new Rect(PANEL_X + 2f, y, PANEL_W - 4f, ROW_H),
                        GUIContent.none, GUIStyle.none))
                        TeleportFreecamToBot(e.Position);

                    // Маркер «враг»
                    GUI.color = e.HasEnemy ? new Color(1f, 0.3f, 0.3f) : new Color(0.3f, 1f, 0.4f);
                    GUI.Label(new Rect(x, y, 10f, ROW_H), e.HasEnemy ? "◉" : "○", _rowStyle);
                    GUI.color = Color.white;

                    // [N] номер бота
                    GUI.color = new Color(0.6f, 0.6f, 0.9f);
                    GUI.Label(new Rect(x + 12f, y, 32f, ROW_H), $"[{e.BotNumber}]", _rowStyle);
                    GUI.color = Color.white;

                    // Имя [роль]
                    Color nameCol = e.IsSleeping ? new Color(0.5f, 0.5f, 0.6f) : Color.white;
                    GUI.color = nameCol;
                    GUI.Label(new Rect(x + 44f, y, 155f, ROW_H),
                        $"{e.Name}  <color=#999>[{e.Role}]</color>", _rowStyle);
                    GUI.color = Color.white;

                    // Индикатор сна — справа от имени, перед решением
                    if (e.IsSleeping)
                    {
                        GUI.color = new Color(0.5f, 0.55f, 1f, 0.9f);
                        GUI.Label(new Rect(x + 200f, y, 38f, ROW_H), "💤Zz", _rowStyle);
                        GUI.color = Color.white;
                    }
                    else
                    {
                        // Решение / слой
                        string displayDecision = string.IsNullOrEmpty(e.LayerName)
                            ? e.Decision
                            : $"{e.Decision}  <color=#aaa>({e.LayerName})</color>";

                        GUI.color = e.ScavLayerActive
                            ? new Color(1f, 0.85f, 0f)
                            : GetDecisionColor(e.Decision);
                        GUI.Label(new Rect(x + 200f, y, 195f, ROW_H), displayDecision, _rowStyle);
                        GUI.color = Color.white;
                    }

                    // ScavTask индикатор
                    if (e.HasScavLayer)
                    {
                        string scavLabel;
                        Color  scavCol;
                        bool   isTaskTarget = _selectedTaskIdx >= 0 &&
                                              _selectedTaskIdx < _taskBots.Count &&
                                              _taskBots[_selectedTaskIdx].Name == e.Name;

                        if (e.ScavLayerActive)
                        { scavLabel = $"[T] {e.ScavTask}"; scavCol = new Color(1f, 0.85f, 0f); }
                        else if (!string.IsNullOrEmpty(e.ScavTask))
                        { scavLabel = $"[T] {e.ScavTask}"; scavCol = new Color(0.7f, 0.7f, 0.7f); }
                        else
                        { scavLabel = "[T] idle";          scavCol = new Color(0.5f, 0.5f, 0.5f); }

                        if (isTaskTarget) scavCol = new Color(0.3f, 1f, 1f);

                        if (e.IsSleeping) scavCol = new Color(scavCol.r * 0.5f, scavCol.g * 0.5f, scavCol.b * 0.5f);

                        GUI.color = scavCol;
                        GUI.Label(new Rect(x + 390f, y, 75f, ROW_H), scavLabel, _rowStyle);
                        GUI.color = Color.white;
                    }
                    else
                    {
                        Color hpCol = e.HpPct > 60f ? Color.green : e.HpPct > 30f ? Color.yellow : Color.red;
                        if (e.IsSleeping) hpCol = new Color(hpCol.r * 0.5f, hpCol.g * 0.5f, hpCol.b * 0.5f);
                        GUI.color = hpCol;
                        GUI.Label(new Rect(x + 425f, y, 38f, ROW_H), $"{e.HpPct:F0}%", _rowStyle);
                        GUI.color = Color.white;
                    }

                    aliveIdx++;
                }

                y += ROW_H + 2f;
            }
        }

        // ─── Панель истории выполненных задач ────────────

        private void DrawTaskHistory()
        {
            EnsureScavReflection();
            if (_scavHistoryField == null) return;
            var lines = _scavHistoryField.GetValue(null) as List<string>;
            if (lines == null || lines.Count == 0) return;

            int count = Mathf.Min(lines.Count, 15);
            float histH = PAD * 2 + ROW_H * 1.5f + (ROW_H + 2f) * count + 4f;
            GUI.Box(new Rect(HIST_X, PANEL_Y, HIST_W, histH), GUIContent.none, _boxStyle);

            float x = HIST_X + PAD;
            float y = PANEL_Y + PAD;

            GUI.Label(new Rect(x, y, HIST_W - PAD * 2, ROW_H),
                "ВЫПОЛНЕННЫЕ ЗАДАНИЯ", _titleStyle);
            y += ROW_H + 2f;
            DrawHLine(x, y, HIST_W - PAD * 2);
            y += 5f;

            for (int i = 0; i < count; i++)
            {
                float alpha = 1f - (i / (float)count) * 0.55f;
                GUI.color = new Color(0.4f, 1f, 0.55f, alpha);
                GUI.Label(new Rect(x, y, HIST_W - PAD * 2, ROW_H), lines[i], _rowStyle);
                GUI.color = Color.white;
                y += ROW_H + 2f;
            }
        }

        // ─── 3D пути ботов (рисуются в Camera.onPostRender) ──

        private void DrawWorldPaths(Camera cam)
        {
            EnsureScavReflection();
            if (_scavBotPathsField == null) return;

            var allPathsObj = _scavBotPathsField.GetValue(null);
            if (allPathsObj == null) return;
            var allPaths = allPathsObj as System.Collections.IDictionary;
            if (allPaths == null || allPaths.Count == 0) return;

            var mat = GetGLMaterial();
            if (mat == null) return;

            mat.SetPass(0);
            GL.PushMatrix();

            // Projection + View раздельно — GL.Vertex() принимает мировые координаты
            GL.LoadProjectionMatrix(cam.projectionMatrix);
            GL.modelview = cam.worldToCameraMatrix;

            GL.Begin(GL.LINES);

            foreach (System.Collections.DictionaryEntry kv in allPaths)
            {
                int botId = (int)kv.Key;
                var path  = kv.Value as List<Vector3>;
                if (path == null || path.Count < 2) continue;

                string taskName = GetBotTaskNameById(botId);
                Color  col      = _taskPathColors.TryGetValue(taskName, out var tc)
                    ? tc : new Color(0.4f, 1f, 0.4f, 0.9f);

                // Сегменты пути: яркость нарастает к концу (начало = бледнее)
                for (int i = 1; i < path.Count; i++)
                {
                    float t     = (float)i / path.Count;
                    float alpha = col.a * (0.2f + 0.8f * t);
                    GL.Color(new Color(col.r, col.g, col.b, alpha));

                    // +0.15м над землёй чтобы не тонуло в текстуре
                    var p0 = path[i - 1] + Vector3.up * 0.15f;
                    var p1 = path[i]     + Vector3.up * 0.15f;
                    GL.Vertex(p0);
                    GL.Vertex(p1);
                }

                // 3D крест на последней точке (позиция бота)
                if (path.Count > 0)
                {
                    GL.Color(new Color(col.r, col.g, col.b, 1f));
                    var end = path[path.Count - 1] + Vector3.up * 0.3f;
                    const float S = 0.6f;  // размер метки в метрах
                    GL.Vertex(end + Vector3.left    * S); GL.Vertex(end + Vector3.right   * S);
                    GL.Vertex(end + Vector3.forward * S); GL.Vertex(end + Vector3.back    * S);
                    GL.Vertex(end);                       GL.Vertex(end + Vector3.up      * S * 2f);
                }
            }

            GL.End();
            GL.PopMatrix();
        }

        private Material GetGLMaterial()
        {
            if (_glMaterial != null) return _glMaterial;
            try
            {
                var shader = Shader.Find("Hidden/Internal-Colored")
                          ?? Shader.Find("Sprites/Default");
                if (shader == null) return null;
                _glMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
                _glMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
                _glMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
                _glMaterial.SetInt("_Cull",     (int)CullMode.Off);
                _glMaterial.SetInt("_ZWrite",   0);
                // ZTest = Always (8): линии видны сквозь стены и terrain
                _glMaterial.SetInt("_ZTest",    (int)CompareFunction.Always);
            }
            catch { }
            return _glMaterial;
        }

        // ─── Вспомогательные методы рисования ─────────────

        private void DrawHLine(float x, float y, float w)
        {
            GUI.color = new Color(1f, 1f, 1f, 0.25f);
            GUI.DrawTexture(new Rect(x, y, w, 1f), Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private void FillRect(Rect r, Color c)
        {
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = Color.white;
        }

        private Color GetDecisionColor(string dec)
        {
            if (string.IsNullOrEmpty(dec)) return Color.white;
            string lo = dec.ToLowerInvariant();
            foreach (var (key, col) in _decisionPalette)
                if (lo.Contains(key)) return col;
            return new Color(0.9f, 0.9f, 0.9f);
        }

        // ─── Инициализация стилей ──────────────────────────

        private void EnsureStyles()
        {
            if (_stylesReady) return;
            _boxStyle = new GUIStyle(GUI.skin.box)
            {
                normal = { background = MakeTex(new Color(0f, 0f, 0f, 0.80f)) }
            };
            _titleStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize  = 12,
                fontStyle = FontStyle.Bold,
                richText  = true,
                normal    = { textColor = new Color(1f, 0.9f, 0.3f) }
            };
            _rowStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 11,
                richText = true,
                normal   = { textColor = Color.white }
            };
            _stylesReady = true;
        }

        private static Texture2D MakeTex(Color c)
        {
            var t = new Texture2D(2, 2);
            t.SetPixels(new[] { c, c, c, c });
            t.Apply();
            return t;
        }

        // ─── Модель данных ─────────────────────────────────

        private class BotEntry
        {
            public string  Name;
            public string  Role;
            public string  Decision;
            public string  LayerName;
            public bool    HasEnemy;
            public float   HpPct;
            public bool    IsDead;
            public Vector3 Position;
            public int     BotId;
            public int     BotNumber;    // постоянный порядковый номер
            public bool    IsSleeping;   // BotStandByType != active
            // ScavTask
            public bool    HasScavLayer;
            public bool    ScavLayerActive;
            public string  ScavTask;
            public Vector3 TaskTargetPos;
        }
    }
}
