
using GlmSharp;
using Silk.NET.GLFW;

namespace IntelliCraft;


public static partial class Program
{

    private static float cameraYaw = 0.9f;
    private static float cameraPitch = 0.8f;
    private static float cameraDistance = 6f;
    private static vec3 cameraTarget = vec3.Zero;

    private const float OrbitSensitivity = 0.005f;
    private const float ZoomSensitivity = 0.5f;
    private const float MinDistance = 0.5f;
    private const float MaxDistance = 100f;
    private const float MinPitch = 0.05f;
    private const float MaxPitch = (float)(System.Math.PI - 0.05);

    private static bool isDragging = false;
    private static double lastCursorX;
    private static double lastCursorY;


    private static vec3 CameraPosition()
    {
        float x = cameraDistance * (float)System.Math.Sin(cameraPitch) * (float)System.Math.Cos(cameraYaw);
        float y = cameraDistance * (float)System.Math.Sin(cameraPitch) * (float)System.Math.Sin(cameraYaw);
        float z = cameraDistance * (float)System.Math.Cos(cameraPitch);
        return cameraTarget + new vec3(x, y, z);
    }

    private static mat4 CameraViewMatrix()
    {
        return mat4.LookAt(CameraPosition(), cameraTarget, vec3.UnitZ);
    }


    private static unsafe void InitOrbitalCamera()
    {
        Glfw.SetScrollCallback(Window, OnScroll);
        Glfw.SetMouseButtonCallback(Window, OnMouseButton);
        Glfw.SetCursorPosCallback(Window, OnCursorPos);

        if (Glfw.RawMouseMotionSupported())
        {
            Glfw.SetInputMode(Window, CursorStateAttribute.RawMouseMotion, true);
        }
    }


    private static unsafe void OnScroll(WindowHandle* window, double offsetX, double offsetY)
    {
        cameraDistance -= (float)offsetY * ZoomSensitivity;
        cameraDistance = System.Math.Clamp(cameraDistance, MinDistance, MaxDistance);
    }

    private static unsafe void OnMouseButton(WindowHandle* window, MouseButton button, InputAction action, KeyModifiers mods)
    {
        if (button != MouseButton.Left) return;

        if (action == InputAction.Press)
        {
            isDragging = true;
            Glfw.GetCursorPos(window, out lastCursorX, out lastCursorY);
        }
        else if (action == InputAction.Release)
        {
            isDragging = false;
        }
    }

    private static unsafe void OnCursorPos(WindowHandle* window, double x, double y)
    {
        if (!isDragging) return;

        double dx = x - lastCursorX;
        double dy = y - lastCursorY;
        lastCursorX = x;
        lastCursorY = y;

        cameraYaw -= (float)dx * OrbitSensitivity;
        cameraPitch -= (float)dy * OrbitSensitivity;
        cameraPitch = System.Math.Clamp(cameraPitch, MinPitch, MaxPitch);
    }

}
