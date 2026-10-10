using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace ZompiercerLAN
{
    // The partner on the tablet's map (1.2.2): a copy of the game's own player marker, tinted, placed
    // with the tablet's own world-to-map mapping, shown when the game shows the player there
    // (DifficultyManager.ShowPlayerOnMap) and the partner is in this world. Main thread only.
    internal static class LanMapMarker
    {
        private static readonly Color Tint = new Color(1f, .55f, .1f, 1f);
        private static Harmony _harmony;
        private static Action<string> _log;
        private static bool _logged;
        private static MethodInfo _posOnMap;
        private static RectTransform _marker, _source;
        internal static string Failure { get; private set; }
        // Set by the plugin every frame: where the partner stands in this world (null: not here).
        internal static Func<Vector3?> Partner;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.map");
            try
            {
                _posOnMap = AccessTools.Method(typeof(Zompiercer.GUI.TabletPC), "GetPosOnMap");
                if (_posOnMap == null) throw new MissingMethodException("TabletPC.GetPosOnMap");
                _harmony.Patch(AccessTools.Method(typeof(Zompiercer.GUI.TabletPC), "Update"), postfix: new HarmonyMethod(typeof(LanMapMarker), nameof(UpdatePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(Zompiercer.GUI.TabletPC), "LateUpdate"), postfix: new HarmonyMethod(typeof(LanMapMarker), nameof(LateUpdatePostfix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                _harmony.UnpatchSelf();
                log("Map marker hook unavailable: " + Failure);
            }
        }
        internal static void Shutdown()
        {
            _harmony?.UnpatchSelf(); _harmony = null; Partner = null;
            if (_marker != null) UnityEngine.Object.Destroy(_marker.gameObject);
            _marker = null; _source = null;
        }

        private static void UpdatePostfix(Zompiercer.GUI.TabletPC __instance)
        {
            try
            {
                var source = __instance.MapMarkerPlayer;
                if (source == null) return;
                var at = Partner == null ? null : Partner();
                bool show = at != null && DifficultyManager.Global != null && DifficultyManager.Global.ShowPlayerOnMap;
                if (!show) { if (_marker != null) _marker.gameObject.SetActive(false); return; }
                if (_marker == null || _source != source)
                {
                    if (_marker != null) UnityEngine.Object.Destroy(_marker.gameObject);
                    var copy = UnityEngine.Object.Instantiate(source.gameObject, source.parent, false);
                    copy.name = "LAN partner marker";
                    foreach (var graphic in copy.GetComponentsInChildren<Graphic>(true)) graphic.color = Tint;
                    _marker = copy.GetComponent<RectTransform>(); _source = source;
                }
                _marker.localPosition = (Vector3)_posOnMap.Invoke(__instance, new object[] { at.Value });
                _marker.gameObject.SetActive(true);
            }
            catch (Exception ex)
            {
                if (!_logged) { _logged = true; _log?.Invoke("Partner map marker failed: " + ex.GetType().Name + ": " + ex.Message); }
            }
        }
        private static void LateUpdatePostfix()
        {
            if (_marker != null && _source != null) _marker.localScale = _source.localScale;
        }
    }
}
