public enum Material : byte
{
    Empty = 0,
    Sand = 1,
    Water = 2,
    Stone = 3,
    Fire = 4,
}

public class World
{
    public const int Width = 640;
    public const int Height = 360;

    private readonly byte[] _cells = new byte[Width * Height]; // row major, index = y * Width + x
    private readonly int[] _risenAt = new int[Width * Height]; // frame a water cell was last pushed up by sand
    private int _frame;

    private const int BlastRadius = 30;
    private const float StoneBreakRadius = BlastRadius * 0.6f;
    private const float FireRadius = BlastRadius * 0.45f;
    private const float DebrisSpeed = 8f;
    private const float UpBias = 2.5f;
    private const int SparkCount = 300;
    private const float Gravity = 0.18f;
    private const float MaxFallSpeed = 6f;
    private const float BombGravity = 0.18f;
    private const float BombMaxFallSpeed = 6f;
    private const int WaterSpread = 8;
    private const int HeatSteps = 45; // how long debris glows
    private const int FireBurnOutChance = 3;
    private const float BombSparkChance = 0.7f; // chance per step a falling bomb gives off a trail spark
    private const float ShakeStrength = 6f;
    private const float ShakeDecay = 0.88f; // shake is multiplied by this every step
    private const float RingSpeed = 4f; // shockwave ring radius growth per step
    private const float MaxRingRadius = BlastRadius * 2.5f;
    private const int FlashSteps = 5; // length of the initial white flash
    private const int MaxBombs = 32;

    private struct Particle
    {
        public float X, Y;
        public float VX, VY;
        public byte Cell; // material it turns back into when lands
        public float Heat; // starting glow (0 to 1)
        public int Age;
        public int Life; // sparks only, steps until they die
        public bool Spark; // visual only
    }

    private struct Blast
    {
        public int X, Y, Age;
    }

    private enum MoveResult { Flying, Landed, OffScreen }

    private Particle[] _particles = new Particle[16384];
    private int _particleCount;
    private readonly Particle[] _bombs = new Particle[MaxBombs];
    private int _bombCount;
    private readonly List<Blast> _blasts = [];

    private float _shake;
    private int _shakeX, _shakeY;
    private uint _explosionRng = 2463534242u; // xorshift32 state

    public void Reset()
    {
        Array.Clear(_cells);
        _particleCount = 0;
        _bombCount = 0;
        _blasts.Clear();
        _shake = 0;
        _shakeX = 0;
        _shakeY = 0;
    }

    // fills a circle of the given material
    public void Paint(int cx, int cy, int radius, Material material)
    {
        // bounding box of the circle (clamped to the grid)
        var yMin = Math.Max(0, cy - radius);
        var yMax = Math.Min(Height - 1, cy + radius);
        var xMin = Math.Max(0, cx - radius);
        var xMax = Math.Min(Width - 1, cx + radius);

        for (var y = yMin; y <= yMax; y++)
        {
            for (var x = xMin; x <= xMax; x++)
            {
                var dx = x - cx;
                var dy = y - cy;

                // inside circle? (pythagoras) 
                if (dx * dx + dy * dy <= radius * radius)
                {
                    _cells[y * Width + x] = (byte)material;
                }
            }
        }
    }

    // stamps circles along a line a fast mouse movment dosnt have gaps
    public void PaintLine(int x0, int y0, int x1, int y1, int radius, Material material)
    {
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Sqrt(dx * dx + dy * dy);

        // stamp every half radius so neighbouring circles overlap (at least 1 cell apart)
        var spacing = MathF.Max(1f, radius * 0.5f);
        var count = Math.Max(1, (int)MathF.Ceiling(length / spacing));

        for (var k = 0; k <= count; k++)
        {
            var t = k / (float)count; // 0 at start, 1 at end
            Paint(x0 + (int)MathF.Round(dx * t), y0 + (int)MathF.Round(dy * t), radius, material); // point = start + delta * t
        }
    }

    // xorshift32 rng
    private static uint Next(ref uint s)
    {
        s ^= s << 13;
        s ^= s >> 17;
        s ^= s << 5;
        return s;
    }

