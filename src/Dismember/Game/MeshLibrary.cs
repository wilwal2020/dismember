using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using Dismember.Assets;
using Dismember.Core;
using UnityEngine;

namespace Dismember.Game
{
    /// <summary>
    /// Supplies CPU-side mesh data for NPC renderers.
    ///
    /// BONELAB's NPC meshes are not CPU-readable at runtime, so this reads them out of the game's (and mods')
    /// asset bundles instead. A name -> bundle index is built once in the background and cached on disk;
    /// individual meshes are decoded on worker threads the first time an NPC type is seen.
    /// </summary>
    internal static class MeshLibrary
    {
        private static readonly ConcurrentDictionary<string, MeshData> Cache = new ConcurrentDictionary<string, MeshData>();
        private static readonly ConcurrentDictionary<string, byte> Pending = new ConcurrentDictionary<string, byte>();
        private static readonly ConcurrentDictionary<string, byte> Missing = new ConcurrentDictionary<string, byte>();
        private static Dictionary<string, List<(string bundle, long pathId)>> _index;
        private static volatile bool _indexReady;      // usable (game content indexed)
        private static volatile bool _indexComplete;   // mods indexed too: a name missing now is really missing
        private static volatile string _status = "not started";
        private static string _cacheFile;
        private static readonly object IndexLock = new object();

        public static bool IndexReady => _indexReady;
        public static string Status => _status;

        public static void Init(string userDataDir)
        {
            _cacheFile = Path.Combine(userDataDir, "Dismember", "mesh_index.txt");
            StartIndexBuild(false);
        }

        public static void StartIndexBuild(bool force)
        {
            _indexReady = false;
            _indexComplete = false;
            Missing.Clear();
            var roots = BundleRoots();
            var thread = new Thread(() => BuildIndex(roots, force)) { IsBackground = true, Priority = System.Threading.ThreadPriority.BelowNormal, Name = "Dismember mesh index" };
            thread.Start();
        }

        private static List<string> BundleRoots()
        {
            var roots = new List<string>();
            try
            {
                string aa = Path.Combine(Application.streamingAssetsPath, "aa", "StandaloneWindows64");
                if (Directory.Exists(aa))
                {
                    var dirs = Directory.GetDirectories(aa).Select(Path.GetFileName).ToList();
                    // NPC meshes live in spawnables, avatars and the deduplicated mesh/prefab bundles (the Null body's are
                    // in deduped_assets_gameobject).
                    bool Want(string d) => (d.Contains("spawnable") || d.Contains("avatar") || d == "deduped_assets_mesh" ||
                                            d == "deduped_assets_gameobject" || d == "deduped_assets_object") && !d.Contains("previewmesh");
                    // Spawnables first: that is where most NPC meshes live.
                    foreach (var d in dirs.Where(d => Want(d) && d.Contains("spawnable"))) roots.Add(Path.Combine(aa, d));
                    foreach (var d in dirs.Where(d => Want(d) && !d.Contains("spawnable"))) roots.Add(Path.Combine(aa, d));
                }
                string mods = Path.Combine(Application.persistentDataPath, "Mods");
                if (Directory.Exists(mods)) roots.Add(mods);
            }
            catch (Exception e)
            {
                Log.Warn("Could not list bundle folders: " + e.Message);
            }
            return roots;
        }

        private static void BuildIndex(List<string> roots, bool force)
        {
            try
            {
                var cached = force ? new Dictionary<string, (long len, long time, List<(long, string)> meshes)>() : LoadCache();
                var index = new Dictionary<string, List<(string, long)>>(StringComparer.Ordinal);
                var fresh = new Dictionary<string, (long len, long time, List<(long, string)> meshes)>();
                var files = new List<string>();
                int gameFiles = 0;
                foreach (var root in roots)
                {
                    try { files.AddRange(Directory.EnumerateFiles(root, "*.bundle", SearchOption.AllDirectories)); }
                    catch (Exception e) { Log.Warn($"Could not scan {root}: {e.Message}"); }
                    if (root.Contains("StreamingAssets")) gameFiles = files.Count;
                }

                int done = 0, scanned = 0;
                foreach (var file in files)
                {
                    if (done == gameFiles && gameFiles > 0 && !_indexReady)
                    {
                        // Vanilla NPCs can be cut now; mod content keeps indexing in the background.
                        Publish(index);
                        _indexReady = true;
                        Log.Msg($"Mesh index ready for game content ({index.Count} meshes); indexing mod content...");
                    }
                    done++;
                    FileInfo fi;
                    try { fi = new FileInfo(file); } catch { continue; }
                    if (fi.Length > 300L * 1024 * 1024) continue; // levels and other huge bundles never hold NPC meshes we need
                    List<(long, string)> meshes;
                    if (cached.TryGetValue(file, out var c) && c.len == fi.Length && c.time == fi.LastWriteTimeUtc.Ticks)
                    {
                        meshes = c.meshes;
                    }
                    else
                    {
                        scanned++;
                        _status = $"indexing bundles {done}/{files.Count}";
                        try
                        {
                            meshes = BundleMeshReader.ListMeshes(file).Select(m => (m.pathId, m.name)).ToList();
                        }
                        catch (Exception e)
                        {
                            Log.Debug($"skip {Path.GetFileName(file)}: {e.Message}");
                            meshes = new List<(long, string)>();
                        }
                    }
                    fresh[file] = (fi.Length, fi.LastWriteTimeUtc.Ticks, meshes);
                    foreach (var (pathId, name) in meshes)
                    {
                        if (!index.TryGetValue(name, out var list)) index[name] = list = new List<(string, long)>();
                        list.Add((file, pathId));
                    }
                }

                Publish(index);
                _indexReady = true;
                _indexComplete = true;
                Missing.Clear();   // names looked up before mod content was indexed get another chance
                _status = $"ready ({index.Count} meshes in {files.Count} bundles)";
                Log.Msg($"Mesh index ready: {index.Count} mesh names, {files.Count} bundles ({scanned} scanned this time)");
                if (scanned > 0) SaveCache(fresh);
            }
            catch (Exception e)
            {
                _status = "failed: " + e.Message;
                Log.Error("Mesh index failed: " + e);
            }
        }

