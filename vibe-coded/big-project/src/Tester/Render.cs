
using GlmSharp;

using Silk.NET.GLFW;
using Silk.NET.Vulkan;

using MYT.Kit.Vulkan;

using Image = Silk.NET.Vulkan.Image;
using Buffer = Silk.NET.Vulkan.Buffer;
using Semaphore = Silk.NET.Vulkan.Semaphore;

namespace IntelliCraft;

public static partial class Program
{

    private const float RotationSpeed = 0.6f;

    private static uint currentFrame = 0;

    private static unsafe void DrawFrame()
    {

        vk.WaitForFences(device, 1, in inFlightFences[currentFrame], true, ulong.MaxValue);

        uint imageIndex = 0;
        Result acquireResult = khrSwapchain.AcquireNextImage(device, swapchain, ulong.MaxValue, imageAvailableSemaphores[currentFrame], default, &imageIndex);
        if (acquireResult != Result.Success && acquireResult != Result.SuboptimalKhr)
            throw new Exception($"Failed to acquire swapchain image: {acquireResult}");

        vk.ResetFences(device, 1, in inFlightFences[currentFrame]);



        UpdateUniforms(currentFrame);
        UpdateRigidBodyUniforms(currentFrame);
        CommandBuffer cmd = commandBuffers[currentFrame];
        vk.ResetCommandBuffer(cmd, 0);
        RecordCommandBuffer(cmd, imageIndex, currentFrame);


        PipelineStageFlags waitStage = PipelineStageFlags.ColorAttachmentOutputBit;
        Semaphore waitSemaphore = imageAvailableSemaphores[currentFrame];
        Semaphore signalSemaphore = renderFinishedSemaphores[imageIndex];

        SubmitInfo submitInfo = new()
        {
            SType = StructureType.SubmitInfo,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &waitSemaphore,
            PWaitDstStageMask = &waitStage,
            CommandBufferCount = 1,
            PCommandBuffers = &cmd,
            SignalSemaphoreCount = 1,
            PSignalSemaphores = &signalSemaphore
        };

        if (vk.QueueSubmit(graphicsQueue, 1, &submitInfo, inFlightFences[currentFrame]) != Result.Success)
            throw new Exception("Failed to submit draw command buffer!");



        SwapchainKHR swapchainHandle = swapchain;
        PresentInfoKHR presentInfo = new()
        {
            SType = StructureType.PresentInfoKhr,
            WaitSemaphoreCount = 1,
            PWaitSemaphores = &signalSemaphore,
            SwapchainCount = 1,
            PSwapchains = &swapchainHandle,
            PImageIndices = &imageIndex
        };

        khrSwapchain.QueuePresent(presentQueue, &presentInfo);

        currentFrame = (currentFrame + 1) % FramesInFlight;

    }


    private static void UpdateUniforms(uint frameIndex)
    {

        float time = (float)Glfw.GetTime();
        float aspect = (float)swapchainExtent.Width / swapchainExtent.Height;

        vec3 eye = CameraPosition();
        mat4 model = mat4.Identity;
        mat4 view = CameraViewMatrix();
        mat4 projection = mat4.Perspective(glm.Radians(45f), aspect, 0.1f, 100f);

        material.WriteTransform(frameIndex, model, view, projection);

        material.WriteLighting
        (
            frameIndex,
            lightPosition: new vec3(4f, 4f, 4f),
            ambientStrength: 0.15f,
            lightColor: new vec3(1f, 1f, 1f),
            specularStrength: 0.5f,
            viewPosition: eye,
            shininess: 32f,
            objectColor: new vec3(0.85f, 0.35f, 0.2f)
        );

    }


