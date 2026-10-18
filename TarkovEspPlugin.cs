using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;
using Comfort.Common;
using EFT;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TarkovEsp;

[BepInPlugin("codex.lilith.tarkovesp", "TarkovEsp", "0.2.0")]
[BepInDependency("sptushonka.core", "5.0.0")]
public sealed class TarkovEspPlugin : BasePlugin
{
    internal static ConfigEntry<bool> Enabled, ShowNames, ShowBoxes, ShowSkeleton;
    internal static ConfigEntry<float> MaxDistance, SkeletonDistance;
    internal static ConfigEntry<int> RefreshRate, MaxVisibleTargets, MaxSkeletonTargets;
    internal static ConfigEntry<KeyCode> ToggleKey;
    public override void Load()
    {
        Enabled = Config.Bind("ESP", "Enabled", true, "Enable the player ESP overlay.");
        ShowNames = Config.Bind("ESP", "ShowNames", true, "Draw player names.");
        ShowBoxes = Config.Bind("ESP", "ShowBoxes", true, "Draw player boxes.");
        ShowSkeleton = Config.Bind("ESP", "ShowSkeleton", true, "Draw skeleton lines.");
        MaxDistance = Config.Bind("ESP", "MaxDistance", 300f, new ConfigDescription("Maximum distance in metres.", new AcceptableValueRange<float>(10f, 1000f)));
        RefreshRate = Config.Bind("ESP", "RefreshRate", 30, new ConfigDescription("Maximum projection updates per second.", new AcceptableValueRange<int>(5, 60)));
        MaxVisibleTargets = Config.Bind("ESP", "MaxVisibleTargets", 64, new ConfigDescription("Draw the closest visible targets up to this budget.", new AcceptableValueRange<int>(1, 128)));
        MaxSkeletonTargets = Config.Bind("ESP", "MaxSkeletonTargets", 16, new ConfigDescription("Draw skeletons for at most this many nearby targets; names and boxes have a separate budget.", new AcceptableValueRange<int>(0, 64)));
        SkeletonDistance = Config.Bind("ESP", "SkeletonDistance", 80f, new ConfigDescription("Maximum skeleton distance in metres; distant targets retain names and boxes.", new AcceptableValueRange<float>(10f, 500f)));
        ToggleKey = Config.Bind("ESP", "ToggleKey", KeyCode.Insert, "Toggle ESP drawing.");
        ClassInjector.RegisterTypeInIl2Cpp<TarkovEspOverlay>();
        AddComponent<TarkovEspOverlay>();
        Log.LogInfo("Loaded TarkovEsp 0.2.0: cached SPT world and repaint-only drawing.");
    }
}

