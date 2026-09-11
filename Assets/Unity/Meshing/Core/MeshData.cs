using System.Collections.Generic;

namespace Godless.Meshing
{
    /// <summary>
    /// A mesh as plain arrays, with no Unity types in it. The mesher fills
    /// one of these on a worker thread; the Unity layer copies it into a
    /// UnityEngine.Mesh on the main thread. Keeping the two apart is what lets
    /// the expensive half run anywhere and be tested with dotnet.
    /// </summary>
    public sealed class MeshData
    {
        public readonly List<float> Positions = new List<float>();  // x, y, z per vertex
        public readonly List<float> Normals = new List<float>();    // x, y, z per vertex
        public readonly List<byte> Colors = new List<byte>();       // r, g, b, a per vertex
        public readonly List<int> Indices = new List<int>();         // triangles

        public int VertexCount { get { return Positions.Count / 3; } }
        public int TriangleCount { get { return Indices.Count / 3; } }
        public int QuadCount { get { return Indices.Count / 6; } }
        public bool IsEmpty { get { return Indices.Count == 0; } }

        public void Clear()
        {
            Positions.Clear();
            Normals.Clear();
            Colors.Clear();
            Indices.Clear();
        }

        /// <summary>
        /// A digest over every vertex and index, so a test can prove two
        /// builds of the same chunk produced the same mesh bit for bit.
        /// </summary>
        public ulong Digest()
        {
            var d = new Godless.Sim.Core.Digest();
            for (int i = 0; i < Positions.Count; i++) d.Add(System.BitConverter.SingleToInt32Bits(Positions[i]));
            for (int i = 0; i < Normals.Count; i++) d.Add(System.BitConverter.SingleToInt32Bits(Normals[i]));
            for (int i = 0; i < Colors.Count; i++) d.Add(Colors[i]);
            for (int i = 0; i < Indices.Count; i++) d.Add(Indices[i]);
            return d.Value;
        }
    }
}
