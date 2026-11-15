using NeuralGraph;
using Silk.NET.Vulkan;
using Silk.NET.Vulkan.Extensions.KHR;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace ArcadeLab;

/// <summary>GLFW window with a composed framebuffer uploaded through Vulkan; render-pass rectangle fallback. No OpenGL dependency.</summary>
public sealed unsafe partial class Viewer : IDisposable
{
    private nint window;
    private VulkanDevice context = null!;
    private KhrSurface surfaceApi = null!;
    private KhrSwapchain swapchainApi = null!;
    private SurfaceKHR surface;
    private SwapchainKHR swapchain;
    private ImageView[] views = [];
    private Framebuffer[] framebuffers = [];
    private RenderPass renderPass;
    private CommandBuffer command;
    private Silk.NET.Vulkan.Semaphore acquired;
    private Silk.NET.Vulkan.Semaphore[] rendered = [];
    private Fence fence;
    private Extent2D extent;
    private bool initialized;
    private bool frameCaptured;
    private readonly uint[] mazeMap = new uint[512 * 512];
    private GpuBuffer? framePixels;
    private Image[] swapchainImages = [];
    private bool blueFirst;
    private Vk Vk => context.Api;
    public Viewer()
    {
        try
        {
            if (Glfw.Init() == 0) throw new InvalidOperationException("GLFW could not initialize. Use a desktop session or run train/evaluate headlessly.");
            initialized = true;
            Glfw.WindowHint(0x00022001, 0); // GLFW_CLIENT_API = GLFW_NO_API
            window = Glfw.CreateWindow(1600, 1000, "Neural Maze | Vulkan", 0, 0);
            if (window == 0) throw new InvalidOperationException("GLFW window creation failed.");
            Glfw.MaximizeWindow(window);
            byte** names = Glfw.GetRequiredInstanceExtensions(out uint count);
            if (names == null) throw new InvalidOperationException("GLFW Vulkan extensions unavailable.");
            var extensions = new string[count]; for (int i = 0; i < count; i++) extensions[i] = Marshal.PtrToStringUTF8((nint)names[i])!;
            context = new(extensions, presentation: true);
            if (!Vk.TryGetInstanceExtension(context.Instance, out surfaceApi) || !Vk.TryGetDeviceExtension(context.Instance, context.Device, out swapchainApi))
                throw new InvalidOperationException("Required Vulkan presentation extensions unavailable.");
            VulkanDevice.Check((Result)Glfw.CreateWindowSurface(context.Instance.Handle, window, 0, out ulong handle)); surface = new(handle);
            VulkanDevice.Check(surfaceApi.GetPhysicalDeviceSurfaceSupport(context.PhysicalDevice, context.QueueFamily, surface, out var present));
            if (!present) throw new NotSupportedException("Selected graphics queue cannot present to this surface.");
            command = context.AllocateCommand();
            var semaphoreInfo = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VulkanDevice.Check(Vk.CreateSemaphore(context.Device, in semaphoreInfo, null, out acquired));
            var fenceInfo = new FenceCreateInfo { SType = StructureType.FenceCreateInfo, Flags = FenceCreateFlags.SignaledBit };
            VulkanDevice.Check(Vk.CreateFence(context.Device, in fenceInfo, null, out fence));
            Recreate();
        }
        catch { Dispose(); throw; }
    }
    private void Recreate()
    {
        VulkanDevice.Check(Vk.DeviceWaitIdle(context.Device)); DestroySwapchain();
        Glfw.GetFramebufferSize(window, out int width, out int height);
        if (width <= 0 || height <= 0) return;
        VulkanDevice.Check(surfaceApi.GetPhysicalDeviceSurfaceCapabilities(context.PhysicalDevice, surface, out var caps));
        uint count = 0; VulkanDevice.Check(surfaceApi.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, surface, &count, null));
        var formats = new SurfaceFormatKHR[count]; fixed (SurfaceFormatKHR* p = formats) VulkanDevice.Check(surfaceApi.GetPhysicalDeviceSurfaceFormats(context.PhysicalDevice, surface, &count, p));
        if (count == 0) throw new NotSupportedException("No presentation formats.");
        var format = formats.FirstOrDefault(x => x.Format == Format.B8G8R8A8Unorm);
        if (format.Format == Format.Undefined) format = formats[0];
        if (format.Format == Format.Undefined) format.Format = Format.B8G8R8A8Unorm;
        extent = caps.CurrentExtent.Width != uint.MaxValue ? caps.CurrentExtent : new Extent2D(
            Math.Clamp((uint)width, caps.MinImageExtent.Width, caps.MaxImageExtent.Width), Math.Clamp((uint)height, caps.MinImageExtent.Height, caps.MaxImageExtent.Height));
        uint imageCount = caps.MinImageCount + 1; if (caps.MaxImageCount > 0) imageCount = Math.Min(imageCount, caps.MaxImageCount);
        var alpha = (caps.SupportedCompositeAlpha & CompositeAlphaFlagsKHR.OpaqueBitKhr) != 0 ? CompositeAlphaFlagsKHR.OpaqueBitKhr :
            (CompositeAlphaFlagsKHR)((uint)caps.SupportedCompositeAlpha & (0u - (uint)caps.SupportedCompositeAlpha));
        var info = new SwapchainCreateInfoKHR { SType = StructureType.SwapchainCreateInfoKhr, Surface = surface, MinImageCount = imageCount, ImageFormat = format.Format,
            ImageColorSpace = format.ColorSpace, ImageExtent = extent, ImageArrayLayers = 1, ImageUsage = ImageUsageFlags.ColorAttachmentBit | ((caps.SupportedUsageFlags & ImageUsageFlags.TransferDstBit) != 0 ? ImageUsageFlags.TransferDstBit : 0),
            ImageSharingMode = SharingMode.Exclusive, PreTransform = caps.CurrentTransform, CompositeAlpha = alpha, PresentMode = PresentModeKHR.FifoKhr, Clipped = true };
        VulkanDevice.Check(swapchainApi.CreateSwapchain(context.Device, in info, null, out swapchain));
        VulkanDevice.Check(swapchainApi.GetSwapchainImages(context.Device, swapchain, &count, null));
        var images = new Image[count]; fixed (Image* p = images) VulkanDevice.Check(swapchainApi.GetSwapchainImages(context.Device, swapchain, &count, p));
        swapchainImages = images;
        blueFirst = format.Format == Format.B8G8R8A8Unorm;
        if ((caps.SupportedUsageFlags & ImageUsageFlags.TransferDstBit) != 0 && (blueFirst || format.Format == Format.R8G8B8A8Unorm))
            framePixels = context.CreateBuffer((ulong)extent.Width * extent.Height * 4, BufferUsageFlags.TransferSrcBit, preferHostCached: true);
        var attachment = new AttachmentDescription { Format = format.Format, Samples = SampleCountFlags.Count1Bit, LoadOp = AttachmentLoadOp.Clear, StoreOp = AttachmentStoreOp.Store,
            StencilLoadOp = AttachmentLoadOp.DontCare, StencilStoreOp = AttachmentStoreOp.DontCare, InitialLayout = ImageLayout.Undefined, FinalLayout = ImageLayout.PresentSrcKhr };
        var reference = new AttachmentReference { Attachment = 0, Layout = ImageLayout.ColorAttachmentOptimal };
        var subpass = new SubpassDescription { PipelineBindPoint = PipelineBindPoint.Graphics, ColorAttachmentCount = 1, PColorAttachments = &reference };
        var dependency = new SubpassDependency { SrcSubpass = Vk.SubpassExternal, DstSubpass = 0, SrcStageMask = PipelineStageFlags.ColorAttachmentOutputBit,
            DstStageMask = PipelineStageFlags.ColorAttachmentOutputBit, DstAccessMask = AccessFlags.ColorAttachmentWriteBit };
        var passInfo = new RenderPassCreateInfo { SType = StructureType.RenderPassCreateInfo, AttachmentCount = 1, PAttachments = &attachment,
            SubpassCount = 1, PSubpasses = &subpass, DependencyCount = 1, PDependencies = &dependency };
        VulkanDevice.Check(Vk.CreateRenderPass(context.Device, in passInfo, null, out renderPass));
        views = new ImageView[count]; framebuffers = new Framebuffer[count]; rendered = new Silk.NET.Vulkan.Semaphore[count];
        for (int i = 0; i < count; i++)
        {
            var viewInfo = new ImageViewCreateInfo { SType = StructureType.ImageViewCreateInfo, Image = images[i], ViewType = ImageViewType.Type2D, Format = format.Format,
                SubresourceRange = new ImageSubresourceRange(ImageAspectFlags.ColorBit, 0, 1, 0, 1) };
            VulkanDevice.Check(Vk.CreateImageView(context.Device, in viewInfo, null, out views[i])); var view = views[i];
            var fbInfo = new FramebufferCreateInfo { SType = StructureType.FramebufferCreateInfo, RenderPass = renderPass, AttachmentCount = 1, PAttachments = &view, Width = extent.Width, Height = extent.Height, Layers = 1 };
            VulkanDevice.Check(Vk.CreateFramebuffer(context.Device, in fbInfo, null, out framebuffers[i]));
            var sem = new SemaphoreCreateInfo { SType = StructureType.SemaphoreCreateInfo };
            VulkanDevice.Check(Vk.CreateSemaphore(context.Device, in sem, null, out rendered[i]));
        }
        CreateGraphRenderer(format.Format);
    }
    public void Play(Agent agent, GameKind kind, int maxFrames = 0, bool mute = false)
    {
        using var audio = new AudioPlayback(mute);
        var env = new ArcadeGame(kind, 12345); var sensors = new Sensors(); var obs = new float[Sensors.ObservationSize];
        sensors.Reset(env); sensors.Observe(env, obs);
        var timer = Stopwatch.StartNew(); double nextTick = 0; bool manual = false, paused = false, oldTab = false, oldSpace = false;
        int frames = 0; float reward = 0; int action = 0;
        Console.WriteLine("Viewer: Tab AI/manual, arrows move, Space pause, R restart, Escape quit. Left: current pixels. Right: previous pixels; bottom: FFT + waveform.");
        while (Glfw.WindowShouldClose(window) == 0 && Glfw.GetKey(window, 256) == 0 && (maxFrames <= 0 || frames < maxFrames))
        {
            Glfw.PollEvents(); bool tab = Glfw.GetKey(window, 258) == 1, space = Glfw.GetKey(window, 32) == 1;
            if (tab && !oldTab) manual = !manual; if (space && !oldSpace) paused = !paused; oldTab = tab; oldSpace = space;
            if (Glfw.GetKey(window, 82) == 1 || env.Done) { env.Reset(); sensors.Reset(env); sensors.Observe(env, obs); reward = 0; }
            if (timer.Elapsed.TotalSeconds >= nextTick && !paused)
            {
                nextTick = timer.Elapsed.TotalSeconds + .05;
                action = manual ? Glfw.GetKey(window, 263) == 1 ? 1 : Glfw.GetKey(window, 262) == 1 ? 2 : 0 : agent.Act(obs);
                reward += env.Step(Sensors.Control(action)); sensors.Observe(env, obs); audio.Submit(env.Audio);
                Glfw.SetWindowTitle(window, $"{kind} | {(manual ? "MANUAL" : "NEURAL AI")} | score {env.Score} reward {reward:F2} | {agent.Backend.DeviceName} | Tab: control  Space: pause  R: reset  Esc: exit");
            }
            Draw(env, obs, action); frames++;
            Thread.Sleep(1);
        }
    }
    public void PlayRealtime(RealtimeSession session, int maxFrames, bool mute, FrameStatistics statistics, Action? autosave = null, Func<bool>? stopping = null)
    {
        using var audio = new AudioPlayback(mute);
        var timer = Stopwatch.StartNew(); double nextFrame = 0; long startFrame = session.Frames;
        bool manual = false, paused = false, oldTab = false, oldSpace = false, oldReset = false;
        Console.WriteLine("Realtime viewer: Tab AI/manual; arrows move; Space pause learning; R reset game; Escape quit and save. Right: sampled node graph (green/blue nonnegative rates, cyan/red signed weights, dashed skips); bars: learned pleasure (green), learned pain (red), primitive pain (amber), primitive catch reward (blue).");
        while (Glfw.WindowShouldClose(window) == 0 && Glfw.GetKey(window, 256) == 0 && stopping?.Invoke() != true && (maxFrames <= 0 || session.Frames - startFrame < maxFrames))
        {
            Glfw.PollEvents(); bool tab = Glfw.GetKey(window, 258) == 1, space = Glfw.GetKey(window, 32) == 1, reset = Glfw.GetKey(window, 82) == 1;
            if (tab && !oldTab) manual = !manual; if (space && !oldSpace) paused = !paused;
            if (reset && !oldReset) session.ResetEpisode(); oldTab = tab; oldSpace = space; oldReset = reset;
            Glfw.GetFramebufferSize(window, out int width, out int height);
            if (width <= 0 || height <= 0 || Glfw.GetWindowAttrib(window, 0x00020002) == 1) { Thread.Sleep(20); continue; }
            if (timer.Elapsed.TotalSeconds < nextFrame) { Thread.Sleep(1); continue; }
            nextFrame = timer.Elapsed.TotalSeconds + 1.0 / session.Config.Fps;
            if (!paused)
            {
                bool wasRealtime = session.IsRealtime;
                int? action = manual ? Glfw.GetKey(window, 263) == 1 ? 1 : Glfw.GetKey(window, 262) == 1 ? 2 : 0 : null;
                session.Step(action); statistics.Add(session.FrameMilliseconds); autosave?.Invoke(); audio.Submit(session.Game.Audio, ArcadeGame.AudioSize * session.Config.Fps);
                if (session.Frames % 120 == 0 || wasRealtime != session.IsRealtime) RealtimeCommand.PrintStatus(session, statistics);
            }
            Glfw.SetWindowTitle(window, $"{session.Game.Kind} | {(paused ? "PAUSED" : session.IsRealtime ? "REALTIME: HEAD FROZEN" : "CALIBRATING HEAD")} | score {session.Game.Score} env {session.EpisodeReward:F1} | P {session.Pleasure:F2} pain {session.Pain:F2} hurt {session.PrimitivePain:F2} catch {session.PrimitiveReward:F2} clock {session.ClockValue:F2} gain {session.Gain:F2} | {session.Config.GameSpeed:F2}x {session.FrameMilliseconds:F1}ms | {session.MainModel.Topology.Edges.Length:N0} edges | Tab control / Space pause / Esc save+exit");
            Draw(session.Game, session.Observation, session.LastAction, session);
        }
    }
    public void PlayMaze(MazeSession session, int maxFrames, int fps, bool mute, FrameStatistics statistics, Action autosave, Func<bool> stopping)
    {
        using var audio = new AudioPlayback(mute);
        var timer = Stopwatch.StartNew(); double nextFrame = 0; long start = session.Frames;
        mazeManual = mazePaused = mazeReset = false;
        InitializeMazeGraphInput();
        bool oldTab = false, oldSpace = false, oldReset = false, wasPaused = false;
        while (Glfw.WindowShouldClose(window) == 0 && Glfw.GetKey(window, 256) == 0 && !stopping() && (maxFrames == 0 || session.Frames - start < maxFrames))
        {
            PollCanvas(); bool tab = Glfw.GetKey(window, 258) == 1, space = Glfw.GetKey(window, 32) == 1, reset = Glfw.GetKey(window, 82) == 1;
            if (tab && !oldTab) mazeManual = !mazeManual; if (space && !oldSpace) mazePaused = !mazePaused; if ((reset && !oldReset) || mazeReset) { audio.FadeOut(); session.ResetEpisode(); mazeReset=false; }
            if(mazePaused && !wasPaused)audio.FadeOut();
            wasPaused=mazePaused;
            oldTab = tab; oldSpace = space; oldReset = reset;
            Glfw.GetFramebufferSize(window, out int width, out int height);
            if (width <= 0 || height <= 0 || Glfw.GetWindowAttrib(window, 0x00020002) == 1) { Thread.Sleep(20); continue; }
            if (timer.Elapsed.TotalSeconds < nextFrame) { Thread.Sleep(1); continue; }
            nextFrame = timer.Elapsed.TotalSeconds + 1.0 / fps;
            if (!mazePaused)
            {
                MazeControls? controls=null;
                if(mazeManual)
                {
                    controls=MazeKeyBinding.Read(Key);
                }
                session.StepControls(controls);statistics.Add(session.FrameMilliseconds);autosave();audio.SubmitSounds(session.Game.Sounds);
                if (session.Frames % 600 == 0) MazeCommand.Print(session, statistics);
            }
            string rewardStatus = session.IsBakedBrain ? $"reward teaching {session.RewardUpdates} / perceived dR +{session.Pleasure:F2} -{session.Pain:F2}" : session.Training ? $"ideal P {session.Game.TeacherTarget.Pleasure:F2} pain {session.Game.TeacherTarget.Pain:F2} / head P {session.Pleasure:F2} pain {session.Pain:F2}" : $"head P {session.Pleasure:F2} pain {session.Pain:F2}";
            Glfw.SetWindowTitle(window, $"TOP VIEW + 1D VISION | Maze {session.Game.MapWidth}×{session.Game.MapHeight} | {(mazePaused ? "PAUSED" : session.Training ? "TRAIN" : "PLAY: MAIN LEARNS / REWARD FROZEN")} | {rewardStatus} | {(mazeManual ? "MANUAL" : session.BotEnabled ? $"BOT {session.BotStatus}" : "AI")} HP {session.Game.Health:F0} ammo {session.Game.Ammo}/{session.Game.Reserve} {(session.Game.Reloading ? "RELOADING" : "")} | level {session.Game.Level} geometry {session.Game.DifficultyStage+1}/5 kills {session.TotalKills} exits {session.CompletedMazes} | {session.FrameMilliseconds:F1}ms | Tab control, W/S A/D Q/E F fire X reload, Space pause, Esc save");
            Draw(null, session.Observation, 0, maze: session);
        }
    }
    private void Draw(ArcadeGame? game, float[] observation, int action, RealtimeSession? session = null, MazeSession? maze = null, Action<float,float>? canvas = null)
    {
        Glfw.GetFramebufferSize(window, out int width, out int height);
        if (width <= 0 || height <= 0) { Thread.Sleep(20); return; }
        if (swapchain.Handle == 0 || (uint)width != extent.Width || (uint)height != extent.Height) Recreate();
        var f = fence; VulkanDevice.Check(Vk.WaitForFences(context.Device, 1, &f, true, ulong.MaxValue));
        uint index = 0; var result = swapchainApi.AcquireNextImage(context.Device, swapchain, ulong.MaxValue, acquired, default, &index);
        if (result == Result.ErrorOutOfDateKhr) { Recreate(); return; }
        if (result != Result.SuboptimalKhr) VulkanDevice.Check(result);
        context.Begin(command);
        var clear = new ClearValue { Color = new ClearColorValue(.018f, .025f, .045f, 1) };
        var begin = new RenderPassBeginInfo { SType = StructureType.RenderPassBeginInfo, RenderPass = renderPass, Framebuffer = framebuffers[index],
            RenderArea = new Rect2D(default, extent), ClearValueCount = 1, PClearValues = &clear };
        if (framePixels is null) Vk.CmdBeginRenderPass(command, in begin, SubpassContents.Inline);
        else framePixels.Ints.Fill(PackColor(.018f,.025f,.045f));
        float w = extent.Width, h = extent.Height;
        Graph.Clear();
        DrawScene(game,observation,action,session,maze,w,h,canvas);
        // Optional developer snapshot of the composed framebuffer, without desktop capture.
        if(!frameCaptured && framePixels is not null && Environment.GetEnvironmentVariable("ARCADELAB_FRAME_CAPTURE") is string capturePath)
        {
            DrawGraphReference();
            using var output=File.Create(capturePath);
            byte[] header=System.Text.Encoding.ASCII.GetBytes($"P6\n{extent.Width} {extent.Height}\n255\n");output.Write(header);
            byte[] rgb=new byte[checked((int)(extent.Width*extent.Height*3))];var pixels=framePixels.Ints;
            for(int i=0;i<pixels.Length;i++)
            {
                int value=pixels[i];rgb[3*i]=(byte)(blueFirst?value>>16:value);rgb[3*i+1]=(byte)(value>>8);rgb[3*i+2]=(byte)(blueFirst?value:value>>16);
            }
            output.Write(rgb);frameCaptured=true;
        }
        if (framePixels is null)
        {
            Vk.CmdEndRenderPass(command);
            var graphBarrier=new ImageMemoryBarrier {SType=StructureType.ImageMemoryBarrier,OldLayout=ImageLayout.PresentSrcKhr,NewLayout=ImageLayout.ColorAttachmentOptimal,
                SrcAccessMask=AccessFlags.ColorAttachmentWriteBit,DstAccessMask=AccessFlags.ColorAttachmentReadBit|AccessFlags.ColorAttachmentWriteBit,
                SrcQueueFamilyIndex=uint.MaxValue,DstQueueFamilyIndex=uint.MaxValue,Image=swapchainImages[index],SubresourceRange=new(ImageAspectFlags.ColorBit,0,1,0,1)};
            Vk.CmdPipelineBarrier(command,PipelineStageFlags.ColorAttachmentOutputBit,PipelineStageFlags.ColorAttachmentOutputBit,0,0,null,0,null,1,&graphBarrier);
            graphRenderer!.Draw(command,graphFrames[index],Graph);
            graphBarrier.OldLayout=ImageLayout.ColorAttachmentOptimal;graphBarrier.NewLayout=ImageLayout.PresentSrcKhr;
            graphBarrier.SrcAccessMask=AccessFlags.ColorAttachmentWriteBit;graphBarrier.DstAccessMask=0;
            Vk.CmdPipelineBarrier(command,PipelineStageFlags.ColorAttachmentOutputBit,PipelineStageFlags.BottomOfPipeBit,0,0,null,0,null,1,&graphBarrier);
        }
        else
        {
            var range = new ImageSubresourceRange(ImageAspectFlags.ColorBit,0,1,0,1);
            var barrier = new ImageMemoryBarrier { SType=StructureType.ImageMemoryBarrier, OldLayout=ImageLayout.Undefined, NewLayout=ImageLayout.TransferDstOptimal,
                DstAccessMask=AccessFlags.TransferWriteBit, SrcQueueFamilyIndex=uint.MaxValue, DstQueueFamilyIndex=uint.MaxValue, Image=swapchainImages[index], SubresourceRange=range };
            Vk.CmdPipelineBarrier(command,PipelineStageFlags.TopOfPipeBit,PipelineStageFlags.TransferBit,0,0,null,0,null,1,&barrier);
            var copy = new BufferImageCopy { ImageSubresource=new ImageSubresourceLayers(ImageAspectFlags.ColorBit,0,0,1), ImageExtent=new Extent3D(extent.Width,extent.Height,1) };
            Vk.CmdCopyBufferToImage(command,framePixels.Buffer,swapchainImages[index],ImageLayout.TransferDstOptimal,1,&copy);
            barrier.OldLayout=ImageLayout.TransferDstOptimal;barrier.NewLayout=ImageLayout.ColorAttachmentOptimal;barrier.SrcAccessMask=AccessFlags.TransferWriteBit;
            barrier.DstAccessMask=AccessFlags.ColorAttachmentReadBit|AccessFlags.ColorAttachmentWriteBit;
            Vk.CmdPipelineBarrier(command,PipelineStageFlags.TransferBit,PipelineStageFlags.ColorAttachmentOutputBit,0,0,null,0,null,1,&barrier);
            graphRenderer!.Draw(command,graphFrames[index],Graph);
            barrier.OldLayout=ImageLayout.ColorAttachmentOptimal;barrier.NewLayout=ImageLayout.PresentSrcKhr;
            barrier.SrcAccessMask=AccessFlags.ColorAttachmentWriteBit;barrier.DstAccessMask=0;
            Vk.CmdPipelineBarrier(command,PipelineStageFlags.ColorAttachmentOutputBit,PipelineStageFlags.BottomOfPipeBit,0,0,null,0,null,1,&barrier);
        }
        VulkanDevice.Check(Vk.EndCommandBuffer(command));
        var wait = acquired; var signal = rendered[index]; var cmd = command; var stage = framePixels is null ? PipelineStageFlags.ColorAttachmentOutputBit : PipelineStageFlags.TransferBit;
        var submit = new SubmitInfo { SType = StructureType.SubmitInfo, WaitSemaphoreCount = 1, PWaitSemaphores = &wait, PWaitDstStageMask = &stage,
            CommandBufferCount = 1, PCommandBuffers = &cmd, SignalSemaphoreCount = 1, PSignalSemaphores = &signal };
        VulkanDevice.Check(Vk.ResetFences(context.Device, 1, &f)); VulkanDevice.Check(Vk.QueueSubmit(context.Queue, 1, in submit, fence));
        var sc = swapchain; var present = new PresentInfoKHR { SType = StructureType.PresentInfoKhr, WaitSemaphoreCount = 1, PWaitSemaphores = &signal,
            SwapchainCount = 1, PSwapchains = &sc, PImageIndices = &index };
        result = swapchainApi.QueuePresent(context.Queue, in present);
        if (result is Result.ErrorOutOfDateKhr or Result.SuboptimalKhr) Recreate(); else VulkanDevice.Check(result);
    }
    private void DrawScene(ArcadeGame? game,float[] observation,int action,RealtimeSession? session,MazeSession? maze,float w,float h,Action<float,float>? canvas=null)
    {
        if(maze is not null) BeginMazeGui(w,h);
        if(canvas is not null) canvas(w,h);
        else
        {
        if (maze is null) Panel(game!.Pixels, .035f*w, .05f*h, .6f*w, .68f*h, 1);
        else
        {
            float mapSize=MathF.Min(.60f*w,.51f*h),mapX=.035f*w+(.60f*w-mapSize)/2;

            maze.Game.RenderTopView(mazeMap,512,512);
            ColorPanel(mazeMap,512,512,mapX,.09f*h,mapSize,mapSize);
            string[] legends=["PLAYER","ENEMIES","HEALTH","AMMO","EXIT","SHOTS"];
            uint[] colors=[0x6CCFF6,0xED6370,0x6EF0B0,0xFFE19D,0x76EDE5,0xFFA861];
            for(int i=0;i<legends.Length;i++)
            {
                float lx=(.035f+i*.10f)*w;uint color=colors[i];
                Rect(lx,.612f*h,10,10,((color>>16)&255)/255f,((color>>8)&255)/255f,(color&255)/255f);
                Label(legends[i],lx+15,.61f*h,2);
            }
            Rect(.035f*w,.65f*h,.6f*w,.022f*h,.12f,.18f,.23f);
            Rect(.035f*w,.65f*h,.6f*w*maze.Game.Health/100,.022f*h,.25f,.88f,.60f);
            for (int i=0;i<MazeGame.MagazineSize;i++) Rect((.035f+i*.041f)*w,.69f*h,.033f*w,.022f*h,i<maze.Game.Ammo?1:.16f,i<maze.Game.Ammo?.81f:.19f,.35f);
            Rect(.39f*w,.69f*h,.245f*w*maze.Game.Reserve/96,.022f*h,.64f,.65f,.87f);
            if(maze.Game.Reloading)Rect(.035f*w,.72f*h,.33f*w,.006f*h,1,.8f,.35f);
            Label("CURRENT VIEW",.035f*w,.745f*h,2);
            VisionStrip(observation.AsSpan(0,MazeGame.VisionRayCount*3),.035f*w,.77f*h,.6f*w,.06f*h);
            Rect(.335f*w-1,.835f*h,2,.01f*h,.7f,.9f,1);
            Label("LEFT",.035f*w,.835f*h,2);Label("RIGHT",.57f*w,.835f*h,2);
        }
        if (maze is not null)
        {
            MazeEditorGraph(maze,w,h);
            NetworkErrorBar(maze.Loss,w,h);
            CombatRewardReadout(maze,w,h);
            Label($"MOTOR SOURCE {maze.ControlOrigin}",.70f*w,.529f*h,1);
            Label(FormattableString.Invariant($"MAIN SUM E2 {maze.Loss:F4}   HEAD SUM E2 {maze.RewardLoss:F4}"),.70f*w,.795f*h,1);
            Label(maze.IsBakedBrain?$"REWARD TEACHING {maze.RewardUpdates} / ERROR {maze.RewardTeachingError:F4}":maze.Training?FormattableString.Invariant($"TEACHING SUM E2 {maze.RewardTeachingError:F4}"):"REWARD WEIGHTS FROZEN - ACTIVITIES CONTINUE",.70f*w,.81f*h,1);
            Label(FormattableString.Invariant($"H {maze.Config.ActivityStep:F4} STEPS {maze.Config.PropagationSteps} TOTAL P {maze.TotalPleasure:F3} N {maze.TotalPain:F3}"),.70f*w,.825f*h,1);
            Label(FormattableString.Invariant($"{(maze.IsBakedBrain?"MEAN LOCAL FACTOR":"REWARD FACTOR")} {maze.WeightRewardFactor:F3}  ETA {maze.EffectiveLearningRate:F7}"),.70f*w,.84f*h,1);
            if(maze.IsBakedBrain)CortexAssignedRewards(maze,w,h);
            else
            {
                Rect(.70f*w,.59f*h,.25f*w*Math.Clamp(maze.AppliedPleasure/(maze.IsBakedBrain?1:MazeGame.MaximumPleasure),0,1),.025f*h,.25f,.95f,.4f);
                Rect(.70f*w,.64f*h,.25f*w*Math.Clamp(maze.AppliedPain,0,1),.025f*h,.95f,.3f,.25f);
                Rect(.70f*w,.69f*h,.25f*w*maze.PrimitivePain,.025f*h,1,.65f,.15f);
                Rect(.70f*w,.74f*h,.25f*w*maze.PrimitivePleasure,.025f*h,.2f,.65f,1);
                Label(FormattableString.Invariant($"{(maze.IsBakedBrain?"PERCEIVED +dR/dt":maze.Training?"IDEAL PLEASURE TRACE":"PREDICTED PLEASURE TRACE")} {maze.AppliedPleasure:F3}"),.70f*w,.577f*h,1);
                Label(FormattableString.Invariant($"{(maze.IsBakedBrain?"PERCEIVED -dR/dt":maze.Training?"IDEAL PAIN TRACE":"PREDICTED PAIN TRACE")} {maze.AppliedPain:F3}"),.70f*w,.627f*h,1);
                Label(FormattableString.Invariant($"PRIMITIVE PAIN {maze.PrimitivePain:F3} WALL {maze.Game.WallCollisionPain:F3} DEATH {maze.Game.PrimitiveDeathPenalty:F0}"),.70f*w,.677f*h,1);
                Label("PRIMITIVE PLEASURE",.70f*w,.727f*h,1);
            }
            if (maze.Training&&!maze.IsBakedBrain)
            {
                Label(FormattableString.Invariant($"HEAD PRED P {maze.Pleasure:F3} N {maze.Pain:F3} - WHITE TICKS"),.70f*w,.78f*h,1);
                Rect((.70f+.25f*maze.Pleasure/MazeGame.MaximumPleasure)*w,.585f*h,2,.035f*h,1,1,1);
                Rect((.70f+.25f*maze.Pain)*w,.635f*h,2,.035f*h,1,1,1);
            }
        }
        else if (session is null) Panel(observation.AsSpan(Sensors.PixelCount, Sensors.PixelCount), .69f*w, .06f*h, .27f*w, .32f*h, .65f);
        else
        {
            NodeGraph(session.MainModel,session.MainBackend,session.Activations,session.Config.Layers,session.Config.Width,w,h);
            Rect(.70f*w,.59f*h,.25f*w*(session.Pleasure/session.MaximumPleasure),.025f*h,.25f,.95f,.4f);
            Rect(.70f*w,.64f*h,.25f*w*session.Pain,.025f*h,.95f,.3f,.25f);
            Rect(.70f*w,.69f*h,.25f*w*session.PrimitivePain,.025f*h,1,.65f,.15f);
            Rect(.70f*w,.74f*h,.25f*w*session.PrimitiveReward,.025f*h,.2f,.65f,1);
        }
        int indicators=maze is null?3:MazeControlPolicy.Buttons.Length;
        for(int i=0;i<indicators;i++)
        {
            bool active=maze is null?action==i:(maze.LastControls&MazeControlPolicy.Buttons[i])!=0;
            Rect((.70f+i*(maze is null?.08f:.03125f))*w,(maze is null?.47f:.55f)*h,(maze is null?.055f:.023f)*w,.025f*h,active?.3f:.07f,active?.95f:.12f,.5f);
            if(maze is not null)
            {
                Label("WSADQEFX"[i].ToString(),(.70f+i*.03125f)*w+4,.55f*h+3,2);
                Label(FormattableString.Invariant($"{(maze.Baked is {} b?b.Brain.Manifest.Neurons[b.Motors[i]].Id:i.ToString())}:{maze.ControlScores[i]:F2}"),(.70f+i*.03125f)*w,.54f*h,1);
            }
        }
        int spectrum = maze is null ? Sensors.PixelCount * 2 + ArcadeGame.AudioSize : MazeSession.LeftSpectrumOffset;
        for (int i = 0; i < Sensors.SpectrumSize; i++)
        {
            float bar = Math.Clamp(observation[spectrum+i]*8, .01f, 1)*h*(maze is null ? .17f : .05f);
            Rect(w*(.04f+i*.018f), h*(maze is null?.95f:.985f)-bar, w*(maze is null ? .012f : .005f), bar, .2f, .8f, 1);
            if (maze is not null)
            {
                float rightBar=Math.Clamp(observation[MazeSession.LegacyObservationCount+MazeGame.AudioSize+i]*8,.01f,1)*h*.05f;
                Rect(w*(.046f+i*.018f),h*.985f-rightBar,w*.005f,rightBar,1,.4f,.7f);
            }
        }
        if(maze?.IsBakedBrain==true)CortexRewardReadout(maze,w,h);
        var samples = maze is null ? game!.Audio : maze.Game.Audio;
        for (int i = 0; i < (maze?.IsBakedBrain==true?0:samples.Length); i++)
        {
            Rect(w*(.7f+i*.004f), h*((maze is not null ? .885f : session is null ? .8f : .88f)+samples[i]*(maze is not null?.015f:.035f)), Math.Max(1,w*.003f), 2, .2f,.8f,1);
            if (maze is not null) Rect(w*(.7f+i*.004f),h*(.95f+maze.Game.AudioRight[i]*.015f),Math.Max(1,w*.003f),2,1,.4f,.7f);
        }
        }
        if (maze is not null) MazeToolbar(maze,w,h);
    }
    public void RenderMazePreview(MazeSession session,float width,float height,MYT.Kit.Gui.GuiInput? input=null)
    {
        mazePreviewInput=input;
        try{DrawScene(null,session.Observation,0,null,session,width,height);}
        finally{mazePreviewInput=null;}
    }
    public static void CaptureMaze(MazeSession session,int width,int height,string path)
        =>CaptureCanvas(width,height,path,(viewer,w,h)=>viewer.RenderMazePreview(session,w,h));
    private static readonly Dictionary<char,byte[]> labelFont=new()
    {
        ['[']=[14,8,8,8,8,8,14],[']']=[14,2,2,2,2,2,14],
        ['{']=[6,8,8,16,8,8,6],['}']=[12,2,2,1,2,2,12],
        ['"']=[10,10,0,0,0,0,0],[',']=[0,0,0,0,0,4,8],
        ['(']=[2,4,8,8,8,4,2],[')']=[8,4,2,2,2,4,8],
        ['<']=[1,2,4,8,4,2,1],['%']=[17,2,4,8,16,17,0],
        ['_']=[0,0,0,0,0,0,31],['*']=[0,21,14,31,14,21,0],
        ['0']=[14,17,19,21,25,17,14],['1']=[4,12,4,4,4,4,14],['2']=[14,17,1,2,4,8,31],
        ['3']=[30,1,1,14,1,1,30],['4']=[2,6,10,18,31,2,2],['5']=[31,16,16,30,1,1,30],
        ['6']=[14,16,16,30,17,17,14],['7']=[31,1,2,4,8,8,8],['8']=[14,17,17,14,17,17,14],['9']=[14,17,17,15,1,1,14],
        ['.']=[0,0,0,0,0,6,6],['-']=[0,0,0,31,0,0,0],['+']=[0,4,4,31,4,4,0],['=']=[0,0,31,0,31,0,0],
        ['>']=[16,8,4,2,4,8,16],[':']=[0,6,6,0,6,6,0],['/']=[1,2,2,4,8,8,16],
        ['K']=[17,18,20,24,20,18,17],['Z']=[31,1,2,4,8,16,31],['J']=[7,2,2,2,18,18,12],
        ['Q']=[14,17,17,17,21,18,13],['M']=[17,27,21,21,17,17,17],['Y']=[17,17,10,4,4,4,4],['X']=[17,17,10,4,10,17,17],
        ['L']=[16,16,16,16,16,16,31],['F']=[31,16,16,30,16,16,16],['G']=[14,17,16,23,17,17,15],['H']=[17,17,17,31,17,17,17],
        ['C']=[14,17,16,16,16,17,14],['U']=[17,17,17,17,17,17,14],['R']=[30,17,17,30,20,18,17],
        ['E']=[31,16,16,30,16,16,31],['N']=[17,25,25,21,19,19,17],['T']=[31,4,4,4,4,4,4],
        ['V']=[17,17,17,17,17,10,4],['I']=[14,4,4,4,4,4,14],['W']=[17,17,17,21,21,27,17],
        ['A']=[14,17,17,31,17,17,17],['D']=[30,17,17,17,17,17,30],['B']=[30,17,17,30,17,17,30],
        ['P']=[30,17,17,30,16,16,16],['O']=[14,17,17,17,17,17,14],['S']=[15,16,16,14,1,1,30]
    };
    private void Label(string text,float x,float y,int scale)
    {
        if(mazeGuiFrame)
        {
            float size=scale==1?Math.Max(8,extent.Height*.009f):Math.Max(10,extent.Height*.014f);
            mazeGui!.Text(new GlmSharp.vec2(x,y),text,mazeGui.Style.Text,size);
            return;
        }
        scale=Math.Max(1,(int)MathF.Round(scale*MathF.Min(extent.Width/1600f,extent.Height/1000f)));
        foreach(char letter in text)
        {
            if(labelFont.TryGetValue(letter,out var glyph))
                for(int row=0;row<7;row++)for(int col=0;col<5;col++)if((glyph[row]&(1<<(4-col)))!=0)
                    Rect(x+col*scale,y+row*scale,scale,scale,.65f,.85f,.95f);
            x+=6*scale;
        }
    }
    private void VisionStrip(ReadOnlySpan<float> vision,float x,float y,float width,float height)
    {
        Rect(x-2,y-2,width+4,height+4,.12f,.28f,.36f);
        int pixels=vision.Length/MazeVision.Channels;
        int bins=Math.Min(pixels,Math.Max(1,(int)width));
        for (int bin=0;bin<bins;bin++)
        {
            // At narrow window sizes average adjacent inputs, so every pixel still contributes.
            int from=bin*pixels/bins,to=(bin+1)*pixels/bins; float r=0,g=0,b=0;
            for(int i=from;i<to;i++) { r+=vision[3*i];g+=vision[3*i+1];b+=vision[3*i+2]; }
            float scale=1f/(to-from);
            float left=MathF.Floor(x+bin*width/bins),right=MathF.Floor(x+(bin+1)*width/bins);
            Rect(left,y,right-left,height,r*scale,g*scale,b*scale);
        }

    }
    private void ColorPanel(uint[] pixels, int columns, int rows, float x, float y, float width, float height)
    {
        Rect(x-3,y-3,width+6,height+6,.12f,.22f,.30f);
        // The transfer renderer composes these spans in one reusable framebuffer and uploads once.
        for (int row=0;row<rows;row++) for (int col=0;col<columns;)
        {
            uint color=pixels[row*columns+col]; int end=col+1;
            while (end<columns && pixels[row*columns+end]==color) end++;
            float left=MathF.Floor(x+col*width/columns), top=MathF.Floor(y+row*height/rows);
            float right=MathF.Floor(x+end*width/columns), bottom=MathF.Floor(y+(row+1)*height/rows);
            Rect(left,top,right-left,bottom-top,((color>>16)&255)/255f,((color>>8)&255)/255f,(color&255)/255f); col=end;
        }
    }
    private void Panel(ReadOnlySpan<float> pixels, float x, float y, float width, float height, float brightness, int columns = ArcadeGame.Width, int rows = ArcadeGame.Height)
    {
        Rect(x-3,y-3,width+6,height+6,.07f,.15f,.2f); Rect(x,y,width,height,.025f,.04f,.06f);
        for (int row=0;row<rows;row++) for(int col=0;col<columns;col++)
        {
            float value=pixels[row*columns+col]*brightness; if(value<=0) continue;
            Rect(x+col*width/columns,y+row*height/rows,width/columns+1,height/rows+1,value*.5f,value,value*.8f);
        }
    }
    private void Rect(float x,float y,float width,float height,float r,float g,float b)
    {
        int left=Math.Clamp((int)x,0,(int)extent.Width), top=Math.Clamp((int)y,0,(int)extent.Height);
        int right=Math.Clamp((int)(x+width),left,(int)extent.Width), bottom=Math.Clamp((int)(y+height),top,(int)extent.Height);
        if(right==left||bottom==top)return;
        if (framePixels is not null || softwarePixels is not null)
        {
            int color=PackColor(r,g,b); Span<int> pixels=softwarePixels is not null ? softwarePixels.AsSpan() : framePixels!.Ints;
            for (int row=top;row<bottom;row++) pixels.Slice(row*(int)extent.Width+left,right-left).Fill(color);
            return;
        }
        var attachment=new ClearAttachment { AspectMask=ImageAspectFlags.ColorBit, ColorAttachment=0, ClearValue=new ClearValue { Color=new ClearColorValue(r,g,b,1) } };
        var rect=new ClearRect { Rect=new Rect2D(new Offset2D(left,top),new Extent2D((uint)(right-left),(uint)(bottom-top))), LayerCount=1 };
        Vk.CmdClearAttachments(command,1,in attachment,1,in rect);
    }
    private int PackColor(float r,float g,float b)
    {
        int red=(int)(Math.Clamp(r,0,1)*255), green=(int)(Math.Clamp(g,0,1)*255), blue=(int)(Math.Clamp(b,0,1)*255);
        return unchecked((int)0xFF000000) | (blueFirst ? red<<16|green<<8|blue : blue<<16|green<<8|red);
    }
    private void DestroySwapchain()
    {
        graphRenderer?.Dispose();graphRenderer=null;graphFrames=[];
        framePixels?.Dispose(); framePixels=null; swapchainImages=[];
        foreach(var f in framebuffers) if(f.Handle!=0) Vk.DestroyFramebuffer(context.Device,f,null); framebuffers=[];
        foreach(var v in views) if(v.Handle!=0) Vk.DestroyImageView(context.Device,v,null); views=[];
        foreach(var s in rendered) if(s.Handle!=0) Vk.DestroySemaphore(context.Device,s,null); rendered=[];
        if(renderPass.Handle!=0) { Vk.DestroyRenderPass(context.Device,renderPass,null); renderPass=default; }
        if(swapchain.Handle!=0) { swapchainApi.DestroySwapchain(context.Device,swapchain,null); swapchain=default; }
    }
    public void Dispose()
    {
        if(context is not null)
        {
            Vk.DeviceWaitIdle(context.Device); DestroySwapchain();
            if(fence.Handle!=0) Vk.DestroyFence(context.Device,fence,null);
            if(acquired.Handle!=0) Vk.DestroySemaphore(context.Device,acquired,null);
            if(surface.Handle!=0) surfaceApi.DestroySurface(context.Instance,surface,null);
            surfaceApi?.Dispose(); swapchainApi?.Dispose(); context.Dispose(); context=null!;
        }
        if(window!=0) { Glfw.DestroyWindow(window); window=0; }
        if(initialized) { Glfw.Terminate(); initialized=false; }
    }
}
