using System;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Zompiercer.Electricity;
using Zompiercer.Inventory;
using Zompiercer.Paint;
using Zompiercer.Train;
using Object = UnityEngine.Object;

namespace ZompiercerLAN
{
    // Paint, wires and signs of the host's train (1.4.0). The train layout carries what the host's
    // objects look like (TrainDecor): painted surfaces, the inputs fed by each part's switch, sign
    // texts; the guest's copy shows them. The guest paints, wires and letters in the game's own
    // paint mode, wire mode and sign window on that copy, and every change goes to the host as a
    // request: the host changes its own train and the next layout shows the result to both. Nothing
    // is spent: the game's paint and wires are free. Main thread only.
    internal static class LanTrainDecor
    {
        private const float PaintReach = 22f, WireReach = 30f, SignReach = 6f, PaintRay = 20f;
        private static Harmony _harmony;
        private static Action<string> _log;
        internal static string Failure { get; private set; }
        // Guest: sends a request (false: not sent); null outside a usable session.
        internal static Func<LanStorage.DecorRequest, Action<byte>, bool> GuestRequest;
        // Guest: the host's layout is being applied (its own wiring calls are not requests).
        private static bool _applying, _quiet;
        // Private fields of the game's paint code (1.4.1: looked up in Initialize; when one is missing
        // the paint features stay off and everything else works).
        private static AccessTools.FieldRef<Paintable, Material> OriginalOf;
        private static AccessTools.FieldRef<PaintManager, Paintable> HoveredOf;
        private static AccessTools.FieldRef<PaintManager, Renderer> SelectedOf;
        private static System.Reflection.FieldInfo MainCanvas;
        // 1.4.1: how deep switches may feed switches (a wire loop would never end).
        private const int MaxSwitchDepth = 16;
        private static int _switchDepth;
        private static bool _loopLogged, _captureLogged;

