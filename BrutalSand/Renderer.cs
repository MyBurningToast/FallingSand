using Brutal;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;

// uploads the wolrds colours to a small gpu image each frame, then blits it into the swapchain image
public unsafe class Renderer : IDisposable
{
    private readonly Gfx _gfx;

    private readonly VkCommandPool _pool;
    private readonly CommandBuffer _cmd;

    private readonly VkFence _fence; // signals the previous frame's commands finished
    private readonly VkSemaphore _imageReady; // swapchain image acquired
    private readonly VkSemaphore[] _renderDone; // for presentation

    private readonly ImageAllocationInfo _gridImage; // gpu side, one texel per cell
    private readonly BufferAllocationInfo _staging; // cpu side, copied into _gridImage
    private readonly MappedMemory _mapped;

    public Renderer(Gfx gfx)
    {
        _gfx = gfx;
        var d = gfx.Device;

        _pool = d.CreateCommandPool(
            new VkCommandPoolCreateInfo
            {
                Flags = VkCommandPoolCreateFlags.ResetCommandBufferBit, // will be reseting the buffer every frame
                QueueFamilyIndex = gfx.QueueFamily,
            },
            null
        );

        _cmd = d.AllocateCommandBuffer(
            new VkCommandBufferAllocateInfo
            {
                CommandPool = _pool,
                Level = VkCommandBufferLevel.Primary
            }
        );

        _fence = d.CreateFence(VkFenceCreateFlags.SignaledBit, null);
        _imageReady = d.CreateSemaphore(new VkSemaphoreCreateInfo(), null);
        _renderDone = new VkSemaphore[gfx.SwapImages.Length];
        for (var i = 0; i < _renderDone.Length; i++)
            _renderDone[i] = d.CreateSemaphore(new VkSemaphoreCreateInfo(), null);

        _gridImage = d.AllocateImage(
            "sand grid",
            new VkImageCreateInfo
            {
                ImageType = VkImageType._2D,

                // match swapchain sRGB format so blit doesnt try convert the colours again
                Format = VkFormat.R8G8B8A8SRGB,

                Extent = new VkExtent3D(World.Width, World.Height, 1), // one texel per grid cell
                MipLevels = 1,
                ArrayLayers = 1,
                Samples = VkSampleCountFlags._1Bit,
                Tiling = VkImageTiling.Optimal,
                Usage = VkImageUsageFlags.TransferSrcBit | VkImageUsageFlags.TransferDstBit, // copy in, blit out
                SharingMode = VkSharingMode.Exclusive,
                InitialLayout = VkImageLayout.Undefined,
            },
            new AllocationInfo.CreateInfo { Preference = MemoryPreference.PreferGpu }
        );

        _staging = d.AllocateBuffer(
            "sand staging",
            new VkBufferCreateInfo
            {
                Size = (ByteSize64)(World.Width * World.Height * 4), // 4 bytes per cell (rgba)
                Usage = VkBufferUsageFlags.TransferSrcBit, // only ever source of a copy
                SharingMode = VkSharingMode.Exclusive,
            },
            new AllocationInfo.CreateInfo
            {
                Preference = MemoryPreference.PreferCpu,
                RequiredProperties = VkMemoryPropertyFlags.HostVisibleBit | VkMemoryPropertyFlags.HostCoherentBit, // coherent so no manual flush
            }
        );
        _mapped = _staging.Map(); // stays mapped for the whole lifetime
    }

    public void Draw(World world)
    {
        var d = _gfx.Device;

        // wait for the previous frame so the staging buffer and command buffer are safe to reuse
        d.WaitForFence(_fence, nint.MaxValue);

        var result = d.AcquireNextImageKHR(_gfx.Swapchain, nint.MaxValue, _imageReady, default, out var imageIndex);
        if (result != VkResult.Success && result != VkResult.SuboptimalKHR)
            throw new Exception($"AcquireNextImageKHR failed: {result}");
        d.ResetFences([_fence]);

        world.WriteColors(_mapped.AsSpan<uint>());

        var swapImage = _gfx.SwapImages[imageIndex];
        _cmd.Reset();
        _cmd.Begin(VkCommandBufferUsageFlags.OneTimeSubmitBit);

        // grid image: undefined -> transfer dst
        // old contents can be discarded because every texel is overwritten
        Barrier(
            _gridImage.Image,
            VkImageLayout.Undefined,
            VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags.TopOfPipeBit,
            VkPipelineStageFlags.TransferBit,
            VkAccessFlags.None,
            VkAccessFlags.TransferWriteBit
        );

        // upload: staging buffer -> grid image
        var copy = new VkBufferImageCopy
        {
            ImageSubresource = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 },
            ImageExtent = new VkExtent3D(World.Width, World.Height, 1),
        };
        _cmd.CopyBufferToImage(_staging.VkBuffer, _gridImage.Image, VkImageLayout.TransferDstOptimal, [copy]);

