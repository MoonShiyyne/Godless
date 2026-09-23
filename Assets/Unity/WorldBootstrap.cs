using System.Collections.Generic;
using System.IO;
using Godless.Meshing;
using Godless.Sim.Build;
using Godless.Sim.Content;
using Godless.Sim.Core;
using Godless.Sim.Deltas;
using Godless.Sim.Drives;
using Godless.Sim.Harness;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.SceneManagement;

namespace Godless.Unity
{
    /// <summary>
    /// Builds a world and puts it on screen. The Unity side's only entry
    /// point: everything it does is a call into /Sim, which is the point of
    /// law L1 — the game runs identically without this file.
    ///
    /// Content loads from Assets/Content through the same pipeline the
    /// headless harness uses (L5). That path is Editor-only for now; a player
    /// build will need it copied to StreamingAssets, which is ship work.
    /// </summary>
    [RequireComponent(typeof(WorldRenderer))]
    public sealed class WorldBootstrap : MonoBehaviour
    {
        [Tooltip("World seed. The same seed gives the same island on every machine.")]
        [SerializeField] long seed = 7;

        [Tooltip("Which world in Assets/Content to generate. Empty is the built-in island with every biome. See `sim maps`.")]
        [SerializeField] string map = "green-shore";

        [Tooltip("Grow the woods, reed beds and rock that settlements gather from and use up (S2F). Off gives the bare island stratum 1 was built on.")]
        [SerializeField] bool plantDeposits = true;

        [Tooltip("Draw the people as sprites, coloured by what they are doing (S2G). Hover one to read it.")]
        [SerializeField] bool showPeople = true;

        [Tooltip("Place the main camera over the island on start.")]
        [SerializeField] bool frameCamera = true;

        [SerializeField] bool showStats = true;

        [Tooltip("Show the map chooser and let the player pick where the first fire is lit before anything runs. Off starts on the map and seed above, at the suggested site, as tools and captures expect.")]
        [SerializeField] bool chooseBeforePlay = true;

        [Tooltip("Frames a second the Editor and player may render. 0 leaves it to the platform. Vsync is turned off so this is the cap that holds.")]
        [SerializeField] int maxFramesPerSecond = 100;

        [Header("Settlement (stratum 1)")]
        [Tooltip("Put twenty people on the island and let them build.")]
        [SerializeField] bool settle = true;

        [SerializeField] int people = 20;

        // S2W: a day used to pass in half a second at 1x, which made every walk a
        // blink. A new field rather than a new value, so the old one saved in
        // the scene is not read back over it.
        [Tooltip("Real seconds a simulated day takes at 1x: long enough to watch people walk out to their work and home again. Speed never reaches the simulation; it only decides how many whole ticks run this frame.")]
        [SerializeField] float secondsPerDayAt1x = 120f;

        [Tooltip("Speed to start at, as an index into TickPacer.Multipliers (0 paused, 1 is 1x).")]
        [SerializeField] int startSpeed = 1;

        [Tooltip("Ticks a frame when the renderer is keeping up. Lower if a fast speed makes the frame stutter.")]
        [SerializeField] int ticksPerFrame = 16;

        public SimWorld World { get; private set; }
        public WorldRenderer View { get; private set; }

        /// <summary>The island's planning grid, or null in a world nobody settled.</summary>
        public ParcelGrid Parcels { get; private set; }

        public Settlement Town { get; private set; }

        /// <summary>How fast the world runs while somebody is watching. Never seen by the simulation.</summary>
        public TickPacer Pacer { get; private set; }

        CommonsRules _commonsRules;

        /// <summary>What the ground is made of, by place and height — the god's brush builds from this.</summary>
        public GroundPalette Ground { get; private set; }

        /// <summary>The map this world was generated on. Never null once the world exists.</summary>
        public WorldPreset Map { get; private set; }

        /// <summary>Where the game is before it runs: choosing a world, choosing a site, or under way.</summary>
        public enum SetupPhase { ChoosingMap, ChoosingSite, Playing }

        public SetupPhase Phase { get; private set; }

