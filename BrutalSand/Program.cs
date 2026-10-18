using Brutal.GlfwApi;
using Brutal.Numerics;
using Brutal.VulkanApi.Abstractions;
using System.Diagnostics;

const int CellSize = 2; // screen pixels per grid cell
using var gfx = new Gfx("Falling Sand Test", new int2(World.Width * CellSize, World.Height * CellSize));
using var renderer = new Renderer(gfx);

var world = new World();

var window = gfx.Window;
var input = new Input(window);

var material = Material.Sand;
var radius = 4;
var radiusStepSize = 1;

const double StepSeconds = 1.0 / 120.0;
const int MaxStepsPerFrame = 5;

var clock = Stopwatch.StartNew();
var lastTime = clock.Elapsed.TotalSeconds;
var accumulator = 0.0;

var stepTimer = new Stopwatch();
var report = Stopwatch.StartNew();
const float LogFrequencySeconds = 1f;
var frames = 0;
var steps = 0;
var stepMsTotal = 0.0;
var droppedFrames = 0;

var lastX = 0;
var lastY = 0;
var wasPainting = false;

while (!window.ShouldClose)
{
    input.NewFrame();
    Glfw.PollEvents();

    if (input.WasPressed(GlfwKey.Escape)) window.ShouldClose = true;

    if (input.WasPressed(GlfwKey.Number1)) material = Material.Sand;
    if (input.WasPressed(GlfwKey.Number2)) material = Material.Water;
    if (input.WasPressed(GlfwKey.Number3)) material = Material.Stone;
    if (input.WasPressed(GlfwKey.Number4)) material = Material.Empty;

    if (input.WasPressed(GlfwKey.R)) world.Reset();

    if (input.WasPressed(GlfwKey.Comma)) radius = Math.Max(1, radius - radiusStepSize);
    if (input.WasPressed(GlfwKey.Period)) radius = Math.Min(30, radius + radiusStepSize);

    // convert window pixels to grid cell
    var cursor = window.CursorPos;
    var cellX = (int)(cursor.X / CellSize);
    var cellY = (int)(cursor.Y / CellSize);

    if (input.WasPressed(GlfwMouseButton.Right))
    {
        world.SpawnBomb(cellX, cellY);
    }

    var painting = window.GetMouseButton(GlfwMouseButton.Left) == GlfwButtonAction.Press;

    // when a stroke starts begin at cursor so theres no line from the old position
    if (!painting || !wasPainting)
    {
        lastX = cellX;
        lastY = cellY;
    }
    wasPainting = painting;

    var now = clock.Elapsed.TotalSeconds;
    accumulator += now - lastTime;
    lastTime = now;

    // run as many fixed steps as the elapsed time covers
    var stepsThisFrame = 0;
    while (accumulator >= StepSeconds && stepsThisFrame < MaxStepsPerFrame)
    {
        // join the last cursor position to current one so fast mouse moves dosnt have gaps
        if (painting)
        {
            world.PaintLine(lastX, lastY, cellX, cellY, radius, material);
            lastX = cellX;
            lastY = cellY;
        }

        stepTimer.Restart();
        world.Step();
        stepMsTotal += stepTimer.Elapsed.TotalMilliseconds;

        accumulator -= StepSeconds;
        stepsThisFrame++;
        steps++;
    }
    if (accumulator >= StepSeconds) // hit cap and still behind
    {
        accumulator = 0; // give up the backlog
        droppedFrames++;
    }

    renderer.Draw(world);
#if DEBUG
    ReportLogs();
#endif
}

void ReportLogs()
{
    frames++;

    if (report.Elapsed.TotalSeconds < LogFrequencySeconds) return;

    var msPerStep = stepMsTotal / Math.Max(1, steps);

    Console.WriteLine($"{frames} FPS | {steps} steps/s | {msPerStep:F2} ms/step | Brush: {material} ({radius}px)");

    if (droppedFrames > 0)
    {
        Console.WriteLine($"WARNING: Simulation fell behind on {droppedFrames} of {frames} frames");
    }

    frames = 0;
    steps = 0;
    stepMsTotal = 0;
    droppedFrames = 0;
    report.Restart();
}