        // grid image: transfer dst -> transfer src
        // wait for the copy's writes to finish before the blit reads this image
        Barrier(
            _gridImage.Image,
            VkImageLayout.TransferDstOptimal,
            VkImageLayout.TransferSrcOptimal,
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.TransferBit,
            VkAccessFlags.TransferWriteBit,
            VkAccessFlags.TransferReadBit
        );

        // swapchain image: undefined -> transfer dst
        // the submit waits at TransferBit, so the acquired swapchain image is ready before the blit
        Barrier(
            swapImage,
            VkImageLayout.Undefined,
            VkImageLayout.TransferDstOptimal,
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.TransferBit,
            VkAccessFlags.None,
            VkAccessFlags.TransferWriteBit
        );

        // scale the grid up to the window
        // nearest to keep cells as crisp squares
        var layers = new VkImageSubresourceLayers { AspectMask = VkImageAspectFlags.ColorBit, LayerCount = 1 };
        var blit = new VkImageBlit { SrcSubresource = layers, DstSubresource = layers };
        blit.SrcOffsets[1] = new VkOffset3D(World.Width, World.Height, 1);
        blit.DstOffsets[1] = new VkOffset3D(_gfx.Extent.Width, _gfx.Extent.Height, 1);
        _cmd.BlitImage(
            _gridImage.Image,
            VkImageLayout.TransferSrcOptimal,
            swapImage,
            VkImageLayout.TransferDstOptimal,
            [blit],
            VkFilter.Nearest
        );

        // swapchain image: transfer dst -> present
        Barrier(
            swapImage,
            VkImageLayout.TransferDstOptimal,
            VkImageLayout.PresentSrcKHR,
            VkPipelineStageFlags.TransferBit,
            VkPipelineStageFlags.BottomOfPipeBit,
            VkAccessFlags.TransferWriteBit,
            VkAccessFlags.None
        );
        _cmd.End();

        // wait for the swapchain image before transferring, signal presentation and the next frame's fence whn done
        Span<VkSemaphore> wait = [_imageReady];
        Span<VkPipelineStageFlags> waitStages = [VkPipelineStageFlags.TransferBit];
        Span<CommandBuffer> cmds = [_cmd];
        Span<VkSemaphore> signal = [_renderDone[imageIndex]];
        _gfx.Queue.Submit(wait, waitStages, cmds, signal, _fence);

        // copied to locals so we can take their addreses
        var swapchain = _gfx.Swapchain;
        var renderDone = _renderDone[imageIndex];
        var index = imageIndex;
        var present = new VkPresentInfoKHR
        {
            WaitSemaphoreCount = 1,
            WaitSemaphores = &renderDone,
            SwapchainCount = 1,
            Swapchains = &swapchain,
            ImageIndices = &index,
        };
        var presentResult = _gfx.Queue.PresentKHR(present);
        if (presentResult != VkResult.Success && presentResult != VkResult.SuboptimalKHR)
            throw new Exception($"PresentKHR failed: {presentResult}");
    }

    // record an image barrier into the commnd buffer
    private void Barrier(
        VkImage image,
        VkImageLayout from,
        VkImageLayout to,
        VkPipelineStageFlags srcStage,
        VkPipelineStageFlags dstStage,
        VkAccessFlags srcAccess,
        VkAccessFlags dstAccess
    )
    {
        var barrier = new VkImageMemoryBarrier
        {
            SrcAccessMask = srcAccess,
            DstAccessMask = dstAccess,
            OldLayout = from,
            NewLayout = to,
            SrcQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED, // no queue ownership transfer
            DstQueueFamilyIndex = VK.QUEUE_FAMILY_IGNORED,
            Image = image,
            SubresourceRange = new VkImageSubresourceRange
            {
                AspectMask = VkImageAspectFlags.ColorBit,
                LevelCount = 1,
                LayerCount = 1,
            },
        };
        _cmd.PipelineBarrier(srcStage, dstStage, VkDependencyFlags.None, [], [], [barrier]);
    }

    public void Dispose()
    {
        var d = _gfx.Device;
        _gfx.Queue.WaitIdle();

        _mapped.Dispose();
        d.FreeAllocation(_staging);
        d.FreeAllocation(_gridImage);

        foreach (var s in _renderDone)
            d.DestroySemaphore(s, null);
        d.DestroySemaphore(_imageReady, null);
        d.DestroyFence(_fence, null);
        d.DestroyCommandPool(_pool, null);

        GC.SuppressFinalize(this);
    }
}