        /// <summary>The suggested site, in parcels, once a world is made; -1 when the island has none.</summary>
        public int SuggestedX { get; private set; } = -1;
        public int SuggestedZ { get; private set; } = -1;

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

        SiteReport _hover, _chosen;
        GameObject _hoverMarker, _chosenMarker;
        LineRenderer _reachRing;
        Rect _panel;

        float _smoothedFrame = 1f / 60f;
        float _worstFrame;
        float _loadSeconds;
        int _deltaCursor;

        void Awake()
        {
            // Added in code rather than in the scene, so a scene saved before
            // S2G still shows its people.
            if (showPeople && GetComponent<PeopleView>() == null) gameObject.AddComponent<PeopleView>();
            if (GetComponent<DetailRenderer>() == null) gameObject.AddComponent<DetailRenderer>();
            if (GetComponent<CutawayView>() == null) gameObject.AddComponent<CutawayView>();
            if (GetComponent<BorderView>() == null) gameObject.AddComponent<BorderView>();
        }

        void Start()
        {
            // A cap on rendering only. It changes how many frames there are to
            // spread ticks across, never how many ticks run: the pacer works
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

            if (!chooseBeforePlay)
            {
                if (!Generate(wantMap, wantSeed)) return;
                if (settle && SuggestedX >= 0) FoundAt(SuggestedX, SuggestedZ);
                else Phase = SetupPhase.Playing;
                return;
            }
            Phase = SetupPhase.ChoosingMap;
        }

        /// <summary>
        /// Makes the island for a map and seed and puts it on screen, with
        /// nobody on it yet. True when there is a world to settle.
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
            Pacer = new TickPacer(1.0 / Mathf.Max(1f, secondsPerDayAt1x), World.Clock.TicksPerDay, startSpeed);
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

            Parcels = Founding.Survey(World, _content, _biomes, out _fields);
            int px, pz;
            if (Founding.StandInSite(Parcels, World.Island, _biomes, Symbol.For("biome.temperate"), out px, out pz)
                || Founding.StandInSite(Parcels, World.Island, _biomes, Symbol.None, out px, out pz))
            { SuggestedX = px; SuggestedZ = pz; }

            _deltaCursor = World.Voxels.Log.Count;
            _loadSeconds = (float)watch.Elapsed.TotalSeconds;
            Debug.Log("Godless: " + Map.Title + " (" + Map.Name + "), seed " + worldSeed
                      + ", island generated in " + _loadSeconds.ToString("0.00") + "s" + "\n" + Map.Tell);

            if (frameCamera) FrameCamera();
            Phase = settle ? SetupPhase.ChoosingSite : SetupPhase.Playing;
            if (Phase == SetupPhase.ChoosingSite && SuggestedX >= 0) Choose(SuggestedX, SuggestedZ, true);
            return true;
        }

        /// <summary>
        /// Lights the first fire at a parcel and starts the world: twenty people,
        /// everything stratum 1 and 2 gives them, and the clock running. False,
        /// and nothing happens, when people could not live there.
        /// </summary>
        public bool FoundAt(int px, int pz)
        {
            if (World == null || Town != null) return false;
            SiteReport report = Founding.Appraise(World, _content, Parcels, _biomes, px, pz);
            if (!report.CanSettle)
            {
                Debug.Log("Godless: cannot settle at parcel (" + px + ", " + pz + "): " + report.Why);
                return false;
            }

            Town = Founding.Begin(World, _content, Parcels, _biomes, "first", people, px, pz, null);
            Founding.AddSystems(World, _content, Parcels, _fields, _biomes);
            _deltaCursor = World.Voxels.Log.Count;
            Phase = SetupPhase.Playing;
            ClearMarkers();
            Debug.Log("Godless: " + people + " people settled at parcel (" + px + ", " + pz + ")");
            return true;
        }

        // ── choosing the site ───────────────────────────────────────────────