        private static void Publish(Dictionary<string, List<(string, long)>> index)
        {
            var copy = new Dictionary<string, List<(string, long)>>(index.Count, StringComparer.Ordinal);
            foreach (var kv in index) copy[kv.Key] = new List<(string, long)>(kv.Value);
            lock (IndexLock) _index = copy;
        }

        private static Dictionary<string, (long len, long time, List<(long, string)> meshes)> LoadCache()
        {
            var result = new Dictionary<string, (long, long, List<(long, string)>)>();
            try
            {
                if (_cacheFile == null || !File.Exists(_cacheFile)) return result;
                string current = null; long len = 0, time = 0; List<(long, string)> meshes = null;
                foreach (var line in File.ReadLines(_cacheFile))
                {
                    if (line.StartsWith("#B\t"))
                    {
                        if (current != null) result[current] = (len, time, meshes);
                        var p = line.Split('\t');
                        current = p[1]; len = long.Parse(p[2]); time = long.Parse(p[3]);
                        meshes = new List<(long, string)>();
                    }
                    else if (current != null && line.Length > 0)
                    {
                        int tab = line.IndexOf('\t');
                        if (tab > 0) meshes.Add((long.Parse(line.Substring(0, tab)), line.Substring(tab + 1)));
                    }
                }
                if (current != null) result[current] = (len, time, meshes);
            }
            catch (Exception e)
            {
                Log.Warn("Mesh index cache unreadable, rebuilding: " + e.Message);
                result.Clear();
            }
            return result;
        }

        private static void SaveCache(Dictionary<string, (long len, long time, List<(long, string)> meshes)> data)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_cacheFile));
                var sb = new StringBuilder();
                foreach (var kv in data)
                {
                    sb.Append("#B\t").Append(kv.Key).Append('\t').Append(kv.Value.len).Append('\t').Append(kv.Value.time).Append('\n');
                    foreach (var (pathId, name) in kv.Value.meshes)
                        sb.Append(pathId).Append('\t').Append(name.Replace('\n', ' ').Replace('\t', ' ')).Append('\n');
                }
                File.WriteAllText(_cacheFile, sb.ToString());
            }
            catch (Exception e)
            {
                Log.Warn("Could not save mesh index cache: " + e.Message);
            }
        }

        private static string CleanName(string name)
        {
            if (name == null) return "";
            if (name.EndsWith(" Instance")) name = name.Substring(0, name.Length - 9);
            if (name.EndsWith("(Clone)")) name = name.Substring(0, name.Length - 7);
            return name.Trim();
        }

        /// <summary>
        /// Returns mesh data for a runtime mesh, or null. When null, <paramref name="pending"/> tells whether it is
        /// still being loaded (try again later) or cannot be found at all.
        /// </summary>
        public static MeshData TryGet(Mesh mesh, out bool pending)
        {
            pending = false;
            if (mesh.IsDead()) return null;
            string name = CleanName(mesh.name);
            int vc = mesh.vertexCount;
            string key = name + "|" + vc;
            if (Cache.TryGetValue(key, out var md)) return md;
            if (Missing.ContainsKey(key)) return null;

            if (mesh.isReadable)
            {
                try
                {
                    md = MeshFactory.FromUnity(mesh);
                    Cache[key] = md;
                    return md;
                }
                catch (Exception e)
                {
                    Log.Warn($"Reading mesh {name} failed: {e.Message}");
                }
            }

            if (!_indexReady) { pending = true; return null; }
            if (Pending.ContainsKey(key)) { pending = true; return null; }

            List<(string bundle, long pathId)> candidates;
            lock (IndexLock)
            {
                if (_index == null || !_index.TryGetValue(name, out var list))
                {
                    if (!_indexComplete) { pending = true; return null; }   // may be in a mod bundle not indexed yet
                    Missing[key] = 1;
                    Log.Warn($"Mesh '{name}' ({vc} verts) not found in any bundle; this renderer will not be cut.");
                    return null;
                }
                candidates = new List<(string, long)>(list);
            }

            Pending[key] = 1;
            pending = true;
            ThreadPool.QueueUserWorkItem(_ =>
            {
                try
                {
                    foreach (var (bundle, pathId) in candidates)
                    {
                        var data = BundleMeshReader.ReadMesh(bundle, pathId);
                        if (data == null || data.VertexCount != vc) continue;
                        _ = data.Topology; // build adjacency off the main thread
                        _ = data.Topology.EdgeTriangles;
                        Cache[key] = data;
                        Log.Debug($"Loaded mesh {name} ({vc} verts) from {Path.GetFileName(bundle)}");
                        return;
                    }
                    Missing[key] = 1;
                    Log.Warn($"Mesh '{name}' with {vc} verts not found in {candidates.Count} candidate bundle(s).");
                }
                catch (Exception e)
                {
                    Missing[key] = 1;
                    Log.Warn($"Decoding mesh {name} failed: {e.Message}");
                }
                finally
                {
                    Pending.TryRemove(key, out byte _);
                }
            });
            return null;
        }
    }
}
