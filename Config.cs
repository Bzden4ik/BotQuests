using BepInEx.Configuration;

namespace LifePMC
{
    public static class LifePMCConfig
    {
        // ── Тайминги ────────────────────────────────────────────────────────────
        public static ConfigEntry<float> ObjectiveCooldown;
        public static ConfigEntry<float> ObjectiveTimeout;
        public static ConfigEntry<float> CombatCooldown;
        public static ConfigEntry<float> DefaultWaitTime;

        // ── Саб-точки ────────────────────────────────────────────────────────────
        public static ConfigEntry<float> SubPointWaitTime;

        // ── Лимиты ──────────────────────────────────────────────────────────────
        public static ConfigEntry<int> MaxStuck;

        public static void Init(ConfigFile cfg)
        {
            const string T = "LifePMC | Тайминги";
            const string L = "LifePMC | Лимиты";

            ObjectiveCooldown = cfg.Bind(T, "Пауза после цели (сек)", 20f,
                "Задержка перед выбором следующей точки после достижения текущей");

            ObjectiveTimeout = cfg.Bind(T, "Таймаут квеста (сек)", 300f,
                "Максимальное время на достижение одной точки (включая все вейпоинты маршрута)");

            CombatCooldown = cfg.Bind(T, "Пауза после боя (сек)", 30f,
                "Задержка после боя перед возобновлением движения к точке");

            DefaultWaitTime = cfg.Bind(T, "Время ожидания на точке (сек)", 30f,
                "Сколько секунд бот стоит на точке если у неё wait_time = 0");

            SubPointWaitTime = cfg.Bind(T, "Время ожидания на саб-точке (сек)", 15f,
                "Сколько секунд бот стоит на каждой саб-точке если у неё wait_time = 0");

            MaxStuck = cfg.Bind(L, "Макс. застреваний", 5,
                "После N застреваний бот перестаёт выполнять задания на весь рейд");
        }
    }
}