        /// <summary>Makes a parcel the chosen site: appraised, marked, and the camera turned to it.</summary>
        void Choose(int px, int pz, bool focus)
        {
            _chosen = Founding.Appraise(World, _content, Parcels, _biomes, px, pz);
            Vector3 at = ParcelTop(px, pz);
            if (_chosenMarker == null) _chosenMarker = Marker("Chosen fire", new Color(1f, 0.55f, 0.1f), 1.6f, 10f);
            _chosenMarker.transform.position = at + Vector3.up * 5f;
            _chosenMarker.GetComponent<Renderer>().sharedMaterial.color = _chosen.CanSettle ? new Color(1f, 0.55f, 0.1f) : new Color(0.8f, 0.15f, 0.1f);
            DrawReach(at);
            GodCamera god = FindFirstObjectByType<GodCamera>();
            if (focus && god != null) god.Focus(at, 170f);
        }

        void UpdateSitePicking()
        {
            Mouse mouse = Mouse.current;
            Camera cam = Camera.main;
            if (mouse == null || cam == null || World == null) return;

            Vector2 pointer = mouse.position.ReadValue();
            bool overPanel = _panel.Contains(new Vector2(pointer.x, Screen.height - pointer.y));

            int px = -1, pz = -1;
            if (!overPanel)
            {
                Ray ray = cam.ScreenPointToRay(pointer);
                VoxelRaycast.Hit hit;
                if (VoxelRaycast.Cast(World.Voxels.Store, ray.origin.x, ray.origin.y, ray.origin.z,
                                      ray.direction.x, ray.direction.y, ray.direction.z, 3000,
                                      id => id != VoxelTypes.AirId, out hit))
                { px = hit.Voxel.X / ParcelGrid.Size; pz = hit.Voxel.Z / ParcelGrid.Size; }
            }

            if (px < 0) { _hover = null; if (_hoverMarker != null) _hoverMarker.SetActive(false); }
            else
            {
                if (_hover == null || _hover.ParcelX != px || _hover.ParcelZ != pz)
                    _hover = Founding.Appraise(World, _content, Parcels, _biomes, px, pz);
                if (_hoverMarker == null) _hoverMarker = Marker("Site under the pointer", Color.white, 0.8f, 6f);
                _hoverMarker.SetActive(true);
                _hoverMarker.transform.position = ParcelTop(px, pz) + Vector3.up * 3f;
                _hoverMarker.GetComponent<Renderer>().sharedMaterial.color = _hover.CanSettle ? new Color(0.35f, 0.9f, 0.4f) : new Color(0.9f, 0.25f, 0.2f);
                if (mouse.leftButton.wasPressedThisFrame) Choose(px, pz, false);
            }

            Keyboard keys = Keyboard.current;
            if (keys != null && (keys.enterKey.wasPressedThisFrame || keys.numpadEnterKey.wasPressedThisFrame) && _chosen != null && _chosen.CanSettle)
                FoundAt(_chosen.ParcelX, _chosen.ParcelZ);
        }

        Vector3 ParcelTop(int px, int pz)
        {
            int x = px * ParcelGrid.Size + ParcelGrid.Size / 2, z = pz * ParcelGrid.Size + ParcelGrid.Size / 2;
            float y = Parcels != null ? Parcels.GroundAt(x, z) + 1 : World.Island.SeaLevel;
            if (World.Island != null && y < World.Island.SeaLevel) y = World.Island.SeaLevel;
            return new Vector3(x, y, z);
        }

        static GameObject Marker(string name, Color colour, float width, float height)
        {
            // The built-in cylinder mesh, without the collider a primitive would bring.
            var go = new GameObject(name);
            go.AddComponent<MeshFilter>().sharedMesh = Resources.GetBuiltinResource<Mesh>("Cylinder.fbx");
            go.AddComponent<MeshRenderer>();
            go.transform.localScale = new Vector3(width, height * 0.5f, width);
            Shader unlit = Shader.Find("Unlit/Color") ?? Shader.Find("Standard");
            go.GetComponent<Renderer>().sharedMaterial = new UnityEngine.Material(unlit) { color = colour };
            return go;
        }

