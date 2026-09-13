using System.Collections.Generic;
using System.IO;
using Godless.Sim.Build;
using Godless.Sim.Settlements;
using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;

namespace Godless.Unity
{
    /// <summary>
    /// The style plate. S29.
    ///
    /// A fixed camera on a fixed seed, taken at fixed years and committed with
    /// the build. Nothing here tests anything: it is the instrument that
    /// catches "the grammar change made every roof flat" in one glance, which
    /// no assertion will, because nobody thinks to assert it until after it
    /// has happened.
    ///
    /// S1G answers whether two things differ as a number; this answers what
    /// they look like. They are different instruments and the project needs
    /// both — one to pass a gate with, one to notice a regression with.
    /// </summary>
    [RequireComponent(typeof(WorldBootstrap))]
    public sealed class StyleHarness : MonoBehaviour
    {
        [Tooltip("Run the plate on play, then leave play mode. Off for ordinary play.")]
        [SerializeField] bool capture;

        [Tooltip("Years to photograph the settlement at.")]
        [SerializeField] int[] years = { 1, 2, 4 };

        [SerializeField] int width = 960;
        [SerializeField] int height = 540;

        [Tooltip("Under the project folder. Committed, so a diff of the plate is a diff of the style.")]
        [SerializeField] string folder = "Screenshots";

        WorldBootstrap _boot;
        int _next;
        bool _done;
        readonly List<string> _written = new List<string>();

        /// <summary>Files written this run, in order.</summary>
        public IReadOnlyList<string> Written { get { return _written; } }

        public bool Finished { get { return _done; } }

        void Awake() { _boot = GetComponent<WorldBootstrap>(); }

        void LateUpdate()
        {
            if (!capture || _done || _boot.World == null || _boot.Town == null) return;

            // Run the sim as fast as it will go to the next year wanted, then
            // let the mesher catch up before the shutter.
            long target = _boot.World.Clock.TicksInYears(years[_next]);
            if (_boot.World.Clock.Tick < target)
            {
                // The same tick path the player's speed uses; a plate is
                // allowed to be in a hurry, but not to be a different world.
                long owed = target - _boot.World.Clock.Tick;
                _boot.RunTicks((int)System.Math.Min(owed, 4000));
                MarkEverythingDirty();
                return;
            }
            if (_boot.View.ChunksQueued > 0 || _boot.View.ChunksInFlight > 0) return;

            Shoot("village-y" + years[_next], Village());
            Shoot("island-y" + years[_next], Island());

            if (++_next < years.Length) return;
            _done = true;
            Debug.Log("Godless: style plate written — " + string.Join(", ", _written));
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        /// <summary>Everything the fast-forward changed, in one go: the plate is not a frame-rate test.</summary>
        void MarkEverythingDirty()
        {
            Godless.Sim.Deltas.DeltaLog log = _boot.World.Voxels.Log;
            IReadOnlyList<Godless.Sim.Deltas.VoxelDelta> all = log.All();
            for (int i = 0; i < all.Count; i++)
                _boot.View.MarkDirty(ChunkStore.PositionOf(all[i].ChunkIndex, all[i].VoxelIndex));
        }

        /// <summary>Over the settlement's shoulder, close enough to read a roof.</summary>
        (Vector3, Vector3) Village()
        {
            Settlement town = _boot.Town;
            var centre = new Vector3(town.Hearth.X, town.Hearth.Y, town.Hearth.Z);
            foreach (Project project in town.Projects)
            {
                if (!project.Complete) continue;
                centre = new Vector3(project.Site.ParcelX * ParcelGrid.Size, project.Site.Ground, project.Site.ParcelZ * ParcelGrid.Size);
                break;
            }
            return (centre + new Vector3(-26f, 22f, -26f), centre + new Vector3(0f, 2f, 0f));
        }

        /// <summary>And the whole island, for the coastline and the rivers.</summary>
        (Vector3, Vector3) Island()
        {
            var centre = new Vector3(ChunkStore.SizeX * 0.5f,
                                     _boot.World.Island != null ? _boot.World.Island.SeaLevel : IslandMap.DefaultSeaLevel,
                                     ChunkStore.SizeZ * 0.5f);
            return (centre + new Vector3(0f, 300f, -360f), centre);
        }

        void Shoot(string name, (Vector3 from, Vector3 at) view)
        {
            Camera cam = Camera.main;
            if (cam == null) return;

            Vector3 wasPosition = cam.transform.position;
            Quaternion wasRotation = cam.transform.rotation;
            RenderTexture wasTarget = cam.targetTexture;

            var texture = new RenderTexture(width, height, 24);
            var image = new Texture2D(width, height, TextureFormat.RGB24, false);
            try
            {
                cam.transform.position = view.from;
                cam.transform.LookAt(view.at);
                cam.targetTexture = texture;
                cam.Render();

                RenderTexture.active = texture;
                image.ReadPixels(new Rect(0, 0, width, height), 0, 0);
                image.Apply();

                string directory = Path.Combine(Directory.GetParent(Application.dataPath).FullName, folder);
                Directory.CreateDirectory(directory);
                string path = Path.Combine(directory, name + ".png");
                File.WriteAllBytes(path, image.EncodeToPNG());
                _written.Add(name + ".png");
            }
            finally
            {
                RenderTexture.active = null;
                cam.targetTexture = wasTarget;
                cam.transform.position = wasPosition;
                cam.transform.rotation = wasRotation;
                Destroy(texture);
                Destroy(image);
            }
        }
    }
}
