
using GlmSharp;
using GlmSharp.Swizzle;
using MYT.Kit.Vulkan;
using Silk.NET.Maths;
using Silk.NET.Vulkan;

namespace IntelliCraft;


public static partial class Program
{

    private static Mesh3D cubeMesh = null!;
    private static Mesh3D coneMesh = null!;
    private static Mesh3D sphereMesh = null!;
    private static Mesh3D torusMesh = null!;
    private static Mesh3D monkeyMesh = null!;
    private static Mesh3D mapMesh = null!;
    private static PhongMaterial material = null!;
    private static MaterialLoader materialLoader = null!;
    private static FixedBody mapBody = null!;
    private static RigidBody boxBody0 = null!;
    private static RigidBody boxBody1 = null!;
    private static RigidBody boxBody2 = null!;
    private static RigidBody boxBody3 = null!;
    private static RigidBody boxBody4 = null!;
    private static SoftBody softBody = null!;
    private static PhysicsSystem physicsSystem = null!;

    private static void Init()
    {

        InitGLFW();

        CreateInstance();
        CreateSurface();
        CreateDevice();
        CreateCommandPool();
        CreateSwapchain();
        CreateSync();

        cubeMesh = Mesh3D.Loader.Load("../../assets/3d/cube.ply");
        cubeMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);

        coneMesh = Mesh3D.Loader.Load("../../assets/3d/cone.ply");
        coneMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);

        sphereMesh = Mesh3D.Loader.Load("../../assets/3d/sphere.ply");
        sphereMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);

        torusMesh = Mesh3D.Loader.Load("../../assets/3d/torus.ply");
        torusMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);

        monkeyMesh = Mesh3D.Loader.Load("../../assets/3d/monkey.ply");
        monkeyMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);

        mapMesh = Mesh3D.Loader.Load("../../assets/3d/testMap.ply");
        mapMesh.UploadToGPU(vk, device, physicalDevice, commandPool, graphicsQueue);


        materialLoader = new(vk, device, physicalDevice, FramesInFlight);
        material = materialLoader.Load("../../assets/materials/phong");


        mapBody = new FixedBody(mapMesh);

        boxBody0 = new RigidBody(cubeMesh, mass: 1.0)
        {
            Position = new dvec3(0, 0, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        boxBody1 = new RigidBody(coneMesh, mass: 1.0)
        {
            Position = new dvec3(3, 0, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        boxBody2 = new RigidBody(sphereMesh, mass: 1.0)
        {
            Position = new dvec3(0, 3, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        boxBody3 = new RigidBody(torusMesh, mass: 1.0)
        {
            Position = new dvec3(3, 3, 5),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };
        boxBody4 = new RigidBody(monkeyMesh, mass: 1.0)
        {
            Position = new dvec3(0, 0, 8),
            Orientation = dquat.FromAxisAngle(Math.PI / 6, new dvec3(1, 1, 0).Normalized),
            Restitution = 0.3
        };

        softBody = SoftBody.CreateLatticeBox
        (
            center: new dvec3(1, 0, 20),
            size: new dvec3(2, 2, 2),
            resolution: new ivec3(6, 6, 6),
            totalMass: 1.0,
            stiffness: 80.0,
            damping: 0.005
        );

        physicsSystem = new(vk, device, physicalDevice, commandPool, graphicsQueue, "../../assets/shaders/physics");
        physicsSystem.Add(mapBody);
        physicsSystem.Add(boxBody0);
        physicsSystem.Add(boxBody1);
        physicsSystem.Add(boxBody2);
        physicsSystem.Add(boxBody3);
        physicsSystem.Add(boxBody4);
        physicsSystem.Add(softBody);

        InitRigidBodyPipeline();
        RegisterRigidBodyForRendering(boxBody0);
        RegisterRigidBodyForRendering(boxBody1);
        RegisterRigidBodyForRendering(boxBody2);
        RegisterRigidBodyForRendering(boxBody3);
        RegisterRigidBodyForRendering(boxBody4);
        RegisterSoftBodyForRendering(softBody);

        InitOrbitalCamera();

    }



    private static void GameLoop()
    {

        while(ShouldClose())
        {
            Glfw.PollEvents();

            try
            {
                physicsSystem.Update(0.01f);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Physics update failed: {ex.GetType().Name}");
                Console.WriteLine($"Message: {ex.Message}");
                Console.WriteLine($"Stack trace:\n{ex.StackTrace}");
                throw;
            }

            DrawFrame();

        }

    }

    private static unsafe bool ShouldClose()
    {
        return !Glfw.WindowShouldClose(Window);
    }



    private static void Terminate()
    {

        vk.DeviceWaitIdle(device);

        physicsSystem.Dispose();
        DestroyRigidBodyPipeline();
        cubeMesh.Dispose();
        sphereMesh.Dispose();
        torusMesh.Dispose();
        monkeyMesh.Dispose();
        mapMesh.Dispose();
        materialLoader.Dispose();

        DestroySync();
        DestroySwapchain();
        DestroyCommandPool();
        DestroyDevice();
        DestroySurface();
        DestroyInstance();
        TerminateGLFW();

    }

}