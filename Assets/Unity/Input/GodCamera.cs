using Godless.Sim.Voxels;
using Godless.Sim.World;
using UnityEngine;
using UnityEngine.InputSystem;

namespace Godless.Unity
{
    /// <summary>
    /// The god's view: orbit a point on the island. S07.
    ///
    ///   right-drag     orbit            WASD / arrows   pan
    ///   middle-drag    pan              Q / E           turn
    ///   scroll         zoom
    ///
    /// Uses the Input System package, because the project's active input
    /// handler is the new system only and the legacy Input class throws.
    /// </summary>
    public sealed class GodCamera : MonoBehaviour
    {
        [SerializeField] Vector3 pivot = new Vector3(ChunkStore.SizeX * 0.5f, IslandMap.DefaultSeaLevel, ChunkStore.SizeZ * 0.5f);
        [SerializeField] float yaw = 0f;
        [SerializeField, Range(10f, 89f)] float pitch = 48f;
        [SerializeField] float distance = 380f;

        [SerializeField] float minDistance = 20f;
        [SerializeField] float maxDistance = 900f;
        [SerializeField] float orbitDegreesPerPixel = 0.25f;
        [SerializeField] float panScreensPerSecond = 0.9f;
        [SerializeField] float turnDegreesPerSecond = 90f;

        void LateUpdate()
        {
            Mouse mouse = Mouse.current;
            Keyboard keys = Keyboard.current;
            float dt = Time.unscaledDeltaTime;

            if (mouse != null)
            {
                Vector2 delta = mouse.delta.ReadValue();

                if (mouse.rightButton.isPressed)
                {
                    yaw += delta.x * orbitDegreesPerPixel;
                    pitch = Mathf.Clamp(pitch - delta.y * orbitDegreesPerPixel, 10f, 89f);
                }

                if (mouse.middleButton.isPressed)
                {
                    // Drag the ground under the cursor: scale by distance so
                    // it tracks at any zoom.
                    float perPixel = distance * 0.0016f;
                    Pan(-delta.x * perPixel, -delta.y * perPixel);
                }

                float scroll = mouse.scroll.ReadValue().y;
                if (scroll != 0f)
                    distance = Mathf.Clamp(distance * Mathf.Exp(-scroll * 0.0015f), minDistance, maxDistance);
            }

            if (keys != null)
            {
                float x = 0f, z = 0f;
                if (keys.aKey.isPressed || keys.leftArrowKey.isPressed) x -= 1f;
                if (keys.dKey.isPressed || keys.rightArrowKey.isPressed) x += 1f;
                if (keys.sKey.isPressed || keys.downArrowKey.isPressed) z -= 1f;
                if (keys.wKey.isPressed || keys.upArrowKey.isPressed) z += 1f;
                float speed = distance * panScreensPerSecond * dt;
                Pan(x * speed, z * speed);

                if (keys.qKey.isPressed) yaw -= turnDegreesPerSecond * dt;
                if (keys.eKey.isPressed) yaw += turnDegreesPerSecond * dt;
            }

            Quaternion rotation = Quaternion.Euler(pitch, yaw, 0f);
            transform.SetPositionAndRotation(pivot - rotation * Vector3.forward * distance, rotation);
        }

        void Pan(float right, float forward)
        {
            Quaternion flat = Quaternion.Euler(0f, yaw, 0f);
            pivot += flat * new Vector3(right, 0f, forward);
            pivot.x = Mathf.Clamp(pivot.x, 0f, ChunkStore.SizeX);
            pivot.z = Mathf.Clamp(pivot.z, 0f, ChunkStore.SizeZ);
        }
    }
}
