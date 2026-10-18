using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi;
using Brutal.VulkanApi.Abstractions;
using Brutal.Strings;

public unsafe class Gfx : IDisposable
{
    public GlfwWindow Window { get; }
    public DeviceEx Device { get; }
    public Queue Queue { get; }
    public int QueueFamily { get; }
    public VkSwapchainKHR Swapchain { get; }
    public VkImage[] SwapImages { get; }
    public VkExtent2D Extent { get; }

    private readonly Instance _instance;
    private readonly VkSurfaceKHR _surface;

    public Gfx(string title, int2 size)
    {
        if (!Glfw.Init())
            throw new Exception("Failed to initialize GLFW");
        if (!Glfw.VulkanSupported())
            throw new Exception("Vulkan not supported");

        // Window
        Glfw.WindowHint(GlfwWindowHint.ClientApi, 0);
        Glfw.WindowHint(GlfwWindowHint.Resizable, 0);
        Window = Glfw.CreateWindow(new GlfwWindow.CreateInfo { Title = title, Size = size });

        // Instance
        var extensions = new HashSet<string>();

        var glfwExts = Glfw.GetRequiredInstanceExtensions(out uint count);
        for (var i = 0; i < count; i++)
        {
            extensions.Add(Utf8.GetString(Utf8.GetNullTerminatedSpan(glfwExts[i])));
        }
        VulkanHelpers.AddSurfaceExtensions(extensions);
        VulkanHelpers.AddPlatformRequiredExtensions(extensions);

        var layers = new HashSet<string>();
#if DEBUG
        VulkanHelpers.AddValidationLayers(layers);
#endif

        _instance = Vulkan.CreateInstance(new Instance.CreateInfo
        {
            AppInfo = new VkApplicationInfo { ApiVersion = (int)VulkanHelpers.Api.VERSION_1_3 },
            Flags = VulkanHelpers.GetPlatformRequiredInstanceCreateFlags(),
            EnabledExtensions = extensions,
            EnabledLayers = layers,
        });

        Glfw.CreateWindowSurface(_instance.VkHandle, Window, out var surfaceRaw);
        _surface = new VkSurfaceKHR((nint)surfaceRaw);

        // Physical device
        // prefer a discrete gpu, otherwise fall back to the first one
        var physicalDevice = _instance.PhysicalDevices[0];
        foreach (var pd in _instance.PhysicalDevices)
        {
            if (pd.GetProperties().DeviceType == VkPhysicalDeviceType.DiscreteGpu)
            {
                physicalDevice = pd;
                break;
            }
        }
        Console.WriteLine($"GPU: {physicalDevice.GetDeviceName()}");

        // pick the first queue family that can do graphics and present to the surface
        var familyCount = physicalDevice.GetQueueFamilyProperties_Count();
        Span<VkQueueFamilyProperties> families = stackalloc VkQueueFamilyProperties[familyCount];
        physicalDevice.GetQueueFamilyProperties_Write(families);

        var queueFamily = -1;
        for (var i = 0; i < familyCount; i++)
        {
            if (families[i].QueueFlags.HasFlag(VkQueueFlags.GraphicsBit) && physicalDevice.GetSurfaceSupportKHR(i, _surface))
            {
                queueFamily = i;
                break;
            }
        }
        QueueFamily = queueFamily;

        // Logical device
        var deviceExtensions = new HashSet<string>();
        VulkanHelpers.AddSwapchainExtensions(deviceExtensions);
        VulkanHelpers.AddMandatoryIfSupportedDeviceExtensions(deviceExtensions, physicalDevice);

        float priority = 1f;
        var queueInfo = new VkDeviceQueueCreateInfo
        {
            QueueFamilyIndex = queueFamily,
            QueueCount = 1,
            QueuePriorities = &priority,
        };
        Device = physicalDevice.CreateDevice(new DeviceEx.CreateInfo
        {
            Extensions = deviceExtensions,
            QueueCreateInfos = new ReadOnlySpan<VkDeviceQueueCreateInfo>(in queueInfo),
        });
        Queue = Device.GetQueue(queueFamily, 0);

        // Swapchain
        var caps = physicalDevice.GetSurfaceCapabilitiesKHR(_surface);

        var formatCount = physicalDevice.GetSurfaceFormatsKHR_Count(_surface);
        Span<VkSurfaceFormatKHR> formats = stackalloc VkSurfaceFormatKHR[formatCount];
        physicalDevice.GetSurfaceFormatsKHR_Write(_surface, formats);

        Extent = caps.CurrentExtent;

        // one more than the minimum
        var imageCount = caps.MinImageCount + 1;
        if (caps.MaxImageCount > 0 && imageCount > caps.MaxImageCount)
        {
            imageCount = caps.MaxImageCount;
        }

        Swapchain = Device.CreateSwapchainKHR(new VkSwapchainCreateInfoKHR
        {
            Surface = _surface,
            MinImageCount = imageCount,
            ImageFormat = VkFormat.B8G8R8A8SRGB,
            ImageColorSpace = VkColorSpaceKHR.SRGBNonlinearKHR,
            ImageExtent = Extent,
            ImageArrayLayers = 1,

            // TransferDst so the renderer can blit the sand straight into the swapchain image
            ImageUsage = VkImageUsageFlags.ColorAttachmentBit | VkImageUsageFlags.TransferDstBit,

            ImageSharingMode = VkSharingMode.Exclusive,
            PreTransform = caps.CurrentTransform,
            CompositeAlpha = VkCompositeAlphaFlagsKHR.OpaqueBitKHR,

            PresentMode = VkPresentModeKHR.FifoKHR, // vsync
            //PresentMode = VkPresentModeKHR.ImmediateKHR,

            Clipped = true,
        }, null);

        var swapCount = Device.GetSwapchainImagesKHR_Count(Swapchain);
        SwapImages = new VkImage[swapCount];
        Device.GetSwapchainImagesKHR_Write(Swapchain, SwapImages);
        Console.WriteLine($"Swapchain: {swapCount} images, {Extent.Width}x{Extent.Height}");
    }

    public void Dispose()
    {
        Queue.WaitIdle();
        Device.DestroySwapchainKHR(Swapchain, null);
        Device.Dispose();
        _instance.DestroySurfaceKHR(_surface, null);
        _instance.Dispose();
        Glfw.Terminate();

        GC.SuppressFinalize(this);
    }
}