        internal static void Initialize(Action<string> log)
        {
            _log = log;
            if (_harmony != null || Failure != null) return;
            _harmony = new Harmony("local.zompiercer.lan.train-decor");
            try { _harmony.Patch(AccessTools.Method(typeof(LogicOutput), "SetState"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(SwitchPrefix)), finalizer: new HarmonyMethod(typeof(LanTrainDecor), nameof(SwitchDone))); }
            catch (Exception ex) { log("Switch loop guard unavailable: " + ex.GetType().Name + ": " + ex.Message); }
            try
            {
                OriginalOf = AccessTools.FieldRefAccess<Paintable, Material>("OriginalMaterial");
                HoveredOf = AccessTools.FieldRefAccess<PaintManager, Paintable>("HoveredPaintable");
                SelectedOf = AccessTools.FieldRefAccess<PaintManager, Renderer>("selectedRenderer");
                MainCanvas = AccessTools.Field(typeof(Zompiercer.GUI.GuiCore), "mainCanvas");
                _harmony.Patch(AccessTools.Method(typeof(PaintManager), "Update"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(AimPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(PaintManager), "ApplyColorHandler"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(AimPrefix)), postfix: new HarmonyMethod(typeof(LanTrainDecor), nameof(PaintedPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(PaintManager), "RestoreColorHandler"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(AimPrefix)), postfix: new HarmonyMethod(typeof(LanTrainDecor), nameof(RestoredPostfix)));
                _harmony.Patch(AccessTools.Method(typeof(PaintManager), "CopyColorHandler"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(AimPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(WireEditor), "SetEditMode"), postfix: new HarmonyMethod(typeof(LanTrainDecor), nameof(WireModePostfix)));
                _harmony.Patch(AccessTools.Method(typeof(WireEditor), "Update"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(WirePrunePrefix)));
                _harmony.Patch(AccessTools.Method(typeof(LogicInput), "ConnectTo"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(ConnectPrefix)), finalizer: new HarmonyMethod(typeof(LanTrainDecor), nameof(LoudAgain)));
                _harmony.Patch(AccessTools.Method(typeof(LogicInput), "ClearConnection"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(CutPrefix)));
                _harmony.Patch(AccessTools.Method(typeof(LogicOutput), "ClearConnection"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(CutAllPrefix)), finalizer: new HarmonyMethod(typeof(LanTrainDecor), nameof(LoudAgain)));
                _harmony.Patch(AccessTools.Method(typeof(TextSign), "Edit"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(EditPrefix)));
            }
            catch (Exception ex)
            {
                Failure = ex.GetType().Name + ": " + ex.Message;
                OriginalOf = null; HoveredOf = null; SelectedOf = null; MainCanvas = null;
                _harmony.UnpatchSelf();
                try { _harmony.Patch(AccessTools.Method(typeof(LogicOutput), "SetState"), prefix: new HarmonyMethod(typeof(LanTrainDecor), nameof(SwitchPrefix)), finalizer: new HarmonyMethod(typeof(LanTrainDecor), nameof(SwitchDone))); }
                catch (Exception) { }
                log("Train decor hooks unavailable: " + Failure);
            }
        }
        internal static void Shutdown() { _harmony?.UnpatchSelf(); _harmony = null; GuestRequest = null; _applying = _quiet = false; }

        private static bool Active { get { return LanSaveIsolation.Active && LanTrainBuild.GuestAttached; } }

        // ---- Host: what the layout carries ----

        // The object's paint (surfaces whose material or colour is not its own), sign and wires; null when none.
        internal static TrainDecor Capture(Component root)
        {
            try { return CaptureDecor(root); }
            catch (Exception ex)
            {
                if (!_captureLogged) { _captureLogged = true; _log?.Invoke("Train decor not captured: " + ex.GetType().Name + ": " + ex.Message); }
                return null;
            }
        }
        private static TrainDecor CaptureDecor(Component root)
        {
            var d = new TrainDecor();
            var db = DatabaseMaterials.Global;
            var paintables = OriginalOf == null ? new Paintable[0] : TrainLayout.OwnedBy<Paintable>(root);
            for (int i = 0; i < paintables.Length && i <= LanStorage.MaxDecorIndex && d.Paints.Count < TrainDecor.MaxPaint; i++)
            {
                var p = paintables[i];
                var renderer = p == null ? null : p.GetComponent<MeshRenderer>();
                var shared = renderer == null ? null : renderer.sharedMaterial;
                if (shared == null || db == null || shared == OriginalOf(p)) continue;
                string name = LanStorage.CleanMaterial(shared.name);
                // The game's save turns every surface's material into an instance of itself: unchanged.
                if (OriginalOf(p) != null && name == LanStorage.CleanMaterial(OriginalOf(p).name) && Same(shared.color, OriginalOf(p).color)) continue;
                if (name.Length == 0 || db.GetMaterialByName(name) == null) continue;
                Color32 c = shared.color;
                d.Paints.Add(new TrainDecor.Paint { Index = i, R = c.r, G = c.g, B = c.b, A = c.a, Material = name });
            }
            var signs = TrainLayout.OwnedBy<TextSign>(root);
            if (signs.Length > 0 && signs[0].text != null) d.Sign = LanStorage.CleanSign(signs[0].text.text);
            var inputs = TrainLayout.OwnedBy<LogicInput>(root);
            for (int i = 0; i < inputs.Length && i <= LanStorage.MaxDecorIndex && d.Wires.Count < TrainDecor.MaxWires; i++)
            {
                var source = inputs[i] == null ? null : inputs[i].ConnectedTo;
                var part = source == null ? null : source.GetComponentInParent<TrainPart>();
                if (part != null) d.Wires.Add(new TrainDecor.Wire { Input = i, Output = part.GetInstanceID() });
            }
            return d.Empty ? null : d;
        }
        // 1.4.1: a material instance a surface no longer uses (made for it by an earlier paint) is
        // destroyed; the game's own materials and one a paint preview still holds are left alone.
        private static void Discard(Material old, Renderer renderer, Paintable p)
        {
            if (old == null || old == renderer.sharedMaterial || OriginalOf != null && old == OriginalOf(p) ||
                !old.name.EndsWith(" (Instance)", StringComparison.Ordinal) || renderer.GetComponent<PaintPreviewer>() != null) return;
            Object.Destroy(old);
        }
        private static bool Same(Color a, Color b) { return Mathf.Abs(a.r - b.r) < .002f && Mathf.Abs(a.g - b.g) < .002f && Mathf.Abs(a.b - b.b) < .002f && Mathf.Abs(a.a - b.a) < .002f; }

        // ---- Guest: showing the host's decor on a shown object ----

        // `paintables`, `inputs` and `sign` belong to one shown object; `outputs` are the switches of the
        // shown parts by host id. A surface without host paint shows its own material again.
        internal static void Apply(TrainDecor d, Paintable[] paintables, LogicInput[] inputs, UnityEngine.UI.Text sign, Dictionary<int, LogicOutput> outputs)
        {
            _applying = true;
            try
            {
                var db = DatabaseMaterials.Global;
                if (paintables != null && OriginalOf != null)
                    for (int i = 0; i < paintables.Length; i++)
                    {
                        var p = paintables[i];
                        var renderer = p == null ? null : p.GetComponent<MeshRenderer>();
                        if (renderer == null || renderer.GetComponent<PaintPreviewer>() != null) continue; // the guest is choosing a paint here
                        var paint = d == null ? null : d.PaintOf(i);
                        var shared = renderer.sharedMaterial;
                        if (paint == null)
                        {
                            if (OriginalOf(p) != null && shared != OriginalOf(p)) { renderer.sharedMaterial = OriginalOf(p); Discard(shared, renderer, p); }
                            continue;
                        }
                        var color = (Color)new Color32(paint.R, paint.G, paint.B, paint.A);
                        if (shared != null && LanStorage.CleanMaterial(shared.name) == paint.Material && Same(shared.color, color)) continue;
                        var material = db == null ? null : db.GetMaterialByName(paint.Material);
                        if (material == null) continue;
                        renderer.material = material;
                        renderer.material.color = color;
                        Discard(shared, renderer, p);
                    }
                if (sign != null && d != null && d.Sign != null && sign.text != d.Sign) sign.text = d.Sign;
                if (inputs != null)
                    for (int i = 0; i < inputs.Length; i++)
                    {
                        var input = inputs[i];
                        if (input == null) continue;
                        var wire = d == null ? null : d.WireOf(i);
                        LogicOutput source = null;
                        if (wire != null && outputs != null) outputs.TryGetValue(wire.Output, out source);
                        if (input.ConnectedTo == source) continue;
                        if (source == null) input.ClearConnection(); else input.ConnectTo(source);
                    }
            }
            catch (Exception ex) { _log?.Invoke("Train decor not shown: " + ex.GetType().Name + ": " + ex.Message); }
            finally { _applying = false; }
        }

        // ---- Guest: requests ----

        private static void Send(LanStorage.DecorRequest request)
        {
            var send = GuestRequest;
            if (send == null) { LanStorageGame.Notice("Менять поезд хоста можно, когда хост сохраняет ваш профиль"); return; }
            if (!send(request, Answered)) LanStorageGame.Notice("Хост ещё отвечает на прошлые запросы");
        }
        private static void Answered(byte status)
        {
            if (status == LanStorage.Ok) return; // the next train layout shows it
            if (status != LanStorage.Busy) LanStorageGame.Notice("Хост не принял изменение: " + LanStorage.Describe(status));
            TrainLayout.ReapplyDecor(); // the guest's copy shows the host's train again
        }
        private static LanStorage.DecorRequest Target(int kind, Component node, int index)
        {
            int car, owner, identity; Component shown;
            if (!TrainLayout.TryIdentifyDecor(node.transform, out car, out owner, out identity, out shown)) return null;
            return new LanStorage.DecorRequest { Kind = kind, Car = car, Owner = owner == TrainLayout.FurnitureKindId ? LanStorage.TrainFurniture : LanStorage.TrainPart, Identity = identity, Index = index };
        }

        // Paint mode: the game picks the surface under the crosshair with its interaction ray, which
        // misses the copies of the host's train; this ray sees them.
        private static void AimPrefix(PaintManager __instance)
        {
            if (!Active || __instance == null || !__instance.PaintMode) return;
            try
            {
                var selection = SelectionManager.Global;
                var manager = GlobalManager.global;
                var camera = manager == null ? null : manager.MainCamera;
                var player = manager == null ? null : manager.controlledChar;
                if (selection == null || camera == null) return;
                var input = Zompiercer.Inputs.InputController.Global;
                var ray = input != null && input.isKbMouseDevice ? camera.ScreenPointToRay(input.MousePosition) : camera.ScreenPointToRay(new Vector3(Screen.width / 2f, Screen.height / 2f, 0f));
                int mask = selection.layerMask.value | 1 << TrainLayout.SupportLayer;
                RaycastHit best = default(RaycastHit); bool any = false;
                foreach (var hit in Physics.RaycastAll(ray, PaintRay, mask, QueryTriggerInteraction.Ignore))
                {
                    if (hit.collider == null || player != null && hit.collider.transform.IsChildOf(player.transform)) continue;
                    if (!any || hit.distance < best.distance) { best = hit; any = true; }
                }
                if (any) selection.raycastHitInfRange = best;
            }
            catch (Exception ex) { _log?.Invoke("Paint aim failed: " + ex.Message); }
        }
        private static int PaintIndex(Paintable p)
        {
            int car, owner, identity; Component shown;
            if (p == null || !TrainLayout.TryIdentifyDecor(p.transform, out car, out owner, out identity, out shown)) return -1;
            return Array.IndexOf(TrainLayout.DecorPaintables(shown), p);
        }
        private static void PaintedPostfix(PaintManager __instance)
        {
            if (!Active || __instance == null || !__instance.PaintMode || HoveredOf == null) return;
            try
            {
                var p = HoveredOf(__instance);
                var material = __instance.SelectedMaterial;
                if (p == null || SelectedOf(__instance) == null || material == null || GuiRaycaster.global != null && GuiRaycaster.global.CursorOn != null) return;
                int index = PaintIndex(p);
                var request = index < 0 || index > LanStorage.MaxDecorIndex ? null : Target(LanStorage.DecorPaint, p, index);
                if (request == null) { LanStorageGame.Notice("Красить можно только поезд хоста"); return; }
                var c = __instance.SelectedColor;
                request.Text = LanStorage.CleanMaterial(material.name);
                request.Color = new[] { Mathf.Clamp01(c.r), Mathf.Clamp01(c.g), Mathf.Clamp01(c.b), 1f };
                request.ApplyColor = __instance.ApplyColor;
                if (request.Text.Length == 0) return;
                Send(request);
            }
            catch (Exception ex) { _log?.Invoke("Paint hook failed: " + ex.Message); }
        }
        private static void RestoredPostfix(PaintManager __instance)
        {
            if (!Active || __instance == null || !__instance.PaintMode || HoveredOf == null) return;
            try
            {
                var p = HoveredOf(__instance);
                if (p == null || SelectedOf(__instance) == null || GuiRaycaster.global != null && GuiRaycaster.global.CursorOn != null) return;
                int index = PaintIndex(p);
                var request = index < 0 || index > LanStorage.MaxDecorIndex ? null : Target(LanStorage.DecorRestore, p, index);
                if (request != null) Send(request);
            }
            catch (Exception ex) { _log?.Invoke("Paint restore hook failed: " + ex.Message); }
        }

        // Wire mode: only the nodes of shown host objects (the guest's own hidden ones stay out).
        private static void WireModePostfix(WireEditor __instance, bool value)
        {
            if (!Active || !value || __instance == null) return;
            try
            {
                var gone = new HashSet<LogicNodeIndicator>();
                var outputs = new List<LogicOutput>();
                foreach (var o in __instance.AllLogicOutput ?? new LogicOutput[0])
                {
                    if (o != null && Shown(o)) { outputs.Add(o); continue; }
                    if (o != null && o.Indicator != null) { gone.Add(o.Indicator); Object.Destroy(o.Indicator.gameObject); o.Indicator = null; }
                }
                var inputs = new List<LogicInput>();
                foreach (var i in __instance.AllLogicInput ?? new LogicInput[0])
                {
                    if (i != null && Shown(i)) { inputs.Add(i); continue; }
                    if (i != null && i.Indicator != null) { gone.Add(i.Indicator); Object.Destroy(i.Indicator.gameObject); i.Indicator = null; }
                }
                __instance.AllLogicOutput = outputs.ToArray();
                __instance.AllLogicInput = inputs.ToArray();
                if (__instance.SpawnedIndicators != null) __instance.SpawnedIndicators.RemoveAll(x => gone.Contains(x));
            }
            catch (Exception ex) { _log?.Invoke("Wire mode filter failed: " + ex.Message); }
        }
        // 1.4.1: a copy the host's layout replaced while the wire mode is open: its nodes are gone, and
        // the game's wire mode reads every node's place each frame.
        private static void WirePrunePrefix(WireEditor __instance)
        {
            if (!Active || __instance == null || !__instance.EditMode) return;
            var outputs = __instance.AllLogicOutput;
            if (outputs != null && Array.Exists(outputs, o => o == null)) __instance.AllLogicOutput = Array.FindAll(outputs, o => o != null);
            var inputs = __instance.AllLogicInput;
            if (inputs != null && Array.Exists(inputs, i => i == null)) __instance.AllLogicInput = Array.FindAll(inputs, i => i != null);
            if (__instance.SpawnedIndicators != null) __instance.SpawnedIndicators.RemoveAll(x => x == null);
        }

        // 1.4.1: a switch feeding a lamp that feeds that switch again (a wire loop, which the guest can now
        // make on the host's train too) would recurse until the game crashes; the chain stops here.
        private static bool SwitchPrefix(out bool __state)
        {
            __state = false;
            if (_switchDepth >= MaxSwitchDepth)
            {
                if (!_loopLogged) { _loopLogged = true; _log?.Invoke("A wire loop was cut at " + MaxSwitchDepth + " switches"); }
                return false;
            }
            _switchDepth++; __state = true;
            return true;
        }
        private static Exception SwitchDone(bool __state, Exception __exception) { if (__state) _switchDepth--; return __exception; }

        private static bool Shown(Component node)
        {
            int car, owner, identity; Component shown;
            return TrainLayout.TryIdentifyDecor(node.transform, out car, out owner, out identity, out shown);
        }
        private static bool Wiring { get { return Active && !_applying && WireEditor.Global != null && WireEditor.Global.EditMode; } }

        // A wire dragged from a switch to an input: the host connects its own.
        private static bool ConnectPrefix(LogicInput __instance, LogicOutput target)
        {
            if (!Wiring || _quiet || __instance == null || target == null) return true;
            _quiet = true; // the game cuts the old wire first: part of this request
            try
            {
                int car, owner, identity, outCar, outOwner, outIdentity; Component shown, outShown;
                if (!TrainLayout.TryIdentifyDecor(__instance.transform, out car, out owner, out identity, out shown) ||
                    !TrainLayout.TryIdentifyDecor(target.transform, out outCar, out outOwner, out outIdentity, out outShown) || outOwner == TrainLayout.FurnitureKindId)
                { LanStorageGame.Notice("Провода можно тянуть только на поезде хоста"); return false; }
                int index = Array.IndexOf(TrainLayout.DecorInputs(shown), __instance);
                var request = index < 0 || index > LanStorage.MaxDecorIndex ? null : Target(LanStorage.DecorWire, __instance, index);
                if (request == null) return false;
                request.OutCar = outCar; request.OutIdentity = outIdentity;
                Send(request);
            }
            catch (Exception ex) { _log?.Invoke("Wire hook failed: " + ex.Message); }
            return true;
        }
        private static Exception LoudAgain(Exception __exception) { _quiet = false; return __exception; }
        private static bool CutPrefix(LogicInput __instance)
        {
            if (!Wiring || _quiet || __instance == null || __instance.ConnectedTo == null) return true;
            try
            {
                int car, owner, identity; Component shown;
                if (!TrainLayout.TryIdentifyDecor(__instance.transform, out car, out owner, out identity, out shown)) return true;
                int index = Array.IndexOf(TrainLayout.DecorInputs(shown), __instance);
                var request = index < 0 || index > LanStorage.MaxDecorIndex ? null : Target(LanStorage.DecorUnwire, __instance, index);
                if (request != null) Send(request);
            }
            catch (Exception ex) { _log?.Invoke("Wire cut hook failed: " + ex.Message); }
            return true;
        }
        private static bool CutAllPrefix(LogicOutput __instance)
        {
            if (!Wiring || _quiet || __instance == null) return true;
            _quiet = true; // each input it feeds is cut as part of this request
            try
            {
                var request = Target(LanStorage.DecorUnwireAll, __instance, 0);
                if (request != null && request.Owner == LanStorage.TrainPart) Send(request);
            }
            catch (Exception ex) { _log?.Invoke("Wire cut hook failed: " + ex.Message); }
            return true;
        }

        // Signs: the guest's look ray finds the copy of a host sign; the game's own text window edits it.
        private static bool EditPrefix() { return !Active; }
        internal static bool LookAtSign(Zompiercer.Inputs.InputController input, Component node, string text)
        {
            var info = InventoryController.global == null ? null : InventoryController.global.simpleInfoDisplayer;
            var gui = Zompiercer.Singleton<Zompiercer.GUI.GuiCore>.Global;
            if (info == null || gui == null) return false;
            info.color = GlobalSoundEffects.global.colorGreen;
            info.SetIconText("Edit", (Zompiercer.Inputs.Actions)30, (Zompiercer.Inputs.Actions)0, true);
            bool edit = input.isKbMouseDevice ? input.isLeverUpDown : input.gp_isAltUseDown;
            if (!edit || gui.OpenedWindowWithInputField != null) return true;
            input.isLeverUpDown = false; input.gp_isAltUseDown = false;
            var request = Target(LanStorage.DecorSign, node, 0);
            if (request == null) return true;
            try
            {
                var window = Object.Instantiate(gui.WindowWithInputFieldPrefab);
                var canvas = MainCanvas == null ? null : MainCanvas.GetValue(gui) as Component;
                if (canvas == null) { Object.Destroy(window.gameObject); return true; }
                window.transform.SetParent(canvas.transform, false);
                window.inputField.text = text ?? "";
                window.inputField.Select();
                window.inputField.ActivateInputField();
                window.Event.AddListener(value => { request.Text = LanStorage.CleanSign(value); Send(request); });
                CursorChanger.CursorSetLock(false);
                GlobalManager.global.controlledChar.controlEnabled = false;
                gui.OpenedWindowWithInputField = window;
            }
            catch (Exception ex) { _log?.Invoke("Sign window failed: " + ex.Message); }
            return true;
        }

        // ---- Host ----

        internal static byte Host(LanStorage.DecorRequest r, Vector3? guest)
        {
            var train = GlobalManager.global == null ? null : GlobalManager.global.controlledTrain;
            var scenes = GlobalSceneManager.global;
            if (train == null || scenes == null || scenes.SceneCurrentlyLoading) return LanStorage.Unavailable;
            if (guest == null) return LanStorage.Lost;
            byte status;
            var target = Find(train, r.Car, r.Owner, r.Identity, out status);
            if (target == null) return status;
            float reach = r.Kind == LanStorage.DecorSign ? SignReach : r.Kind == LanStorage.DecorPaint || r.Kind == LanStorage.DecorRestore ? PaintReach : WireReach;
            if (Distance(target, guest.Value) > reach) return LanStorage.TooFar;
            switch (r.Kind)
            {
                case LanStorage.DecorPaint:
                case LanStorage.DecorRestore:
                {
                    var paintables = TrainLayout.OwnedBy<Paintable>(target);
                    if (r.Index >= paintables.Length || paintables[r.Index] == null) return LanStorage.NotFound;
                    var p = paintables[r.Index];
                    var renderer = p.GetComponent<MeshRenderer>();
                    if (renderer == null) return LanStorage.NotFound;
                    var old = renderer.sharedMaterial;
                    if (r.Kind == LanStorage.DecorRestore) { p.Restore(); Discard(old, renderer, p); return LanStorage.Ok; }
                    var material = DatabaseMaterials.Global == null ? null : DatabaseMaterials.Global.GetMaterialByName(r.Text);
                    if (material == null) return LanStorage.NotFound;
                    // As the game's paint mode leaves it (PaintPreviewer) and its save loads it (Paintable.Load);
                    // 1.4.1: always opaque.
                    renderer.material = material;
                    if (r.ApplyColor) renderer.material.color = new Color(r.Color[0], r.Color[1], r.Color[2], 1f);
                    Discard(old, renderer, p);
                    return LanStorage.Ok;
                }
                case LanStorage.DecorSign:
                {
                    var signs = TrainLayout.OwnedBy<TextSign>(target);
                    if (signs.Length == 0) return LanStorage.NotFound;
                    signs[0].SetText(LanStorage.CleanSign(r.Text));
                    return LanStorage.Ok;
                }
                case LanStorage.DecorWire:
                case LanStorage.DecorUnwire:
                {
                    var inputs = TrainLayout.OwnedBy<LogicInput>(target);
                    if (r.Index >= inputs.Length || inputs[r.Index] == null) return LanStorage.NotFound;
                    if (r.Kind == LanStorage.DecorUnwire) { inputs[r.Index].ClearConnection(); return LanStorage.Ok; }
                    var part = Find(train, r.OutCar, LanStorage.TrainPart, r.OutIdentity, out status);
                    if (part == null) return status;
                    if (Distance(part, guest.Value) > WireReach) return LanStorage.TooFar;
                    var outputs = TrainLayout.OwnedBy<LogicOutput>(part);
                    if (outputs.Length == 0 || outputs[0] == null) return LanStorage.NotFound;
                    inputs[r.Index].ConnectTo(outputs[0]);
                    return LanStorage.Ok;
                }
                case LanStorage.DecorUnwireAll:
                {
                    var outputs = TrainLayout.OwnedBy<LogicOutput>(target);
                    if (outputs.Length == 0 || outputs[0] == null) return LanStorage.NotFound;
                    outputs[0].ClearConnection();
                    return LanStorage.Ok;
                }
            }
            return LanStorage.Invalid;
        }

        // A built furniture or part of the host's train by its host id.
        private static Component Find(FluffyUnderware.Curvy.Examples.TrainManager train, int carIndex, int owner, int identity, out byte status)
        {
            status = LanStorage.NotFound;
            TrainCar car;
            try { car = TrainLayout.GetCar(train, carIndex); }
            catch (Exception) { return null; }
            if (owner == LanStorage.TrainFurniture)
            {
                if (car.AllFurniture != null)
                    foreach (var furniture in car.AllFurniture)
                        if (furniture != null && furniture.GetInstanceID() == identity)
                        {
                            if (furniture.notAssembled) { status = LanStorage.NotAllowed; return null; }
                            return furniture;
                        }
                return null;
            }
            foreach (var part in car.GetComponentsInChildren<TrainPart>(true))
                if (part != null && part.GetInstanceID() == identity)
                {
                    if (part.Blueprint) { status = LanStorage.NotAllowed; return null; }
                    return part;
                }
            return null;
        }
        private static float Distance(Component target, Vector3 guest)
        {
            float best = Vector3.Distance(target.transform.position, guest);
            foreach (var collider in target.GetComponentsInChildren<Collider>(false))
                if (collider != null && collider.enabled && !collider.isTrigger) best = Mathf.Min(best, Vector3.Distance(collider.bounds.ClosestPoint(guest), guest));
            return best;
        }
    }
}
