using System.Collections.Generic;
using System.IO;
using Godless.Meshing;
using Godless.Sim.Chronicle;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Harness;
using Godless.Sim.Life;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Godless.Unity
{
    /// <summary>
    /// Builds a world and puts it on screen. The Unity side's only entry
    /// point: everything it does is a call into /Sim, which is the point of
    /// law L1 — the game runs identically without this file.
    ///
    /// v2 M0: choose a map, and the world runs. There is nobody on it yet;
    /// what there is, is the ground, the god's hand on it through the command
    /// queue, and the feed telling the player what happened and where.
    ///
    /// Content loads from Assets/Content through the same pipeline the
    /// headless harness uses (L5). That path is Editor-only for now; a player
    /// build will need it copied to StreamingAssets, which is ship work.
    /// </summary>
    [RequireComponent(typeof(WorldRenderer))]
    public sealed class WorldBootstrap : MonoBehaviour
    {
        [Tooltip("World seed. The same seed gives the same world on every machine.")]
        [SerializeField] long seed = 7;

        [Tooltip("Which world in Assets/Content to generate. Empty is the built-in island with every biome. See `sim maps`.")]
        [SerializeField] string map = "green-shore";

        [Tooltip("Grow the woods, reed beds and rock that will be gathered and used up.")]
        [SerializeField] bool plantDeposits = true;

        [Tooltip("Place the main camera over the island on start.")]
        [SerializeField] bool frameCamera = true;

        [SerializeField] bool showStats = true;

        [Tooltip("Show the map chooser before anything runs. Off starts on the map and seed above, as tools and captures expect.")]
        [SerializeField] bool chooseBeforePlay = true;

        [Tooltip("Frames a second the Editor and player may render. 0 leaves it to the platform. Vsync is turned off so this is the cap that holds.")]
        [SerializeField] int maxFramesPerSecond = 100;

        [Tooltip("Speed to start at, as an index into TickPacer.Multipliers (0 paused, 1 is 1x).")]
        [SerializeField] int startSpeed = 1;

        [Tooltip("Steps a frame when the renderer is keeping up. Lower if a fast speed makes the frame stutter.")]
        [SerializeField] int ticksPerFrame = 32;

        [Tooltip("Lines the feed shows at once.")]
        [SerializeField] int feedLines = 8;

        public SimWorld World { get; private set; }
        public WorldRenderer View { get; private set; }

        /// <summary>The planning grid, kept current by the ground system as the ground changes.</summary>
        public ParcelGrid Parcels { get; private set; }

        /// <summary>How fast the world runs while somebody is watching. Never seen by the simulation.</summary>
        public TickPacer Pacer { get; private set; }

        /// <summary>What the ground is made of, by place and height — the god's brush builds from this.</summary>
        public GroundPalette Ground { get; private set; }

        /// <summary>The map this world was generated on. Never null once the world exists.</summary>
        public WorldPreset Map { get; private set; }

        /// <summary>Life on the land, for the god's powers over it (v2 M1).</summary>
        public LifeSystem Life { get; private set; }

        /// <summary>What happened, told to the player (v2 M0).</summary>
        public EventFeed Feed { get; private set; }

        /// <summary>Where the game is: choosing a world, or under way.</summary>
        public enum SetupPhase { ChoosingMap, Playing }

        public SetupPhase Phase { get; private set; }

        // Kept across a return to the map chooser, which reloads the scene.
        static string _lastMap;
        static long _lastSeed = long.MinValue;

        ContentDatabase _content;
        VoxelTypes _types;
        WorldTable _maps;
        BiomeTable _biomes;
        ConstraintFields _fields;
        int _mapIndex;
        string _seedText = "7";
        Rect _panel;

        struct Shown { public FeedItem Item; public float Since; }
        readonly List<Shown> _feed = new List<Shown>();

        float _smoothedFrame = 1f / 60f;
        float _loadSeconds;
        int _deltaCursor;

        void Awake()
        {
            if (GetComponent<DetailRenderer>() == null) gameObject.AddComponent<DetailRenderer>();
            if (GetComponent<CreatureView>() == null) gameObject.AddComponent<CreatureView>();
        }

        void Start()
        {
            // A cap on rendering only. It changes how many frames there are to
            // spread steps across, never how many steps run: the pacer works
            // from real seconds, so a capped frame simply takes more of them.
            ApplyFrameCap();

            string root = Path.Combine(Application.dataPath, "Content");
            LoadResult content = ContentLoader.Load(new DirectoryContentSource(root));
            foreach (string warning in content.Warnings) Debug.LogWarning("content: " + warning);
            _content = content.Database;
            _types = VoxelTypes.FromContent(_content);
            _maps = WorldTable.FromContent(_content, BiomeTable.FromContent(_content));
            foreach (string problem in _maps.Problems) Debug.LogWarning("content: " + problem);

            string wantMap = _lastMap ?? (map == null ? "" : map.Trim());
            long wantSeed = _lastSeed != long.MinValue ? _lastSeed : seed;
            _seedText = wantSeed.ToString();
            for (int i = 0; i < _maps.Count; i++) if (_maps[i].Name == wantMap) _mapIndex = i;

            if (!chooseBeforePlay) { Generate(wantMap, wantSeed); return; }
            Phase = SetupPhase.ChoosingMap;
        }

        /// <summary>
        /// Makes the world for a map and seed, puts it on screen and sets it
        /// running. True when there is a world.
        /// </summary>
        public bool Generate(string mapName, long worldSeed)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            seed = worldSeed;
            map = mapName;
            _lastMap = mapName;
            _lastSeed = worldSeed;

            WorldChoice choice;
            try { choice = WorldChoice.Pick(_content, mapName ?? ""); }
            catch (ContentException e)
            {
                // A misspelled map in the inspector is a content problem, not
                // a crash: say what there is and fall back to the plain island.
                Debug.LogWarning("Godless: " + e.Message);
                choice = WorldChoice.Pick(_content, "");
            }

            _biomes = choice.Biomes;
            if (_biomes.Count == 0)
            {
                // L5's tell, seen from the Unity side: no content is not a crash.
                Debug.Log("No biomes declared — booting with nothing to build.");
                Phase = SetupPhase.Playing;
                return false;
            }

            World = new SimWorld((ulong)worldSeed, _content, _types);
            TimeRules time = World.Time;
            Pacer = new TickPacer(time.TicksPerSecondAt1x / time.TicksPerDay, World.Clock.TicksPerDay, startSpeed);
            World.Island = IslandGenerator.Generate(World.Voxels.Store, World.Streams, _biomes, _types, choice.Preset,
                                                    plantDeposits ? choice.Features : null);
            foreach (string problem in choice.Features.Problems) Debug.LogWarning("content: " + problem);
            Ground = GroundPalette.From(World.Island, _biomes, _types);
            Map = choice.Preset;

            // The untouched island is history's baseline (S04): everything the
            // player does is replayed forward from here when scrubbing back.
            World.BeginHistory();

            View = GetComponent<WorldRenderer>();
            View.Bind(World.Voxels.Store, VoxelVisuals.FromContent(_content, _types));

            Parcels = Survey.Of(World, _content, _biomes, out _fields);
            Life = Genesis.AddSystems(World, _content, Parcels, _fields, _biomes);
            foreach (string problem in Life.Life.Species.Problems) Debug.LogWarning("content: " + problem);

            Feed = EventFeed.FromContent(_content);
            foreach (string problem in Feed.Problems) Debug.LogWarning("content: " + problem);
            Feed.SkipTo(World.Annals);
            _feed.Clear();

            _deltaCursor = World.Voxels.Log.Count;
            _loadSeconds = (float)watch.Elapsed.TotalSeconds;
            Debug.Log("Godless: " + Map.Title + " (" + Map.Name + "), seed " + worldSeed
                      + ", world generated in " + _loadSeconds.ToString("0.00") + "s" + "\n" + Map.Tell);

            if (frameCamera) FrameCamera();
            Phase = SetupPhase.Playing;
            return true;
        }

        /// <summary>Back to the map chooser: the scene again from the top, with the last map and seed remembered.</summary>
        public void ChooseAnotherWorld()
        {
            Scene scene = SceneManager.GetActiveScene();
#if UNITY_EDITOR
            UnityEditor.SceneManagement.EditorSceneManager.LoadSceneInPlayMode(scene.path, new LoadSceneParameters(LoadSceneMode.Single));
#else
            SceneManager.LoadScene(scene.buildIndex);
#endif
        }

        void ApplyFrameCap()
        {
            // Vsync overrides targetFrameRate when it is on, so a cap without
            // this line is a cap at the monitor's refresh rate instead.
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = maxFramesPerSecond > 0 ? maxFramesPerSecond : -1;
        }

        void OnValidate()
        {
            if (Application.isPlaying) ApplyFrameCap();
        }

        void FrameCamera()
        {
            Camera cam = Camera.main;
            if (cam == null) return;
            if (cam.GetComponent<GodCamera>() != null) return;   // it frames itself

            var centre = new Vector3(ChunkStore.SizeX * 0.5f,
                                     World.Island != null ? World.Island.SeaLevel : IslandMap.DefaultSeaLevel,
                                     ChunkStore.SizeZ * 0.5f);
            cam.transform.position = centre + new Vector3(0f, 250f, -330f);
            cam.transform.LookAt(centre + new Vector3(0f, 0f, 30f));
            cam.nearClipPlane = 0.5f;
            cam.farClipPlane = 2000f;
        }

        void Update()
        {
            float dt = Time.unscaledDeltaTime;
            _smoothedFrame = Mathf.Lerp(_smoothedFrame, dt, 0.05f);
            RunSim(dt);
            ReadFeed();
        }

        /// <summary>
        /// Advances the simulation in whole steps, then tells the renderer
        /// which chunks changed. The sim never touches Unity and Unity never
        /// touches the voxels: it reads the delta log, which is there for
        /// exactly this (S04). How many steps is the pacer's business — the
        /// world does not know what speed it is being watched at.
        /// </summary>
        void RunSim(float dt)
        {
            if (World == null || Pacer == null || Phase != SetupPhase.Playing) return;

            // Paused, the god still acts: whatever is waiting lands in the present.
            if (Pacer.IsPaused && World.Commands.Pending > 0) { World.Commands.ApplyNow(World); ShowChanges(); }

            Pacer.Advance(dt);
            int ticks = Pacer.Take(FrameBudget());
            if (ticks > 0) RunTicks(ticks);
        }

        /// <summary>
        /// Steps this frame can afford. A fast speed must not outrun the
        /// mesher: the queue is the honest signal that the picture is falling
        /// behind the world, and the steps it cannot afford stay owed rather
        /// than being lost.
        /// </summary>
        int FrameBudget()
        {
            int waiting = View.ChunksQueued + View.ChunksInFlight;
            if (waiting > 24) return 1;
            if (waiting > 8) return Mathf.Max(1, ticksPerFrame / 4);
            return ticksPerFrame;
        }

        /// <summary>
        /// Runs whole steps and hands the renderer what changed. Public so a
        /// tool can wind the world on without pretending to be a frame.
        /// </summary>
        public void RunTicks(int ticks)
        {
            for (int i = 0; i < ticks; i++) World.Tick();
            ShowChanges();
        }

        /// <summary>
        /// Hands the renderer every voxel changed since it last looked, from
        /// the delta log — the simulation's steps and the god's hand alike, so
        /// an act while paused is drawn at once and never drawn twice.
        /// </summary>
        public void ShowChanges()
        {
            if (World == null || View == null) return;
            IReadOnlyList<VoxelDelta> log = World.Voxels.Log.All();
            for (; _deltaCursor < log.Count; _deltaCursor++)
                View.MarkDirty(ChunkStore.PositionOf(log[_deltaCursor].ChunkIndex, log[_deltaCursor].VoxelIndex));
        }

        void ReadFeed()
        {
            if (World == null || Feed == null) return;
            foreach (FeedItem item in Feed.Read(World.Annals))
            {
                // A held brush is a dozen acts a second: one line, counted, not a dozen.
                if (_feed.Count > 0 && _feed[_feed.Count - 1].Item.Kind == item.Kind && Time.unscaledTime - _feed[_feed.Count - 1].Since < 2f)
                {
                    Shown last = _feed[_feed.Count - 1];
                    last.Item.Place = item.Place;
                    last.Since = Time.unscaledTime;
                    _feed[_feed.Count - 1] = last;
                    continue;
                }
                _feed.Add(new Shown { Item = item, Since = Time.unscaledTime });
            }
            while (_feed.Count > 40) _feed.RemoveAt(0);
        }

        /// <summary>Takes the camera to where a feed line happened.</summary>
        public void GoTo(Int3 place)
        {
            GodCamera god = FindFirstObjectByType<GodCamera>();
            if (god == null || World == null) return;
            float y = Parcels != null ? Parcels.GroundAt(Mathf.Clamp(place.X, 0, ChunkStore.SizeX - 1), Mathf.Clamp(place.Z, 0, ChunkStore.SizeZ - 1)) : place.Y;
            god.Focus(new Vector3(place.X, y, place.Z), 90f);
        }

        void OnGUI()
        {
            if (Phase == SetupPhase.ChoosingMap) { MapChooser(); return; }
            if (World == null) return;
            Styles();

            // The top line: when it is and how fast it runs.
            string when = "Year " + (World.Clock.Year + 1) + ", month " + (World.Clock.Month + 1) + "   ·   "
                        + Pacer.Label + (Pacer.Behind > 4.0 ? "  (the picture is behind the world)" : "");
            GUI.Label(new Rect(14, 10, 700, 26), when, _heading);
            if (World.Life != null)
            {
                Living life = World.Life;
                var counts = new System.Text.StringBuilder();
                int bands = 0, settled = 0;
                foreach (Group b in life.Bands) if (!b.Gone) { bands++; if (b.Settled) settled++; }
                for (int s = 0; s < life.Species.Count; s++)
                {
                    Species sp = life.Species[s];
                    int n = life.CountOf(s);
                    if (counts.Length > 0) counts.Append("   ");
                    counts.Append(n).Append(' ').Append(n == 1 ? sp.Singular : sp.Plural);
                    if (sp.Person) counts.Append(" in ").Append(bands).Append(bands == 1 ? " band" : " bands").Append(settled > 0 ? " (" + settled + " settled)" : "");
                }
                GUI.Label(new Rect(14, 96, 900, 22), counts.ToString(), _small);
            }

            string keys = "";
            if (GetComponent<SimSpeed>() != null) keys += SimSpeed.Keys;
            if (GetComponent<TerrainEditor>() != null) keys += "\n" + TerrainEditor.Keys;
            keys += "\nright-drag turn   WASD move   scroll zoom";
            GUI.Label(new Rect(14, 36, 900, 60), keys, _small);

            if (showStats)
                GUI.Label(new Rect(Screen.width - 330, 10, 320, 60),
                          (1f / _smoothedFrame).ToString("0") + " fps · chunks " + View.ChunksDrawn + " · meshing " + (View.ChunksQueued + View.ChunksInFlight)
                          + "\n" + (Map != null ? Map.Title : "") + ", seed " + seed + " · made in " + _loadSeconds.ToString("0.0") + " s", _small);

            FeedPanel();

            // Top right, under the stats: the bottom edge is the timeline's.
            if (GUI.Button(new Rect(Screen.width - 180, 70, 166, 28), "Choose another world")) ChooseAnotherWorld();
        }

        /// <summary>The feed: newest at the bottom, fading with age; click a line to go there.</summary>
        void FeedPanel()
        {
            // Only as many lines as fit between the top lines and the power bar.
            int fits = Mathf.Max(1, (int)((Screen.height - 160f - 124f) / 24f));
            int shown = Mathf.Min(Mathf.Min(feedLines, fits), _feed.Count);
            if (shown == 0) return;
            float lineH = 24f, w = Mathf.Min(460f, Screen.width - 40f);
            float y = 124f;
            _panel = new Rect(14, y - 6, w, shown * lineH + 12);
            GUI.Box(_panel, GUIContent.none, _box);
            for (int i = _feed.Count - shown; i < _feed.Count; i++)
            {
                Shown s = _feed[i];
                float age = Time.unscaledTime - s.Since;
                Color was = GUI.color;
                GUI.color = new Color(1f, 1f, 1f, Mathf.Clamp01(1.2f - age / 30f) * 0.6f + 0.4f);
                string stamp = "Y" + (s.Item.Tick / World.Clock.TicksInYears(1) + 1) + "  ";
                GUIStyle style = s.Item.Importance >= 3 ? _feedLoud : _feedLine;
                if (GUI.Button(new Rect(20, y, w - 12, lineH), stamp + s.Item.Text, style)) GoTo(s.Item.Place);
                GUI.color = was;
                y += lineH;
            }
        }

        // ── the screen before the world runs ────────────────────────────────

        GUIStyle _title, _body, _small, _button, _box, _heading, _feedLine, _feedLoud;
        Vector2 _mapScroll;

        void Styles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, wordWrap = true };
            _heading = new GUIStyle(GUI.skin.label) { fontSize = 17, fontStyle = FontStyle.Bold };
            _body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 12, wordWrap = true };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 15, alignment = TextAnchor.UpperLeft, wordWrap = true, padding = new RectOffset(12, 12, 10, 10) };
            _box = new GUIStyle(GUI.skin.box);
            _feedLine = new GUIStyle(GUI.skin.label) { fontSize = 13, alignment = TextAnchor.MiddleLeft };
            _feedLoud = new GUIStyle(_feedLine) { fontStyle = FontStyle.Bold };
        }

        /// <summary>Which world: the maps content declares, what each is, and the seed.</summary>
        void MapChooser()
        {
            Styles();
            float w = Mathf.Min(760f, Screen.width - 40f), h = Mathf.Min(640f, Screen.height - 40f);
            _panel = new Rect((Screen.width - w) * 0.5f, (Screen.height - h) * 0.5f, w, h);
            GUI.Box(_panel, GUIContent.none, _box);
            GUILayout.BeginArea(new Rect(_panel.x + 20, _panel.y + 16, w - 40, h - 32));

            GUILayout.Label("Godless", _title);
            GUILayout.Label("Choose a world. Civilisations will rise and fall on it, and reshape it as they go; you may touch anything.", _body);
            GUILayout.Space(10);

            _mapScroll = GUILayout.BeginScrollView(_mapScroll, GUILayout.ExpandHeight(true));
            if (_maps == null || _maps.Count == 0) GUILayout.Label("No maps in content: the built-in island will be made.", _body);
            else
                for (int i = 0; i < _maps.Count; i++)
                {
                    WorldPreset preset = _maps[i];
                    bool selected = i == _mapIndex;
                    string label = (selected ? "▶ " : "   ") + preset.Title + "\n" + preset.Tell;
                    Color was = GUI.backgroundColor;
                    if (selected) GUI.backgroundColor = new Color(1f, 0.75f, 0.4f);
                    float tall = _button.CalcHeight(new GUIContent(label), w - 80f) + 4f;
                    if (GUILayout.Button(label, _button, GUILayout.Height(tall))) _mapIndex = i;
                    GUI.backgroundColor = was;
                }
            GUILayout.EndScrollView();

            GUILayout.Space(8);
            GUILayout.BeginHorizontal();
            GUILayout.Label("Seed", _body, GUILayout.Width(50));
            _seedText = GUILayout.TextField(_seedText, 18, GUILayout.Width(200), GUILayout.Height(26));
            if (GUILayout.Button("Another", GUILayout.Width(90), GUILayout.Height(26)))
                _seedText = (System.DateTime.Now.Ticks % 1000000L).ToString();   // presentation only: the sim sees the number, never the clock
            GUILayout.FlexibleSpace();
            plantDeposits = GUILayout.Toggle(plantDeposits, " woods, reeds and rock", GUILayout.Height(26));
            GUILayout.EndHorizontal();

            GUILayout.Space(8);
            long parsed;
            bool seedOk = long.TryParse(_seedText, out parsed);
            GUI.enabled = seedOk;
            if (GUILayout.Button(seedOk ? "Make this world" : "The seed must be a whole number", GUILayout.Height(44)))
            {
                string name = _maps != null && _maps.Count > 0 ? _maps[_mapIndex].Name : "";
                Generate(name, parsed);
            }
            GUI.enabled = true;
            GUILayout.EndArea();
        }
    }
}