    public void Step()
    {
        _frame++;

        // new rng seed per frame. apparently multiplying by a large odd constant scatters consecutive frame numbers
        var rng = ((uint)_frame * 2654435761u) | 1u;

        // bottom up so falls only moves once per step
        for (var y = Height - 1; y >= 0; y--)
        {
            // random scan direction per row so nothing drifts to one side
            var leftToRight = (Next(ref rng) & 1) == 0;

            for (var i = 0; i < Width; i++)
            {
                var x = leftToRight ? i : Width - 1 - i;

                switch ((Material)_cells[y * Width + x])
                {
                    case Material.Sand:
                        StepSand(x, y, ref rng);
                        break;

                    case Material.Water:
                        StepWater(x, y, ref rng);
                        break;
                }
            }
        }

        // scan top down for rising fire
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                if ((Material)_cells[y * Width + x] == Material.Fire)
                {
                    StepFire(x, y, ref rng);
                }
            }
        }

        StepBombs();
        StepParticles();
        StepBlasts();
        StepShake();
    }

    private bool IsEmpty(int x, int y) => (Material)_cells[y * Width + x] == Material.Empty;
    private bool IsWater(int x, int y) => (Material)_cells[y * Width + x] == Material.Water;

    private void Swap(int ax, int ay, int bx, int by)
    {
        var a = ay * Width + ax;
        var b = by * Width + bx;
        (_cells[a], _cells[b]) = (_cells[b], _cells[a]);
    }

    private bool SandCanEnter(int x, int y)
    {
        var i = y * Width + x;
        var m = (Material)_cells[i];

        // water that was just pushed up this step cant be sunk through again
        if (m == Material.Water) return _risenAt[i] != _frame;

        return m == Material.Empty || m == Material.Fire;
    }

    // moves sand from a to b, handling what's in b (water gets displaced, fire gets put out)
    private void SandSwap(int ax, int ay, int bx, int by, ref uint rng)
    {
        var a = ay * Width + ax;
        var b = by * Width + bx;
        var target = (Material)_cells[b];

        if (target == Material.Water)
        {
            // look for empty space beside the water, random side first
            var dir = (Next(ref rng) & 1) == 0 ? -1 : 1;
            for (var k = 0; k < 2; k++, dir = -dir)
            {
                for (var r = 1; r <= WaterSpread; r++)
                {
                    var nx = bx + dir * r;
                    if (nx < 0 || nx >= Width) break;

                    var m = (Material)_cells[by * Width + nx];
                    if (m == Material.Water) continue; // keep scanning past other water

                    if (m == Material.Empty)
                    {
                        // water jumps to the gap, sand takes its place
                        _cells[by * Width + nx] = (byte)Material.Water;
                        _cells[b] = (byte)Material.Sand;
                        _cells[a] = (byte)Material.Empty;
                        return;
                    }
                    break; // blocked by something solid
                }
            }

            // no sideways space, water can only rise one cell per step
            Swap(ax, ay, bx, by);
            // half the time, lock the risen water for this step (this stops a weird banding artifact on sinking sand)
            if ((Next(ref rng) & 1) == 0) _risenAt[a] = _frame;
            return;
        }

        // sand puts out fire
        var hitFire = target == Material.Fire;
        Swap(ax, ay, bx, by);
        if (hitFire) _cells[a] = (byte)Material.Empty;
    }

    private void StepSand(int x, int y, ref uint rng)
    {
        if (y + 1 >= Height) return; // on the floor

        // straight down
        if (SandCanEnter(x, y + 1))
        {
            SandSwap(x, y, x, y + 1, ref rng);
            return;
        }

        // otherwise try the diagonals, random side first
        var dir = (Next(ref rng) & 1) == 0 ? -1 : 1;
        for (var k = 0; k < 2; k++, dir = -dir)
        {
            var nx = x + dir;
            if (nx < 0 || nx >= Width) continue;

            if (SandCanEnter(nx, y + 1))
            {
                SandSwap(x, y, nx, y + 1, ref rng);
                return;
            }
        }
    }

    private void StepWater(int x, int y, ref uint rng)
    {
        // straight down
        if (y + 1 < Height && IsEmpty(x, y + 1))
        {
            Swap(x, y, x, y + 1);
            return;
        }

        var dir = (Next(ref rng) & 1) == 0 ? -1 : 1;

        // diagonals, random side first
        if (y + 1 < Height)
        {
            var d = dir;
            for (var k = 0; k < 2; k++, d = -d)
            {
                var nx = x + d;
                if (nx >= 0 && nx < Width && IsEmpty(nx, y + 1))
                {
                    Swap(x, y, nx, y + 1);
                    return;
                }
            }
        }

        // only slide sideways when pushed from above or when the move goes over a drop
        // this stops water on flat surfaces jittering so much
        var pushed = y > 0 && !IsEmpty(x, y - 1);

        for (var k = 0; k < 2; k++, dir = -dir)
        {
            // scan outwards, remmbering the furthest cell thats worth moving to
            var target = 0;
            for (var r = 1; r <= WaterSpread; r++)
            {
                var nx = x + dir * r;
                if (nx < 0 || nx >= Width || !IsEmpty(nx, y)) break; // wall or something in the way

                if (pushed || (y + 1 < Height && IsEmpty(nx, y + 1)))
                {
                    target = r;
                }
            }

            if (target > 0)
            {
                Swap(x, y, x + dir * target, y);
                return;
            }
        }
    }

    private void StepFire(int x, int y, ref uint rng)
    {
        // any water next to it puts it out
        if ((x > 0 && IsWater(x - 1, y))
            || (x + 1 < Width && IsWater(x + 1, y))
            || (y > 0 && IsWater(x, y - 1))
            || (y + 1 < Height && IsWater(x, y + 1)))
        {
            _cells[y * Width + x] = (byte)Material.Empty;
            return;
        }

        // random burn out, percent per step
        if (Next(ref rng) % 100 < FireBurnOutChance)
        {
            _cells[y * Width + x] = (byte)Material.Empty;
            return;
        }

        // half the time drift up if is room
        if (y > 0 && (Next(ref rng) & 1) == 0 && IsEmpty(x, y - 1))
        {
            Swap(x, y, x, y - 1);
        }
    }

    // random float in [0, 1)
    // keep 24 random bits (all a float can hold exactly) and divide by 2^24
    private float Rand01() => (Next(ref _explosionRng) >> 8) * (1f / (1 << 24));

    private bool IsPassable(int x, int y)
    {
        var m = (Material)_cells[y * Width + x];
        return m == Material.Empty || m == Material.Fire;
    }

    public void SpawnBomb(int cx, int cy)
    {
        // ingnore clicks outside the grid
        if (cx < 0 || cx >= Width || cy < 0 || cy >= Height) return;
        if (_bombCount == MaxBombs) return;

        _bombs[_bombCount++] = new Particle { X = cx + 0.5f, Y = cy + 0.5f }; // +0.5 = centre of the cell
    }

    // moves a particle by its velocity in sub steps of at most 1 cell
    // prevents fast moving particles from skipping through walls
    private MoveResult Move(ref Particle p, float gravity, float maxFallSpeed)
    {
        p.VY = MathF.Min(p.VY + gravity, maxFallSpeed);

        // enough sub steps that the fastest axis moves at most 1 cell each
        var steps = Math.Max(1, (int)MathF.Ceiling(MathF.Max(MathF.Abs(p.VX), MathF.Abs(p.VY))));
        var dx = p.VX / steps;
        var dy = p.VY / steps;

        for (var i = 0; i < steps; i++)
        {
            var nx = p.X + dx;
            var ny = p.Y + dy;
            var cx = (int)MathF.Floor(nx); // floor so negatives round down too
            var cy = (int)MathF.Floor(ny);

            if (cx < 0 || cx >= Width) return MoveResult.OffScreen;
            if (cy >= Height) return MoveResult.Landed;
            if (cy >= 0 && !IsPassable(cx, cy)) return MoveResult.Landed; // above the top is open air

            p.X = nx;
            p.Y = ny;
        }

        return MoveResult.Flying;
    }

    private void StepBombs()
    {
        for (var i = _bombCount - 1; i >= 0; i--)
        {
            ref var b = ref _bombs[i];
            b.Age++;

            // fuse sparks, 70% chance per step
            if (Rand01() < BombSparkChance)
            {
                AddParticle(new Particle
                {
                    X = b.X,
                    Y = b.Y,
                    VX = (Rand01() - 0.5f) * 1.5f, // sideways drift in [-0.75, 0.75]
                    VY = -Rand01() * 1.5f, // upwards in [-1.5, 0]
                    Spark = true,
                    Life = 8 + (int)(Rand01() * 12),
                });
            }

            var result = Move(ref b, BombGravity, BombMaxFallSpeed);
            if (result == MoveResult.Flying) continue;

            // hit something (or the floor or left the screen), explode where it stopped
            var cx = (int)MathF.Floor(b.X);
            var cy = (int)MathF.Floor(b.Y);
            _bombs[i] = _bombs[--_bombCount]; // swap remove
            Explode(cx, cy);
        }
    }

    private void AddParticle(Particle p)
    {
        if (_particleCount == _particles.Length)
        {
            Array.Resize(ref _particles, _particles.Length * 2);
        }
        _particles[_particleCount++] = p;
    }

    private void Explode(int cx, int cy)
    {
        // bounding box of the blast, clamped to the grid
        var yMin = Math.Max(0, cy - BlastRadius);
        var yMax = Math.Min(Height - 1, cy + BlastRadius);
        var xMin = Math.Max(0, cx - BlastRadius);
        var xMax = Math.Min(Width - 1, cx + BlastRadius);

        // Debris
        // every solid cell in range is removed and turnd into a flying particle
        for (var y = yMin; y <= yMax; y++)
        {
            for (var x = xMin; x <= xMax; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                var d2 = dx * dx + dy * dy; // squred distance from the centre
                if (d2 > BlastRadius * BlastRadius) continue; // outside the circle

                var i = y * Width + x;
                var material = (Material)_cells[i];
                if (material == Material.Empty) continue;
                if (material == Material.Stone && d2 > StoneBreakRadius * StoneBreakRadius) continue; // stone survives far out

                _cells[i] = (byte)Material.Empty;
                if (material == Material.Fire) continue; // fire just disapears

                var dist = MathF.Sqrt(d2);
                var falloff = 1f - dist / BlastRadius; // 1 at the centre, 0 at the edge

                // unit vector pointing away from the centre
                float dirX, dirY;
                if (d2 == 0)
                {
                    // dead centre has no direction (and dist would divide by 0) so pick a random angle
                    var a = Rand01() * MathF.Tau; // TAU IS BETTER THAN PI BTW >:)
                    dirX = MathF.Cos(a);
                    dirY = MathF.Sin(a);
                }
                else
                {
                    dirX = dx / dist;
                    dirY = dy / dist;
                }

                // closer to the centre is faster (with +-40% random variation)
                var speed = 1f + DebrisSpeed * falloff * (0.6f + 0.8f * Rand01());

                // velocity = direction * speed, plus a little sideways jitter in [-0.5, 0.5]
                var vx = dirX * speed + (Rand01() - 0.5f);
                var vy = dirY * speed;
                // flip downward debris upwards so the crater sprays out instead of refilling
                if (vy > 0) vy = -vy * 0.5f;
                vy -= UpBias * (0.4f + 0.6f * falloff); // extra upward kick, stronger near the centre (negtive is up)

                AddParticle(new Particle
                {
                    X = x + 0.5f,
                    Y = y + 0.5f,
                    VX = vx,
                    VY = vy,
                    Cell = (byte)material,
                    Heat = 0.4f + 0.6f * falloff, // closer is hotter
                });
            }
        }

        // Fire
        // most empty cells near the centre catch fire
        for (var y = yMin; y <= yMax; y++)
        {
            for (var x = xMin; x <= xMax; x++)
            {
                var dx = x - cx;
                var dy = y - cy;
                if (dx * dx + dy * dy > FireRadius * FireRadius) continue;

                var i = y * Width + x;
                if (_cells[i] == (byte)Material.Empty && Rand01() < 0.75f)
                {
                    _cells[i] = (byte)Material.Fire;
                }
            }
        }

        // Sparks
        for (var k = 0; k < SparkCount; k++)
        {
            var a = Rand01() * MathF.Tau; // random direction
            var s = 2f + 10f * Rand01() * Rand01(); // multiplying two randoms makes slow sparks far more common than fast
            AddParticle(new Particle
            {
                X = cx + 0.5f,
                Y = cy + 0.5f,
                VX = MathF.Cos(a) * s, // polar to cartesian
                VY = MathF.Sin(a) * s - 1.5f, // -1.5 biases the sparks upwards
                Spark = true,
                Life = 25 + (int)(Rand01() * 35),
            });
        }

        _blasts.Add(new Blast { X = cx, Y = cy });
        _shake = MathF.Max(_shake, ShakeStrength); // dont weaken an ongoing shake
    }

    private void StepParticles()
    {
        // backwrds iteration to not skip swapped in particls
        for (var i = _particleCount - 1; i >= 0; i--)
        {
            ref var p = ref _particles[i];
            p.Age++;

            var result = Move(ref p, Gravity, MaxFallSpeed);

            var dead = result == MoveResult.OffScreen;
            if (p.Spark)
            {
                // sparks die on landing or when their life runs out
                dead |= result == MoveResult.Landed || p.Age >= p.Life;
            }
            else if (result == MoveResult.Landed)
            {
                Settle(p); // debris turns back into a cell
                dead = true;
            }

            if (dead)
            {
                _particles[i] = _particles[--_particleCount]; // swap remove
            }
        }
    }

    // puts a landed particle back into the grid
    private void Settle(in Particle p)
    {
        var x = (int)MathF.Floor(p.X);
        var y = (int)MathF.Floor(p.Y);

        // if the cell is taken go up to the first free one (max 8 cells)
        for (var k = 0; k < 8 && y >= 0; k++, y--)
        {
            if (IsPassable(x, y))
            {
                _cells[y * Width + x] = p.Cell;
                return;
            }
        }
    }

    private void StepBlasts()
    {
        for (var i = _blasts.Count - 1; i >= 0; i--)
        {
            var b = _blasts[i];
            b.Age++;

            // ring radius = age * speed
            if (b.Age * RingSpeed > MaxRingRadius)
            {
                _blasts.RemoveAt(i);
            }
            else
            {
                _blasts[i] = b; // struct copy so write it back
            }
        }
    }

    private void StepShake()
    {
        _shake *= ShakeDecay; // exponential decay
        if (_shake < 0.5f)
        {
            _shake = 0;
            _shakeX = 0;
            _shakeY = 0;
            return;
        }

        // random offset in [-shake, +shake]: Rand01 * 2 - 1 maps [0, 1) to [-1, 1)
        _shakeX = (int)MathF.Round((Rand01() * 2f - 1f) * _shake);
        _shakeY = (int)MathF.Round((Rand01() * 2f - 1f) * _shake);
    }

    // pack a colour into a 32bit pixel (0xAABBGGRR). R, G, B, A in memory ( onlittle endian cpus). matches the gpu  R8G8B8A8 format
    private static uint Rgba(byte r, byte g, byte b) => 0xFF000000u | (uint)b << 16 | (uint)g << 8 | r;

    // indexed by Material
    private static readonly uint[] Colours = [
        Rgba(12, 12, 16),
        Rgba(235, 185, 105),
        Rgba(40, 160, 220),
        Rgba(110, 115, 125),
        Rgba(255, 90, 15),
    ];

    private static readonly uint White = Rgba(255, 255, 255);
    private static readonly uint Yellow = Rgba(255, 245, 170);
    private static readonly uint Orange = Rgba(255, 120, 20);
    private static readonly uint SparkDark = Rgba(90, 15, 5);
    private static readonly uint BombRed = Rgba(255, 50, 40);

    private static uint Lerp(uint a, uint b, float t)
    {
        // pull one channel out of each colour and blend: a * (1 - t) + b * t
        static byte Channel(uint a, uint b, int shift, float t) => (byte)(((a >> shift) & 0xFF) * (1f - t) + ((b >> shift) & 0xFF) * t);

        return Rgba(Channel(a, b, 0, t), Channel(a, b, 8, t), Channel(a, b, 16, t)); // shifts 0, 8, 16 = r, g, b
    }

    // heat 0 to 1: base colour -> orange -> yellow
    private static uint HeatColour(uint baseColour, float heat)
    {
        return heat > 0.5f
            ? Lerp(Orange, Yellow, (heat - 0.5f) * 2f) // top half, remap 0.5..1 to 0..1
            : Lerp(baseColour, Orange, heat * 2f); // bottom half, remap 0..0.5 to 0..1
    }

    // postion based noise in [0, 1) to break up the shockwave ring
    private static float Hash01(int x, int y, int salt)
    {
        // mix the inputs, each multiplied by a diffeerent large prime so the axes don't line up
        var h = (uint)(x * 73856093) ^ (uint)(y * 19349663) ^ (uint)(salt * 83492791);

        // scramble the bits so similar inputs give very different outpts
        h ^= h >> 13;
        h *= 0x85ebca6bu;
        h ^= h >> 16;

        return (h & 0xFFFFFF) * (1f / (1 << 24)); // low 24 bits / 2^24
    }

    // writes a pixel with the screen shake applied, ignoring anything off the grid
    private void Plot(Span<uint> dst, int cellX, int cellY, uint colour)
    {
        var x = cellX - _shakeX;
        var y = cellY - _shakeY;
        if (x < 0 || x >= Width || y < 0 || y >= Height) return;
        dst[y * Width + x] = colour;
    }

    private void DrawBlast(Span<uint> dst, in Blast b)
    {
        // Flash
        // a filled circle that shrinks and cools from white to yellow
        if (b.Age < FlashSteps)
        {
            var t = b.Age / (float)FlashSteps; // 0 to 1 over the flash
            var r = BlastRadius * 0.9f * (1f - t); // radius shrinks to 0
            var colour = Lerp(White, Yellow, t);
            var ri = (int)r + 1; // bounding box half size
            for (var dy = -ri; dy <= ri; dy++)
            {
                for (var dx = -ri; dx <= ri; dx++)
                {
                    if (dx * dx + dy * dy <= r * r) Plot(dst, b.X + dx, b.Y + dy, colour);
                }
            }
        }

        // Shockwave ring
        var ringR = b.Age * RingSpeed; // outer radius
        var fade = ringR / MaxRingRadius; // 0 when new, 1 at the end
        var ringColour = Lerp(White, Orange, fade);
        var inner = MathF.Max(0f, ringR - 2f); // inner radius, so the ring is 2 cells thick
        var ro = (int)ringR + 1; // bounding box half size
        for (var dy = -ro; dy <= ro; dy++)
        {
            for (var dx = -ro; dx <= ro; dx++)
            {
                var d2 = dx * dx + dy * dy;
                if (d2 > ringR * ringR || d2 < inner * inner) continue; // only draw between the two radii
                if (Hash01(b.X + dx, b.Y + dy, b.Age) < fade) continue; // skip more pixels as it ages, so it breaks up
                Plot(dst, b.X + dx, b.Y + dy, ringColour);
            }
        }
    }

    // fills dst with the full frame (grid, then effects on top)
    public void WriteColors(Span<uint> dst)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(dst.Length, Width * Height);

        // Grid
        // each output pixel reads from the cell offset by the shake
        for (var y = 0; y < Height; y++)
        {
            var sy = y + _shakeY;
            for (var x = 0; x < Width; x++)
            {
                var sx = x + _shakeX;
                dst[y * Width + x] = sx >= 0 && sx < Width && sy >= 0 && sy < Height
                    ? Colours[_cells[sy * Width + sx]]
                    : Colours[0]; // shifted off the grid, show empty
            }
        }

        foreach (var b in _blasts)
        {
            DrawBlast(dst, b);
        }

        // Particles
        for (var i = 0; i < _particleCount; i++)
        {
            var p = _particles[i];
            uint colour;
            if (p.Spark)
            {
                colour = HeatColour(SparkDark, 1f - p.Age / (float)p.Life); // cools as it nears the end of its life
            }
            else
            {
                var heat = p.Heat * MathF.Max(0f, 1f - p.Age / (float)HeatSteps); // linear cool down to 0
                colour = HeatColour(Colours[p.Cell], heat);
            }
            Plot(dst, (int)MathF.Floor(p.X), (int)MathF.Floor(p.Y), colour);
        }

        // Bombs
        // 3x3 red square with a centre that blinks every 3 steps (not that you can even see it lol)
        for (var i = 0; i < _bombCount; i++)
        {
            var b = _bombs[i];
            var blink = (b.Age / 3) % 2 == 0;
            var bx = (int)MathF.Floor(b.X);
            var by = (int)MathF.Floor(b.Y);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var centre = dx == 0 && dy == 0;
                    Plot(dst, bx + dx, by + dy, centre ? (blink ? White : Yellow) : BombRed);
                }
            }
        }
    }
}