    private static unsafe void RecordCommandBuffer(CommandBuffer cmd, uint imageIndex, uint frameIndex)
    {

        CommandBufferBeginInfo beginInfo = new() { SType = StructureType.CommandBufferBeginInfo };
        vk.BeginCommandBuffer(cmd, &beginInfo);


        TransitionImageLayout
        (
            cmd, swapchainImages[imageIndex],
            ImageLayout.Undefined, ImageLayout.ColorAttachmentOptimal,
            AccessFlags.None, AccessFlags.ColorAttachmentWriteBit,
            PipelineStageFlags.TopOfPipeBit, PipelineStageFlags.ColorAttachmentOutputBit,
            ImageAspectFlags.ColorBit
        );


        RenderingAttachmentInfo colorAttachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = swapchainImageViews[imageIndex],
            ImageLayout = ImageLayout.ColorAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.Store,
            ClearValue = new ClearValue(new ClearColorValue(0.02f, 0.02f, 0.05f, 1f), null)
        };

        RenderingAttachmentInfo depthAttachment = new()
        {
            SType = StructureType.RenderingAttachmentInfo,
            ImageView = depthImageView,
            ImageLayout = ImageLayout.DepthAttachmentOptimal,
            LoadOp = AttachmentLoadOp.Clear,
            StoreOp = AttachmentStoreOp.DontCare,
            ClearValue = new ClearValue(null, new ClearDepthStencilValue(1f, 0))
        };

        RenderingInfo renderingInfo = new()
        {
            SType = StructureType.RenderingInfo,
            RenderArea = new Rect2D(new Offset2D(0, 0), swapchainExtent),
            LayerCount = 1,
            ColorAttachmentCount = 1,
            PColorAttachments = &colorAttachment,
            PDepthAttachment = &depthAttachment
        };


        vk.CmdBeginRendering(cmd, &renderingInfo);

        material.BindPipeline(cmd);

        Viewport viewport = new()
        {
            X = 0,
            Y = swapchainExtent.Height,
            Width = swapchainExtent.Width,
            Height = -(float)swapchainExtent.Height,
            MinDepth = 0,
            MaxDepth = 1
        };
        Rect2D scissor = new(new Offset2D(0, 0), swapchainExtent);

        vk.CmdSetViewport(cmd, 0, 1, &viewport);
        vk.CmdSetScissor(cmd, 0, 1, &scissor);

        material.BindDescriptorSet(cmd, frameIndex);
        mapMesh.Render(cmd);

        RenderRigidBody(cmd, frameIndex, boxBody0);
        RenderRigidBody(cmd, frameIndex, boxBody1);
        RenderRigidBody(cmd, frameIndex, boxBody2);
        RenderRigidBody(cmd, frameIndex, boxBody3);
        RenderRigidBody(cmd, frameIndex, boxBody4);
        RenderSoftBody(cmd, frameIndex, softBody);

        vk.CmdEndRendering(cmd);


        TransitionImageLayout
        (
            cmd, swapchainImages[imageIndex],
            ImageLayout.ColorAttachmentOptimal, ImageLayout.PresentSrcKhr,
            AccessFlags.ColorAttachmentWriteBit, AccessFlags.None,
            PipelineStageFlags.ColorAttachmentOutputBit, PipelineStageFlags.BottomOfPipeBit,
            ImageAspectFlags.ColorBit
        );


        vk.EndCommandBuffer(cmd);

    }


    private static unsafe void TransitionImageLayout
    (
        CommandBuffer cmd, Image image,
        ImageLayout oldLayout, ImageLayout newLayout,
        AccessFlags srcAccess, AccessFlags dstAccess,
        PipelineStageFlags srcStage, PipelineStageFlags dstStage,
        ImageAspectFlags aspect
    )
    {

        ImageMemoryBarrier barrier = new()
        {
            SType = StructureType.ImageMemoryBarrier,
            OldLayout = oldLayout,
            NewLayout = newLayout,
            SrcQueueFamilyIndex = Vk.QueueFamilyIgnored,
            DstQueueFamilyIndex = Vk.QueueFamilyIgnored,
            Image = image,
            SubresourceRange = new ImageSubresourceRange(aspect, 0, 1, 0, 1),
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess
        };

        vk.CmdPipelineBarrier(cmd, srcStage, dstStage, 0, 0, null, 0, null, 1, &barrier);

    }


}
