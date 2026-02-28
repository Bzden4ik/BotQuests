using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace BotOverlay
{
    [BepInPlugin("BotOverlay.UniqueGUID", "Bot Overlay - AI Task Monitor", "1.0.0")]
    public class BotOverlayPlugin : BaseUnityPlugin
    {
        public static ManualLogSource LogSource;

        private BotOverlayComponent _overlay;

        private void Awake()
        {
            LogSource = Logger;
            LogSource.LogInfo("[BotOverlay] Plugin loaded!");
        }

        private void Update()
        {
            if (_overlay == null)
            {
                _overlay = gameObject.AddComponent<BotOverlayComponent>();
                LogSource.LogInfo("[BotOverlay] BotOverlayComponent attached to plugin GameObject.");
            }
            _overlay.DoUpdate();
        }

        private void OnGUI()
        {
            _overlay?.DoGUI();
        }
    }
}
