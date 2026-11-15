using System.Numerics;
using MYT.Kit.Graph;
using MYT.Kit.Gui;
using NeuralGraph;
using Silk.NET.Vulkan;

internal static unsafe class GpuGraphChecks
{
    public static void Run(Action<bool,string> check)
    {
        using var device=new VulkanDevice(graphics:true);
        var vk=device.Api;
        Image image=default;ImageView view=default;DeviceMemory memory=default;Fence fence=default;
        GraphGpuRenderer? renderer=null;
        try
        {
            var create=new ImageCreateInfo {SType=StructureType.ImageCreateInfo,ImageType=ImageType.Type2D,Format=Format.R8G8B8A8Unorm,
                Extent=new(32,32,1),MipLevels=1,ArrayLayers=1,Samples=SampleCountFlags.Count1Bit,Tiling=ImageTiling.Optimal,
                Usage=ImageUsageFlags.ColorAttachmentBit|ImageUsageFlags.TransferDstBit|ImageUsageFlags.TransferSrcBit,SharingMode=SharingMode.Exclusive};
            VulkanDevice.Check(vk.CreateImage(device.Device,in create,null,out image));
            vk.GetImageMemoryRequirements(device.Device,image,out var req);
            vk.GetPhysicalDeviceMemoryProperties(device.PhysicalDevice,out var properties);
            uint type=0;while((req.MemoryTypeBits&(1u<<(int)type))==0)type++;
            var allocation=new MemoryAllocateInfo {SType=StructureType.MemoryAllocateInfo,AllocationSize=req.Size,MemoryTypeIndex=type};
            VulkanDevice.Check(vk.AllocateMemory(device.Device,in allocation,null,out memory));
            VulkanDevice.Check(vk.BindImageMemory(device.Device,image,memory,0));
            var viewInfo=new ImageViewCreateInfo {SType=StructureType.ImageViewCreateInfo,Image=image,ViewType=ImageViewType.Type2D,
                Format=Format.R8G8B8A8Unorm,SubresourceRange=new(ImageAspectFlags.ColorBit,0,1,0,1)};
            VulkanDevice.Check(vk.CreateImageView(device.Device,in viewInfo,null,out view));
            renderer=new(device,Format.R8G8B8A8Unorm,new(32,32));
            var frame=renderer.AddTarget(view);
            using var readback=device.CreateBuffer(32*32*4,BufferUsageFlags.TransferDstBit);
            var fenceInfo=new FenceCreateInfo {SType=StructureType.FenceCreateInfo};
            VulkanDevice.Check(vk.CreateFence(device.Device,in fenceInfo,null,out fence));
            var command=device.AllocateCommand();
            var list=new GraphDrawList {Clip=new(0,0,32,32)};
            void Render()
            {
                device.Begin(command);
                var barrier=new ImageMemoryBarrier {SType=StructureType.ImageMemoryBarrier,OldLayout=ImageLayout.Undefined,NewLayout=ImageLayout.TransferDstOptimal,
                    DstAccessMask=AccessFlags.TransferWriteBit,SrcQueueFamilyIndex=uint.MaxValue,DstQueueFamilyIndex=uint.MaxValue,
                    Image=image,SubresourceRange=new(ImageAspectFlags.ColorBit,0,1,0,1)};
                vk.CmdPipelineBarrier(command,PipelineStageFlags.TopOfPipeBit,PipelineStageFlags.TransferBit,0,0,null,0,null,1,&barrier);
                var clear=new ClearColorValue(0,0,0,1);var range=barrier.SubresourceRange;
                vk.CmdClearColorImage(command,image,ImageLayout.TransferDstOptimal,in clear,1,in range);
                barrier.OldLayout=ImageLayout.TransferDstOptimal;barrier.NewLayout=ImageLayout.ColorAttachmentOptimal;
                barrier.SrcAccessMask=AccessFlags.TransferWriteBit;barrier.DstAccessMask=AccessFlags.ColorAttachmentReadBit|AccessFlags.ColorAttachmentWriteBit;
                vk.CmdPipelineBarrier(command,PipelineStageFlags.TransferBit,PipelineStageFlags.ColorAttachmentOutputBit,0,0,null,0,null,1,&barrier);
                renderer.Draw(command,frame,list);
                barrier.OldLayout=ImageLayout.ColorAttachmentOptimal;barrier.NewLayout=ImageLayout.TransferSrcOptimal;
                barrier.SrcAccessMask=AccessFlags.ColorAttachmentWriteBit;barrier.DstAccessMask=AccessFlags.TransferReadBit;
                vk.CmdPipelineBarrier(command,PipelineStageFlags.ColorAttachmentOutputBit,PipelineStageFlags.TransferBit,0,0,null,0,null,1,&barrier);
                var copy=new BufferImageCopy {ImageSubresource=new(ImageAspectFlags.ColorBit,0,0,1),ImageExtent=new(32,32,1)};
                vk.CmdCopyImageToBuffer(command,image,ImageLayout.TransferSrcOptimal,readback.Buffer,1,in copy);
                var host=new MemoryBarrier {SType=StructureType.MemoryBarrier,SrcAccessMask=AccessFlags.TransferWriteBit,DstAccessMask=AccessFlags.HostReadBit};
                vk.CmdPipelineBarrier(command,PipelineStageFlags.TransferBit,PipelineStageFlags.HostBit,0,1,&host,0,null,0,null);
                VulkanDevice.Check(vk.EndCommandBuffer(command));
                var cmd=command;var submit=new SubmitInfo {SType=StructureType.SubmitInfo,CommandBufferCount=1,PCommandBuffers=&cmd};
                var sync=fence;VulkanDevice.Check(vk.ResetFences(device.Device,1,&sync));
                VulkanDevice.Check(vk.QueueSubmit(device.Queue,1,in submit,fence));
                VulkanDevice.Check(vk.WaitForFences(device.Device,1,&sync,true,ulong.MaxValue));
            }
            void Weights(bool reversed)
            {
                list.Clear();
                void Near()=>list.Line(new(5.5f,15.5f,.2f),new(25.5f,15.5f,.2f),new(255,0,0));
                void Far()=>list.Line(new(15.5f,5.5f,.8f),new(15.5f,25.5f,.8f),new(0,0,255));
                if(reversed){Far();Near();}else{Near();Far();}
            }
            Weights(false);Render();int first=readback.Ints[15*32+15];
            Weights(true);Render();
            check((first&0xFFFFFF)==0x0000FF&&readback.Ints[15*32+15]==first,"Vulkan line-list depth test is independent of draw order");
            list.Neuron(new Vector3(15.5f,15.5f,.99f),3,new(0,255,0));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0x0000FF,"Vulkan far neurons are occluded by weights");
            list.Neuron(new Vector3(15.5f,15.5f,.1f),3,new(0,255,0));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0x00FF00,"Vulkan near neurons cover weights");
            list.Neuron(new Vector3(15.5f,15.5f,.7f),3,new(255,255,0));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0x00FF00,"Vulkan neuron depth is independent of submission order");
            list.Clip=new(0,0,10,32);Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0,"Vulkan graph scissor clips weights and neurons");
            list.Clear();list.Clip=new(0,0,32,32);
            list.Guide(new(5.5f,15.5f,.1f),new(25.5f,15.5f,.1f),new(255,0,0,128));Render();
            check((readback.Ints[15*32+15]&255) is >=127 and <=129,"Vulkan orientation guide alpha blending");
            list.Line(new(15.5f,5.5f,.8f),new(15.5f,25.5f,.8f),new(0,0,255));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0xFF0000,"Vulkan guides do not write weight depth");
            list.Clear();list.Clip=new(10,0,22,32);
            list.Neuron(new(9.5f,15.5f,.5f),3,new(0,255,0));Render();
            check((readback.Ints[15*32+10]&0xFFFFFF)==0x00FF00&&(readback.Ints[15*32+9]&0xFFFFFF)==0,
                "Vulkan clips a partial neuron whose center is outside the canvas");
            list.Clear();list.Clip=new(0,0,32,32);
            list.Line(new(5.5f,15.5f,.2f),new(25.5f,15.5f,.2f),new(255,0,0));
            list.Highlight(new(15.5f,5.5f,.8f),new(15.5f,25.5f,.8f),new(0,0,255));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0x0000FF,"Vulkan selected-link highlight respects scene depth");
            list.OverlayRect(new(13,13,5,5),new(0,255,0));Render();
            check((readback.Ints[15*32+15]&0xFFFFFF)==0x00FF00,"Vulkan module-label overlay renders after link highlights");
            Console.WriteLine("GPU graph checks: "+device.Name);
        }
        finally
        {
            vk.DeviceWaitIdle(device.Device);renderer?.Dispose();
            if(fence.Handle!=0)vk.DestroyFence(device.Device,fence,null);
            if(view.Handle!=0)vk.DestroyImageView(device.Device,view,null);
            if(image.Handle!=0)vk.DestroyImage(device.Device,image,null);
            if(memory.Handle!=0)vk.FreeMemory(device.Device,memory,null);
        }
    }
}