public sealed class TarkovEspOverlay : MonoBehaviour
{
    private static readonly int[] Links = { 0,1, 1,2, 2,3, 3,4, 1,5, 5,7, 7,9, 9,11, 1,6, 6,8, 8,10, 10,12, 4,13, 13,14, 4,15, 15,16 };
    private readonly List<Target> _targets = new(32);
    private readonly Dictionary<IntPtr, Target> _byPointer = new();
    private readonly Meter _update = new(), _draw = new();
    private readonly double[] _frames = new double[2048];
    private readonly string _evidenceRoot;
    private readonly string _controlPath;
    private float _nextControl;
    private DateTime _controlWrite;
    private string _phase = "unlabelled";
    private string _qaReferenceId;
    private DateTimeOffset? _controlAppliedAt;
    private GameWorld _world;
    private Player _main;
    private Camera _camera;
    private GUIStyle _style;
    private Texture2D _whiteTexture;
    private Matrix4x4 _matrix;
    private Matrix4x4 _lastView, _lastProjection;
    private int _lastWidth, _lastHeight;
    private Rect _outputRect;
    private float _nextRoster, _nextProjection, _nextSnapshot;
    private int _generation, _visible, _frameCount, _frameIndex;
    private long _repaints, _names, _boxes, _lines, _errors;
    private string _error;
    private bool _started, _enabled;
    public TarkovEspOverlay(IntPtr pointer) : base(pointer)
    {
        string root = Environment.GetEnvironmentVariable("PIT_SPT5_PROBE_ROOT");
        if (!string.IsNullOrWhiteSpace(root))
        {
            _evidenceRoot = Path.Combine(Path.GetFullPath(root), "TarkovEsp");
            _controlPath = Path.Combine(_evidenceRoot, "benchmark-control.json");
        }
    }
    private void Start() { _started = true; _enabled = TarkovEspPlugin.Enabled.Value; _nextSnapshot = Time.realtimeSinceStartup + 5; }
    private void Update()
    {
        if (!_started) return;
        PollQaControl();
        bool measure = _evidenceRoot != null;
        if (_enabled != TarkovEspPlugin.Enabled.Value)
        {
            Snapshot(); _update.Reset(); _draw.Reset(); _frameCount = _frameIndex = 0;
            _enabled = TarkovEspPlugin.Enabled.Value;
        }
        if (measure) { _frames[_frameIndex++ % _frames.Length] = Time.unscaledDeltaTime * 1000d; _frameCount = Math.Min(_frameCount + 1, _frames.Length); }
        long start = measure ? Stopwatch.GetTimestamp() : 0, allocation = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        try
        {
            if (!_enabled) { _visible = 0; return; }
            // Global scene scans hung inside UnityPlayer during menu creation. SPT owns this singleton.
            GameWorld current = Singleton<GameWorld>.Instantiated ? Singleton<GameWorld>.Instance : null;
            if (current == null) { Clear(); return; }
            if (_world == null || current.Pointer != _world.Pointer) { Clear(); _world = current; }
            _main = _world.MainPlayer;
            if (_main == null) { _visible = 0; return; }
            float now = Time.realtimeSinceStartup;
            if (now >= _nextRoster)
            {
                Roster();
                _nextRoster = now + 0.5f;
            }
        }
        catch (Exception ex) { _visible = 0; _errors++; _error = ex.GetType().Name + ": " + ex.Message; }
        finally
        {
            if (measure)
            {
                _update.Add(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocation);
                if (Time.realtimeSinceStartup >= _nextSnapshot) { _nextSnapshot = Time.realtimeSinceStartup + 5; Snapshot(); }
            }
        }
    }
    private void Clear() { _targets.Clear(); _byPointer.Clear(); _world = null; _main = null; _camera = null; _visible = 0; _nextRoster = _nextProjection = 0; }
    private void Roster()
    {
        int generation = ++_generation;
        var players = _world.AllAlivePlayersList;
        int count = players == null ? 0 : Math.Min(players.Count, 128);
        for (int i = 0; i < count; i++)
        {
            Player p = players[i];
            if (p == null || p.Pointer == _main.Pointer || p.IsYourPlayer || p.HealthController == null || !p.HealthController.IsAlive) continue;
            // A PID-bound shadow control may include one real friendly NPC as a
            // visible geometry reference. Normal play retains its faction filter.
            if (p.Side == _main.Side && (_qaReferenceId == null || p.ProfileId != _qaReferenceId)) continue;
            if (!_byPointer.TryGetValue(p.Pointer, out Target target)) { target = new Target(p); _byPointer.Add(p.Pointer, target); _targets.Add(target); }
            target.Generation = generation;
        }
        for (int i = _targets.Count - 1; i >= 0; i--)
            if (_targets[i].Generation != generation) { _byPointer.Remove(_targets[i].Player.Pointer); _targets.RemoveAt(i); }
    }
    private void Project()
    {
        _visible = 0;
        Vector3 origin = _main.Position;
        float max = TarkovEspPlugin.MaxDistance.Value;
        int width = Screen.width, height = Screen.height;
        // EFT's SSAA camera.rect is the internal render area (e.g. 1129x635),
        // which its final blit stretches over the whole display (1920x1080).
        // Applying that rect here would reintroduce the downsampled-pixel offset.
        _outputRect = new Rect(0, 0, width, height);
        bool skeleton = TarkovEspPlugin.ShowSkeleton.Value;
        for (int i = 0; i < _targets.Count; i++)
        {
            Target t = _targets[i]; t.Visible = t.HasSkeleton = false; t.Distance = float.PositiveInfinity;
            if (t.Player == null || t.Player.HealthController == null || !t.Player.HealthController.IsAlive) continue;
            Vector3 feet = t.Player.Position;
            float distance = (feet - origin).sqrMagnitude;
            if (distance > max * max) continue;
            Vector3 head = t.Transforms[0] != null ? t.Transforms[0].position : feet + Vector3.up * 1.7f;
            if (_qaReferenceId != null && t.Id == _qaReferenceId)
            {
                t.ReferenceHeadWorld = head;
                Vector3 point = ScreenPoint(head);
                t.ReferenceHeadGui = new Vector3(point.x, height - point.y, point.z);
            }
            Vector3 bottom = ScreenPoint(feet), top = ScreenPoint(head + Vector3.up * 0.15f);
            if (top.z <= 0 || bottom.z <= 0 || !Finite(top) || !Finite(bottom)) continue;
            float h = Mathf.Abs(bottom.y - top.y), w = h * 0.45f;
            float x = (top.x + bottom.x - w) * 0.5f, y = height - Mathf.Max(top.y, bottom.y);
            if (h < 2 || x + w < 0 || x > width || y + h < 0 || y > height) continue;
            t.Box = new Rect(x, y, w, h); t.Distance = Mathf.Sqrt(distance);
            t.Visible = true;
        }
        // Bound the costly line work independently of roster size. Sorting reuses
        // existing target objects and puts the closest on-screen targets first.
        _targets.Sort(static (a, b) => a.Distance.CompareTo(b.Distance));
        int skeletons = 0, maximum = TarkovEspPlugin.MaxVisibleTargets.Value;
        for (int i = 0; i < _targets.Count; i++)
        {
            Target t = _targets[i];
            if (!t.Visible) continue;
            if (_visible >= maximum) { t.Visible = false; continue; }
            _visible++;
            if (skeleton && skeletons < TarkovEspPlugin.MaxSkeletonTargets.Value && t.Distance <= TarkovEspPlugin.SkeletonDistance.Value)
            {
                t.HasSkeleton = true; skeletons++;
                for (int b = 0; b < t.Transforms.Length; b++)
                {
                    t.Valid[b] = false; var transform = t.Transforms[b]; if (transform == null) continue;
                    Vector3 p = ScreenPoint(transform.position);
                    if (p.z <= 0 || !Finite(p)) continue;
                    t.Points[b] = new Vector2(p.x, height - p.y); t.Valid[b] = true;
                }
            }
        }
    }
    private void OnGUI()
    {
        if (!_started) return;
        Event ev = Event.current;
        if (ev.type == EventType.KeyDown && ev.keyCode == TarkovEspPlugin.ToggleKey.Value) { TarkovEspPlugin.Enabled.Value = !TarkovEspPlugin.Enabled.Value; ev.Use(); }
        if (ev.type == EventType.Repaint)
        {
            // Initialize IMGUI resources while the menu is already rendering,
            // instead of paying their first-use cost when an enemy first appears.
            _style ??= new GUIStyle(GUI.skin.label) { fontSize = 14 };
            _whiteTexture ??= Texture2D.whiteTexture;
        }
        if (!TarkovEspPlugin.Enabled.Value || _world == null || _main == null || ev.type != EventType.Repaint) return;
        bool measure = _evidenceRoot != null;
        long start = measure ? Stopwatch.GetTimestamp() : 0, allocation = measure ? GC.GetAllocatedBytesForCurrentThread() : 0;
        Color color = GUI.color; int depth = GUI.depth; Matrix4x4 matrix = GUI.matrix;
        try
        {
            // EFT can render its world camera into a lower-resolution texture.
            // WorldToScreenPoint returns that texture's pixels, not final IMGUI pixels.
            // Resolve the current gameplay camera and map normalized viewport coordinates
            // to the final display. Reproject after camera motion, during this repaint.
            Camera current = EFT.CameraControl.CameraManager.Instance?.Camera ?? Camera.main;
            if (current == null || !current.isActiveAndEnabled) { _visible = 0; return; }
            Matrix4x4 view = current.worldToCameraMatrix, projection = current.projectionMatrix;
            bool changed = _camera == null || current.Pointer != _camera.Pointer || view != _lastView || projection != _lastProjection || _lastWidth != Screen.width || _lastHeight != Screen.height;
            _camera = current;
            float now = Time.realtimeSinceStartup;
            if (changed || now >= _nextProjection)
            {
                Project(); _error = null;
                _lastView = view; _lastProjection = projection;
                _lastWidth = Screen.width; _lastHeight = Screen.height;
                _nextProjection = now + 1f / Mathf.Clamp(TarkovEspPlugin.RefreshRate.Value, 5, 60);
            }
            if (_visible == 0) return;
            GUI.matrix = _matrix = Matrix4x4.identity;
            GUI.depth = -1000; _repaints++;
            for (int i = 0; i < _targets.Count; i++)
            {
                Target t = _targets[i]; if (!t.Visible) continue;
                GUI.color = t.Color;
                if (TarkovEspPlugin.ShowBoxes.Value) { Box(t.Box); _boxes++; }
                if (TarkovEspPlugin.ShowSkeleton.Value && t.HasSkeleton)
                    for (int p = 0; p < Links.Length; p += 2) { int a = Links[p], b = Links[p + 1]; if (t.Valid[a] && t.Valid[b]) { Line(t.Points[a], t.Points[b]); _lines++; } }
                if (TarkovEspPlugin.ShowNames.Value) { GUI.Label(new Rect(t.Box.x, t.Box.y - 20, 320, 22), t.Name, _style); _names++; }
            }
        }
        catch (Exception ex) { _visible = 0; _errors++; _error = ex.GetType().Name + ": " + ex.Message; }
        finally { GUI.matrix = matrix; GUI.color = color; GUI.depth = depth; if (measure) _draw.Add(Stopwatch.GetTimestamp() - start, GC.GetAllocatedBytesForCurrentThread() - allocation); }
    }
    private Vector3 ScreenPoint(Vector3 world)
    {
        Vector3 p = _camera.WorldToViewportPoint(world);
        p.x = _outputRect.x + p.x * _outputRect.width;
        p.y = _outputRect.y + p.y * _outputRect.height;
        return p;
    }
    private static bool Finite(Vector3 p) => float.IsFinite(p.x) && float.IsFinite(p.y) && float.IsFinite(p.z);
    private void Box(Rect r)
    {
        GUI.DrawTexture(new Rect(r.x, r.y, r.width, 1), _whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.yMax - 1, r.width, 1), _whiteTexture);
        GUI.DrawTexture(new Rect(r.x, r.y, 1, r.height), _whiteTexture);
        GUI.DrawTexture(new Rect(r.xMax - 1, r.y, 1, r.height), _whiteTexture);
    }
    private void Line(Vector2 a, Vector2 b)
    {
        Vector2 d = b - a;
        GUIUtility.RotateAroundPivot(Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg, a);
        GUI.DrawTexture(new Rect(a.x, a.y - 0.5f, d.magnitude, 1), _whiteTexture); GUI.matrix = _matrix;
    }
    private void Snapshot()
    {
        if (_evidenceRoot == null) return;
        try
        {
            Directory.CreateDirectory(_evidenceRoot);
            var targets = new List<object>();
            object reference = null;
            foreach (Target t in _targets) if (t.Visible) targets.Add(new { profileId = t.Id, name = t.Name, side = t.Side, distance = t.Distance, box = new { x = t.Box.x, y = t.Box.y, width = t.Box.width, height = t.Box.height } });
            foreach (Target t in _targets) if (t.Visible && t.Id == _qaReferenceId)
                reference = new { profileId = t.Id, distance = t.Distance,
                    headWorld = new { x = t.ReferenceHeadWorld.x, y = t.ReferenceHeadWorld.y, z = t.ReferenceHeadWorld.z },
                    headGui = new { x = t.ReferenceHeadGui.x, y = t.ReferenceHeadGui.y, z = t.ReferenceHeadGui.z } };
            object camera = _camera == null ? null : new { name = _camera.name, pixelWidth = _camera.pixelWidth, pixelHeight = _camera.pixelHeight, fieldOfView = _camera.fieldOfView,
                outputRect = new { x = _outputRect.x, y = _outputRect.y, width = _outputRect.width, height = _outputRect.height },
                sourceViewport = new { x = _camera.rect.x, y = _camera.rect.y, width = _camera.rect.width, height = _camera.rect.height },
                viewMatrix = _qaReferenceId == null ? null : Matrix(_lastView), projectionMatrix = _qaReferenceId == null ? null : Matrix(_lastProjection),
                renderTextureWidth = _camera.targetTexture?.width, renderTextureHeight = _camera.targetTexture?.height };
            double[] frames = new double[_frameCount]; Array.Copy(_frames, frames, _frameCount); Array.Sort(frames);
            string json = JsonSerializer.Serialize(new { schema = "tarkovesp/esp-draw/2", timestamp = DateTimeOffset.UtcNow, pid = Environment.ProcessId,
                enabled = _enabled, showNames = TarkovEspPlugin.ShowNames.Value, showBoxes = TarkovEspPlugin.ShowBoxes.Value, showSkeleton = TarkovEspPlugin.ShowSkeleton.Value,
                phase = _phase, controlAppliedAt = _controlAppliedAt, qaReferenceProfileId = _qaReferenceId, screenWidth = Screen.width, screenHeight = Screen.height, camera,
                refreshRate = TarkovEspPlugin.RefreshRate.Value, maxDistance = TarkovEspPlugin.MaxDistance.Value,
                maxVisibleTargets = TarkovEspPlugin.MaxVisibleTargets.Value, maxSkeletonTargets = TarkovEspPlugin.MaxSkeletonTargets.Value, skeletonDistance = TarkovEspPlugin.SkeletonDistance.Value,
                trackedCount = _targets.Count, targetCount = _visible, targets, reference,
                repaintPasses = _repaints, totalNameDraws = _names, totalBoxDraws = _boxes, totalSkeletonLines = _lines, update = _update.Export(), draw = _draw.Export(), frameSamples = _frameCount,
                frameP50Ms = frames.Length == 0 ? 0 : frames[frames.Length / 2], frameP95Ms = frames.Length == 0 ? 0 : frames[Math.Min(frames.Length - 1, (int)(frames.Length * 0.95))], errorCount = _errors, lastError = _error });
            File.WriteAllText(Path.Combine(_evidenceRoot, "draw-stats.json"), json);
            File.AppendAllText(Path.Combine(_evidenceRoot, "performance.jsonl"), json + Environment.NewLine);
        }
        catch (IOException) { }
    }
    private static float[] Matrix(Matrix4x4 value)
    {
        var result = new float[16];
        for (int row = 0; row < 4; row++) for (int col = 0; col < 4; col++) result[row * 4 + col] = value[row, col];
        return result;
    }
    private void PollQaControl()
    {
        // This control exists only in explicitly instrumented shadow runs. No OS input/focus is needed.
        if (_controlPath == null || Time.realtimeSinceStartup < _nextControl) return;
        _nextControl = Time.realtimeSinceStartup + 0.5f;
        try
        {
            DateTime write = File.GetLastWriteTimeUtc(_controlPath);
            if (write == _controlWrite || !File.Exists(_controlPath)) return;
            _controlWrite = write;
            using var doc = JsonDocument.Parse(File.ReadAllText(_controlPath));
            var c = doc.RootElement;
            if (!c.TryGetProperty("pid", out var pid) || !pid.TryGetInt32(out int owner) || owner != Environment.ProcessId) return;
            if (!c.TryGetProperty("phase", out var phaseValue) || phaseValue.ValueKind != JsonValueKind.String) return;
            string phase = phaseValue.GetString();
            if (string.IsNullOrWhiteSpace(phase) || phase == _phase) return;
            foreach (string key in new[] { "enabled", "names", "boxes", "skeleton" })
                if (!c.TryGetProperty(key, out var value) || (value.ValueKind != JsonValueKind.True && value.ValueKind != JsonValueKind.False)) return;
            string reference = null;
            if (c.TryGetProperty("referenceProfileId", out var referenceValue) && referenceValue.ValueKind != JsonValueKind.Null)
            {
                if (referenceValue.ValueKind != JsonValueKind.String) return;
                reference = referenceValue.GetString();
                if (reference.Length != 24) return;
                foreach (char digit in reference) if (!Uri.IsHexDigit(digit)) return;
            }
            Snapshot();
            _qaReferenceId = reference; _nextRoster = _nextProjection = 0;
            _phase = phase; _controlAppliedAt = DateTimeOffset.UtcNow;
            TarkovEspPlugin.Enabled.Value = _enabled = c.GetProperty("enabled").GetBoolean();
            TarkovEspPlugin.ShowNames.Value = c.GetProperty("names").GetBoolean();
            TarkovEspPlugin.ShowBoxes.Value = c.GetProperty("boxes").GetBoolean();
            TarkovEspPlugin.ShowSkeleton.Value = c.GetProperty("skeleton").GetBoolean();
            _update.Reset(); _draw.Reset(); _frameCount = _frameIndex = 0;
            _repaints = _names = _boxes = _lines = _errors = 0;
            _nextSnapshot = Time.realtimeSinceStartup + 5;
        }
        catch (IOException) { }
        catch (JsonException) { }
    }
    private sealed class Target
    {
        public readonly Player Player; public readonly string Id, Name, Side; public readonly Color Color;
        public readonly Transform[] Transforms = new Transform[17]; public readonly Vector2[] Points = new Vector2[17]; public readonly bool[] Valid = new bool[17];
        public int Generation; public bool Visible, HasSkeleton; public Rect Box; public float Distance;
        public Vector3 ReferenceHeadWorld, ReferenceHeadGui;
        public Target(Player p)
        {
            Player = p; Id = p.ProfileId; Name = p.Profile?.Nickname ?? "Enemy"; Side = p.Side.ToString();
            Color = p.Side == EPlayerSide.Bear ? new Color(1, 0.35f, 0.2f, 1) : p.Side == EPlayerSide.Usec ? new Color(0.25f, 0.7f, 1, 1) : new Color(1, 0.8f, 0.2f, 1);
            PlayerBones b = p.PlayerBones; if (b == null) return;
            Transforms[0] = b.Head?.Original; Transforms[1] = b.Neck; Transforms[2] = b.Spine1; Transforms[3] = b.Spine3?.Original; Transforms[4] = b.Pelvis?.Original;
            Transforms[5] = b.LeftShoulder?.Original; Transforms[6] = b.RightShoulder?.Original;
            if (b.Upperarms != null && b.Upperarms.Length >= 2) { Transforms[7] = b.Upperarms[0]; Transforms[8] = b.Upperarms[1]; }
            if (b.Forearms != null && b.Forearms.Length >= 2) { Transforms[9] = b.Forearms[0]; Transforms[10] = b.Forearms[1]; }
            Transforms[11] = b.LeftPalm; Transforms[12] = b.RightPalm; Transforms[13] = b.LeftThigh1?.Original; Transforms[14] = b.LeftThigh2?.Original; Transforms[15] = b.RightThigh1?.Original; Transforms[16] = b.RightThigh2?.Original;
        }
    }
    private sealed class Meter
    {
        private readonly double[] _samples = new double[2048]; private long _calls, _ticks, _allocated, _max;
        public void Add(long ticks, long allocated) { _samples[_calls++ % _samples.Length] = ticks * 1000d / Stopwatch.Frequency; _ticks += ticks; _allocated += allocated; _max = Math.Max(_max, ticks); }
        public void Reset() { _calls = _ticks = _allocated = _max = 0; }
        public object Export()
        {
            int n = (int)Math.Min(_calls, _samples.Length); var sorted = new double[n]; Array.Copy(_samples, sorted, n); Array.Sort(sorted);
            return new { calls = _calls, meanMs = _calls == 0 ? 0 : _ticks * 1000d / Stopwatch.Frequency / _calls, p95Ms = n == 0 ? 0 : sorted[Math.Min(n - 1, (int)(n * 0.95))], maxMs = _max * 1000d / Stopwatch.Frequency, allocatedBytes = _allocated, bytesPerCall = _calls == 0 ? 0 : _allocated / (double)_calls };
        }
    }
}