        /// <summary>A ring round the chosen site at the distance people will go for materials at first.</summary>
        void DrawReach(Vector3 centre)
        {
            if (_reachRing == null)
            {
                var go = new GameObject("Reach of the first fire");
                _reachRing = go.AddComponent<LineRenderer>();
                _reachRing.loop = true;
                _reachRing.widthMultiplier = 3f;
                _reachRing.positionCount = 96;
                Shader sprite = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
                _reachRing.sharedMaterial = new UnityEngine.Material(sprite) { color = new Color(1f, 0.8f, 0.3f, 0.8f) };
                _reachRing.startColor = _reachRing.endColor = new Color(1f, 0.8f, 0.3f, 0.8f);
            }
            float radius = MaterialTable.FromContent(_content, _biomes).DepositRangeVoxels;
            for (int i = 0; i < _reachRing.positionCount; i++)
            {
                float a = i / (float)_reachRing.positionCount * Mathf.PI * 2f;
                int x = Mathf.Clamp(Mathf.RoundToInt(centre.x + Mathf.Cos(a) * radius), 0, ChunkStore.SizeX - 1);
                int z = Mathf.Clamp(Mathf.RoundToInt(centre.z + Mathf.Sin(a) * radius), 0, ChunkStore.SizeZ - 1);
                float y = Mathf.Max(Parcels.GroundAt(x, z) + 1.5f, World.Island.SeaLevel + 0.5f);
                _reachRing.SetPosition(i, new Vector3(x, y, z));
            }
        }

        void ClearMarkers()
        {
            if (_hoverMarker != null) Destroy(_hoverMarker);
            if (_chosenMarker != null) Destroy(_chosenMarker);
            if (_reachRing != null) Destroy(_reachRing.gameObject);
            _hover = _chosen = null;
        }

        /// <summary>Back to the map chooser: the scene again from the top, with the last map and seed remembered.</summary>
        void ChooseAnotherWorld()
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
            if (Time.frameCount > 10 && dt > _worstFrame) _worstFrame = dt;

