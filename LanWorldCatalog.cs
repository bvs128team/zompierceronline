using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using FluffyUnderware.Curvy;
using UnityEngine;
using Zompiercer.SaveLoad;

namespace ZompiercerLAN
{
    internal static partial class LanWorldCatalog
    {
        private sealed class Entry
        {
            internal readonly string Scene;
            internal readonly int Location;
            internal readonly int[] Rails;
            internal Entry(string scene, int location, int[] rails) { Scene = scene; Location = location; Rails = rails; }
        }
        private static volatile bool _ready;
        private static string _failure;
        private static int _started;
        internal static bool Ready { get { return _ready; } }
        internal static string Failure { get { return Volatile.Read(ref _failure); } }
        internal static void Initialize(string dataPath)
        {
            if (Interlocked.Exchange(ref _started, 1) != 0) return;
            // Only file I/O on the worker. No Unity objects or game assembly execution.
            var worker = new Thread(() =>
            {
                try
                {
                    foreach (var item in Fingerprints)
                    {
                        string path = Path.Combine(dataPath, item[0]);
                        LanInventoryGuard.RejectLinks(path);
                        using (var stream = File.OpenRead(path))
                        using (var sha = SHA256.Create())
                            if (BitConverter.ToString(sha.ComputeHash(stream)).Replace("-", "") != item[1])
                                throw new IOException("Unrecognized game asset: " + item[0]);
                    }
                    _ready = true;
                }
                catch (Exception ex) { Volatile.Write(ref _failure, ex.GetType().Name + ": " + ex.Message); }
            }) { IsBackground = true, Name = "Zompiercer LAN asset verification" };
            worker.Start();
        }
        private static Entry Find(string scene)
        {
            foreach (var entry in Entries) if (entry.Scene == scene) return entry;
            return null;
        }
        internal static bool Valid(Packet world)
        {
            if (!Ready || world == null || world.WorldEpoch == 0 || !Finite(world.TrainPosition) ||
                world.TrainPosition < SaveLoadCore.MinLoadedTrainPos) return false;
            var entry = Find(world.Scene);
            if (entry == null || entry.Location != world.Location ||
                (world.RailsId != -1 && Array.IndexOf(entry.Rails, world.RailsId) < 0)) return false;
            var scenes = GlobalSceneManager.global;
            if (scenes == null || scenes.Scenes == null || !Application.CanStreamedLevelBeLoaded(world.Scene)) return false;
            foreach (var scene in scenes.Scenes)
                if (scene != null && scene.sceneName == world.Scene) return true;
            return false;
        }
        internal static CurvySpline ResolveLoadedRails(Packet world, float position)
        {
            if (!Valid(world) || !Finite(position) || position < SaveLoadCore.MinLoadedTrainPos ||
                GlobalSceneManager.global.CurrentSceneName != world.Scene || SaveLoadCore.global == null ||
                SaveLoadCore.global.currentLocation != world.Location)
                throw new InvalidDataException("Client world identity mismatch");
            CurvySpline spline;
            if (world.RailsId == -1) spline = GlobalManager.global == null ? null : GlobalManager.global.currentRailRoad;
            else
            {
                var id = SaveLoadCore.FindObjectByID(world.RailsId);
                spline = id == null ? null : id.GetComponent<CurvySpline>();
            }
            var identity = spline == null ? null : spline.GetComponent<ObjectID>();
            var entry = Find(world.Scene);
            if (spline == null || identity == null || spline.gameObject.scene.name != world.Scene ||
                Array.IndexOf(entry.Rails, identity.ID) < 0 || (world.RailsId != -1 && identity.ID != world.RailsId) ||
                !spline.IsInitialized || spline.Dirty || !Finite(spline.Length) || spline.Length <= 0 || position > spline.Length)
                throw new InvalidDataException("Invalid client rail identity or train distance");
            return spline;
        }
        private static bool Finite(float value) { return !float.IsNaN(value) && !float.IsInfinity(value); }
    }
}