            if (Phase == SetupPhase.ChoosingSite) UpdateSitePicking();
            RunSim(dt);
        }

        /// <summary>
        /// Advances the simulation in whole ticks, then tells the renderer
        /// which chunks the people changed. The sim never touches Unity and
        /// Unity never touches the voxels: it reads the delta log, which is
        /// there for exactly this (S04).
        ///
        /// How many ticks is the pacer's business and nobody else's — the
        /// world does not know what speed it is being watched at.
        /// </summary>
        void RunSim(float dt)
        {
            if (World == null || Town == null || Pacer == null) return;

            Pacer.Advance(dt);
            int ticks = Pacer.Take(FrameBudget());
            if (ticks > 0) RunTicks(ticks);
        }

        /// <summary>
        /// Ticks this frame can afford. A fast speed must not outrun the
        /// mesher: the queue is the honest signal that the picture is falling
        /// behind the world, and the ticks it cannot afford stay owed rather
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
        /// Runs whole ticks and hands the renderer what changed. Public so a
        /// tool can wind the world on (S29's style plate) without pretending
        /// to be a frame.
        /// </summary>
        public void RunTicks(int ticks)
        {
            for (int i = 0; i < ticks; i++) World.Tick();
            ShowChanges();
        }

        /// <summary>
        /// Hands the renderer every voxel changed since it last looked, from
        /// the delta log — the simulation's ticks and the god's hand alike, so
        /// a stroke while paused is drawn at once and never drawn twice.
        /// </summary>
        public void ShowChanges()
        {
            if (World == null || View == null) return;
            IReadOnlyList<VoxelDelta> log = World.Voxels.Log.All();
            for (; _deltaCursor < log.Count; _deltaCursor++)
                View.MarkDirty(ChunkStore.PositionOf(log[_deltaCursor].ChunkIndex, log[_deltaCursor].VoxelIndex));
        }

        void OnGUI()
        {
            if (Phase == SetupPhase.ChoosingMap) { MapChooser(); return; }
            if (Phase == SetupPhase.ChoosingSite) { SiteChooser(); return; }
            if (!showStats || View == null) return;

            string text =
                (1f / _smoothedFrame).ToString("0") + " fps  (" + (_smoothedFrame * 1000f).ToString("0.0") + " ms)\n" +
                "chunks drawn " + View.ChunksDrawn + ", quads " + View.QuadsDrawn.ToString("N0") + "\n" +
                "meshing: " + View.ChunksInFlight + " in flight, " + View.ChunksQueued + " queued\n" +
                "mesher main-thread cost " + View.LastFrameMs.ToString("0.00") + " ms, worst "
                + View.WorstFrameMs.ToString("0.00") + " ms\n" +
                "island generated in " + _loadSeconds.ToString("0.00") + "s, seed " + seed;

            if (Town != null)
            {
                int standing = 0, under = 0;
                foreach (Project project in Town.Projects) { if (project.Complete) standing++; else under++; }
                text += "\n\n" + Pacer.Label + (Pacer.Behind > 2.0 ? "  (the picture is behind the world)" : "")
                      + "   year " + World.Clock.Year + " day " + World.Clock.DayOfYear
                      + "   " + Town.People.Count + " people, " + Town.ShelterCapacity + " sleeping places"
                      + "\nhouses: " + standing + " standing, " + under + " going up"
                      + "   intents " + Town.Intents.Intents.Count;
                int plots = 0;
                foreach (Farm farm in Town.Farms) plots += farm.Plots.Count;
                if (World.Settlements.Count > 1)
                {
                    text += "\ntowns " + World.Settlements.Count + ":";
                    for (int i = 0; i < World.Settlements.Count; i++)
                    {
                        Settlement t = World.Settlements[i];
                        text += "  " + (i == 0 ? "first" : t.Id.ToString().Replace("settlement.", "")) + " " + t.People.Count
                              + " (" + Towns.Homeless(t) + " roofless)";
                    }
                }
                text += "\nfood " + Town.Food.ToString("0") + "   farms " + Town.Farms.Count + " (" + plots + " plots)   stores keep "
                      + Stores.Capacity(Town).ToString("0") + "   heaps " + Town.Piles.Count + "   rotted " + Town.FoodSpoiled.ToString("0");

                // S2Z: what the first fire has become, and the last time the town gathered there.
                Commons commons = Town.Commons;
                if (commons != null)
                {
                    if (_commonsRules == null) _commonsRules = CommonsRules.FromContent(_content, DriveRules.FromContent(_content).Needs);
                    if (_commonsRules != null && _commonsRules.Stages.Count > 0)
                    {
                        text += "\ncommons: " + commons.PlaceName(_commonsRules) + ", " + commons.Seats.Count + " seats"
                              + (commons.Underway >= 0 ? ", making " + _commonsRules.Stages[commons.Underway].Name : "")
                              + (commons.Last != null ? "   last gathering: " + commons.Last.Kind.Doing + ", day " + (commons.Last.Day % World.Clock.DaysPerYear)
                                 + ", " + commons.Last.Attending + " came" : "");
                    }
                }
            }

            if (GetComponent<SimSpeed>() != null) text += "\n" + SimSpeed.Keys + (GetComponent<CutawayView>() != null ? "   " + CutawayView.Keys : "") + (GetComponent<BorderView>() != null ? "   " + BorderView.Keys : "");

            TerrainEditor editor = GetComponent<TerrainEditor>();
            if (editor != null) text += "\n" + TerrainEditor.Keys + "\n" + editor.Status + "   strokes " + editor.Strokes;

            GUI.Label(new Rect(12, 10, 900, 240), text);
        }

        // ── the screens before the world runs ───────────────────────────────

        GUIStyle _title, _body, _small, _button, _box;
        Vector2 _mapScroll, _siteScroll;

        void Styles()
        {
            if (_title != null) return;
            _title = new GUIStyle(GUI.skin.label) { fontSize = 30, fontStyle = FontStyle.Bold, wordWrap = true };
            _body = new GUIStyle(GUI.skin.label) { fontSize = 15, wordWrap = true };
            _small = new GUIStyle(GUI.skin.label) { fontSize = 13, wordWrap = true };
            _button = new GUIStyle(GUI.skin.button) { fontSize = 15, alignment = TextAnchor.UpperLeft, wordWrap = true, padding = new RectOffset(12, 12, 10, 10) };
            _box = new GUIStyle(GUI.skin.box);
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
            GUILayout.Label("Choose a world. Next you choose where its first fire is lit; after that you may act on the world, never on its people.", _body);
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
            plantDeposits = GUILayout.Toggle(plantDeposits, " woods, reeds and rock to use up", GUILayout.Height(26));
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

        /// <summary>Where the first fire goes: what the ground under the pointer and the chosen site offer.</summary>
        void SiteChooser()
        {
            Styles();
            float w = 340f, h = Screen.height - 30f;
            _panel = new Rect(Screen.width - w - 15f, 15f, w, h);
            GUI.Box(_panel, GUIContent.none, _box);
            GUILayout.BeginArea(new Rect(_panel.x + 14, _panel.y + 12, w - 28, h - 24));

            GUILayout.Label("Where is the first fire lit?", new GUIStyle(_title) { fontSize = 22 });
            GUILayout.Label((Map != null ? Map.Title + ", seed " + seed : "") + ". Twenty people begin where you click; the ring is the land they build and eat from."
                            + "\nRight-drag turn, WASD move, scroll zoom.", _small);
            GUILayout.Space(6);

            _siteScroll = GUILayout.BeginScrollView(_siteScroll, GUILayout.ExpandHeight(true));
            if (_hover != null && (_chosen == null || _hover.ParcelX != _chosen.ParcelX || _hover.ParcelZ != _chosen.ParcelZ))
            {
                GUILayout.Label("Under the pointer", new GUIStyle(_body) { fontStyle = FontStyle.Bold });
                Report(_hover, false);
                GUILayout.Space(6);
            }
            if (_chosen != null)
            {
                GUILayout.Label("Chosen", new GUIStyle(_body) { fontStyle = FontStyle.Bold });
                Report(_chosen, true);
            }
            GUILayout.EndScrollView();

            GUILayout.Space(6);
            GUI.enabled = _chosen != null && _chosen.CanSettle;
            if (GUILayout.Button("Light the fire here  (Enter)", GUILayout.Height(40))) FoundAt(_chosen.ParcelX, _chosen.ParcelZ);
            GUI.enabled = SuggestedX >= 0;
            if (GUILayout.Button("Go to the suggested site", GUILayout.Height(28))) Choose(SuggestedX, SuggestedZ, true);
            GUI.enabled = true;
            if (GUILayout.Button("Choose another world", GUILayout.Height(28))) ChooseAnotherWorld();
            GUILayout.EndArea();
        }

        void Report(SiteReport r, bool full)
        {
            if (!r.CanSettle) { GUILayout.Label("Cannot settle here: " + r.Why + ".", _small); return; }
            var text = new System.Text.StringBuilder();
            text.Append(r.Biome.Length > 0 ? char.ToUpperInvariant(r.Biome[0]) + r.Biome.Substring(1) : "Land")
                .Append(", ").Append(r.Elevation).Append(" voxels above the sea")
                .Append("\nWater ").Append(r.WaterParcels < 1.0 ? "right here" : (r.WaterParcels * ParcelGrid.Size * 0.5).ToString("0") + " m away")
                .Append(", ground ").Append(r.Slope < 1.0 ? "flat" : r.Slope < 3.0 ? "gently sloping" : "steep")
                .Append("\nThe wild land feeds about ").Append(r.ForagePerDay.ToString("0")).Append(" people before they must farm")
                .Append("\nRoom nearby: ").Append(r.RoomNearby).Append(" flat dry parcels");
            if (full || r.Materials.Count > 0)
            {
                text.Append("\nTo build with: ");
                if (r.Materials.Count == 0) text.Append("nothing in reach");
                for (int i = 0; i < r.Materials.Count; i++)
                {
                    if (i > 0) text.Append(", ");
                    text.Append(r.Materials[i].Key).Append(' ').Append((r.Materials[i].Value * 100).ToString("0")).Append('%');
                }
            }
            GUILayout.Label(text.ToString(), _small);
        }
    }
}
