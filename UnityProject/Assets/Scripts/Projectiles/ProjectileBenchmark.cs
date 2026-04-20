// ProjectileBenchmark.cs
// Rust native lib + Burst/Jobs side-by-side comparison.
// Includes continuous fire bench (sustained N-frame simulation).
//
// Runtime:  press B while in Play mode.
// Editor:   Window > MID > Projectile Benchmark
//
// Burst jobs are warmed up on Awake — JIT cost is excluded from all timings.
// Requires: com.unity.burst, com.unity.collections, com.unity.mathematics

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.Burst;
using Unity.Collections;
using Unity.Jobs;
using Unity.Mathematics;
using UnityEngine;
using Debug = UnityEngine.Debug;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace MidManStudio.Projectiles
{
    // ─────────────────────────────────────────────────────────────────────────
    //  Result containers
    // ─────────────────────────────────────────────────────────────────────────

    [Serializable]
    public struct BenchResult
    {
        public string backend;
        public double totalMs;
        public double perOpUs;   // µs per operation / iteration
        public int    opCount;
        public bool   passed;
        public string note;

        public bool HasData  => totalMs > 0;
        public string Summary => HasData
            ? $"{totalMs:F2}ms  ({perOpUs:F1} µs/op)  {note}"
            : "not run";
    }

    [Serializable]
    public struct BenchPair
    {
        public BenchResult rust;
        public BenchResult burst;

        public bool   AnyData      => rust.HasData || burst.HasData;
        public bool   BothRan      => rust.HasData && burst.HasData;
        public bool   BurstWins    => BothRan && burst.totalMs < rust.totalMs;
        public double SpeedupRatio => BothRan && burst.totalMs > 0
                                      ? rust.totalMs / burst.totalMs : 0.0;
    }

    [Serializable]
    public struct ContFireResult
    {
        public double totalMs;
        public double avgFrameMs;
        public double peakFrameMs;
        public double minFrameMs;
        public int    framesRun;
        public int    totalSpawned;
        public int    peakActive;
        public int    steadyActive; // active count at last frame (approximate steady state)
        public bool   HasData => totalMs > 0;
    }

    [Serializable]
    public struct ContFirePair
    {
        public ContFireResult rust;
        public ContFireResult burst;
        public bool BothRan       => rust.HasData && burst.HasData;
        public bool BurstWinsAvg  => BothRan && burst.avgFrameMs < rust.avgFrameMs;
        public double AvgSpeedup  => BothRan && burst.avgFrameMs > 0
                                     ? rust.avgFrameMs / burst.avgFrameMs : 0.0;
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Burst-compiled jobs  (straight movement only — matches Rust MOVE_STRAIGHT)
    // ─────────────────────────────────────────────────────────────────────────

    [BurstCompile(CompileSynchronously = false)]
    struct BurstTickJob : IJobParallelFor
    {
        public NativeArray<NativeProjectile> Projectiles;
        public float DeltaTime;

        public void Execute(int i)
        {
            var p = Projectiles[i];
            if (p.Alive == 0) return;

            p.Lifetime -= DeltaTime;
            if (p.Lifetime <= 0f) { p.Alive = 0; Projectiles[i] = p; return; }

            p.Vx += p.Ax * DeltaTime;
            p.Vy += p.Ay * DeltaTime;
            p.X  += p.Vx * DeltaTime;
            p.Y  += p.Vy * DeltaTime;

            if (p.Vx != 0f || p.Vy != 0f)
                p.AngleDeg = math.degrees(math.atan2(p.Vy, p.Vx));

            float dx = p.Vx * DeltaTime;
            float dy = p.Vy * DeltaTime;
            p.TravelDist += math.sqrt(dx * dx + dy * dy);

            Projectiles[i] = p;
        }
    }

    // Single-threaded for an apples-to-apples collision comparison with Rust.
    // Same O(P×T) brute-force — spatial grid is the next step for both backends.
    [BurstCompile(CompileSynchronously = false)]
    struct BurstCollisionJob : IJob
    {
        [ReadOnly] public NativeArray<NativeProjectile> Projectiles;
        [ReadOnly] public NativeArray<CollisionTarget>  Targets;
        public NativeArray<HitResult> OutHits;
        public NativeArray<int>       HitCountOut; // [0] receives result

        public void Execute()
        {
            int hits = 0, maxH = OutHits.Length;

            for (int pi = 0; pi < Projectiles.Length && hits < maxH; pi++)
            {
                var p = Projectiles[pi];
                if (p.Alive == 0) continue;

                for (int ti = 0; ti < Targets.Length; ti++)
                {
                    var t = Targets[ti];
                    if (t.Active == 0) continue;

                    float dx = p.X - t.X, dy = p.Y - t.Y;
                    float r  = p.ScaleX * 0.5f + t.Radius;

                    if (dx * dx + dy * dy > r * r) continue;

                    OutHits[hits++] = new HitResult
                    {
                        ProjId = p.ProjId, ProjIndex = (uint)pi,
                        TargetId = t.TargetId, TravelDist = p.TravelDist,
                        HitX = p.X, HitY = p.Y,
                    };
                    break;
                }
            }
            HitCountOut[0] = hits;
        }
    }

    // Burst spawn: direct struct init, no FFI.
    // Comparing against Rust spawn isolates FFI boundary cost.
    [BurstCompile(CompileSynchronously = false)]
    struct BurstSpawnJob : IJobParallelFor
    {
        [WriteOnly] public NativeArray<NativeProjectile> Out;
        public float   Speed;
        public ushort  ConfigId;
        public ushort  OwnerId;
        public uint    BaseProjId;
        public float   Lifetime;

        public void Execute(int i)
        {
            float angle = math.radians(i * (360f / Out.Length));
            Out[i] = new NativeProjectile
            {
                X = 0f, Y = 0f,
                Vx = math.cos(angle) * Speed,
                Vy = math.sin(angle) * Speed,
                Lifetime    = Lifetime, MaxLifetime = Lifetime,
                ScaleX      = 0.2f, ScaleY = 0.2f,
                ScaleTarget = 0.2f,
                ConfigId    = ConfigId, OwnerId = OwnerId,
                ProjId      = BaseProjId + (uint)i,
                Alive       = 1,
            };
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Runtime MonoBehaviour
    // ─────────────────────────────────────────────────────────────────────────

    public class ProjectileBenchmark : MonoBehaviour
    {
        // ── Config ──────────────────────────────────────────────────────────

        [Header("Spawn")]
        [SerializeField] int   _spawnCount  = 2000;
        [SerializeField] ushort _configId   = 0;

        [Header("Tick")]
        [SerializeField] int _tickIter      = 1000;
        [SerializeField] int _tickProjCount = 2048;

        [Header("Collision")]
        [SerializeField] int _colIter        = 500;
        [SerializeField] int _colProjCount   = 2048;
        [SerializeField] int _colTargetCount = 64;
        [SerializeField] int _colHitMax      = 512;

        [Header("Save / Restore")]
        [SerializeField] int _srCount = 2048;

        [Header("Continuous Fire")]
        [Tooltip("Number of simulated frames")]
        [SerializeField] int   _cfFrames       = 500;
        [Tooltip("Projectiles spawned per frame")]
        [SerializeField] int   _cfSpawnPerFrame = 4;
        [Tooltip("Simulated fixed delta time")]
        [SerializeField] float _cfDt            = 0.02f;
        [Tooltip("Projectile lifetime (seconds) — controls steady-state active count)")]
        [SerializeField] float _cfLifetime      = 3.0f;
        [SerializeField] int   _cfMaxSlots      = 2048;
        [Tooltip("Number of collision targets during continuous fire")]
        [SerializeField] int   _cfTargetCount   = 16;

        // ── Stored results (visible in Inspector + EditorWindow) ────────────

        [Header("Results")]
        public BenchPair    SpawnResults;
        public BenchPair    TickResults;
        public BenchPair    CollisionResults;
        public BenchPair    SaveRestoreResults;
        public ContFirePair ContFireResults;

        // ── Internal ────────────────────────────────────────────────────────

        ProjectileManager _mgr;
        bool _burstReady;

        // ── Lifecycle ────────────────────────────────────────────────────────

        void Awake()
        {
            _mgr = GetComponent<ProjectileManager>() ?? FindObjectOfType<ProjectileManager>();
            WarmUpBurst();
        }

        void Update()
        {
            if (Input.GetKeyDown(KeyCode.B)) RunAll();
        }

        // ── Public run API (called by EditorWindow buttons too) ──────────────

        public void RunAll()
        {
            if (!ProjectileLib.ValidateStructSizes())
            {
                Debug.LogError("[Bench] Struct size mismatch — aborting.");
                return;
            }
            Debug.Log("=== Benchmark START ===");
            RunSpawn();
            RunTick();
            RunCollision();
            RunSaveRestore();
            RunContFire();
            LogSummary();
            Debug.Log("=== Benchmark END ===");
        }

        public void RunSpawn()
        {
            SpawnResults.rust  = BenchSpawnRust();
            SpawnResults.burst = BenchSpawnBurst();
        }

        public void RunTick()
        {
            TickResults.rust  = BenchTickRust();
            TickResults.burst = BenchTickBurst();
        }

        public void RunCollision()
        {
            CollisionResults.rust  = BenchCollisionRust();
            CollisionResults.burst = BenchCollisionBurst();
        }

        public void RunSaveRestore()
        {
            SaveRestoreResults.rust  = BenchSaveRestoreRust();
            SaveRestoreResults.burst = BenchSaveRestoreBurst();
        }

        public void RunContFire()
        {
            ContFireResults.rust  = BenchContFireRust();
            ContFireResults.burst = BenchContFireBurst();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Spawn
        // ─────────────────────────────────────────────────────────────────────

        BenchResult BenchSpawnRust()
        {
            if (_mgr == null) return Fail("Rust", "no ProjectileManager in scene");
            int n = _spawnCount;
            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                _mgr.Spawn(_configId, new Vector2(i * 0.001f, 0f), 0f, 10f, 0f, 0, (uint)i);
            sw.Stop();
            return Make("Rust", n, sw, $"active={_mgr.ActiveCount}/{_mgr.MaxProjectiles}");
        }

        BenchResult BenchSpawnBurst()
        {
            // Direct struct init via a Burst parallel job — no FFI call.
            // Delta vs Rust result = FFI boundary overhead per spawn.
            int n = _spawnCount;
            var arr = new NativeArray<NativeProjectile>(n, Allocator.TempJob);
            var sw  = Stopwatch.StartNew();
            new BurstSpawnJob
            {
                Out = arr, Speed = 10f, ConfigId = _configId,
                OwnerId = 0, BaseProjId = 0, Lifetime = 5f,
            }.Schedule(n, 64).Complete();
            sw.Stop();
            arr.Dispose();
            return Make("Burst", n, sw, "no FFI — struct init only");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Tick
        // ─────────────────────────────────────────────────────────────────────

        BenchResult BenchTickRust()
        {
            int n = _tickIter, count = Mathf.Min(_tickProjCount, 2048);
            float dt = Time.fixedDeltaTime;

            var projs  = new NativeProjectile[count];
            var handle = GCHandle.Alloc(projs, GCHandleType.Pinned);
            FillManaged(projs, count);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                ProjectileLib.tick_projectiles(handle.AddrOfPinnedObject(), count, dt);
            sw.Stop();
            handle.Free();

            float budget = Time.fixedDeltaTime * 1000f;
            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            return Make("Rust", n, sw,
                $"{count} projs/tick  {(usPerTick < budget * 1000f ? "✓ within budget" : "⚠ over budget")}");
        }

        BenchResult BenchTickBurst()
        {
            int n = _tickIter, count = Mathf.Min(_tickProjCount, 2048);
            float dt = Time.fixedDeltaTime;

            var arr = new NativeArray<NativeProjectile>(count, Allocator.Persistent);
            FillNative(arr, count);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                new BurstTickJob { Projectiles = arr, DeltaTime = dt }
                    .Schedule(count, 64).Complete();
            sw.Stop();
            arr.Dispose();

            float budget = Time.fixedDeltaTime * 1000f;
            double usPerTick = sw.Elapsed.TotalMilliseconds * 1000.0 / n;
            return Make("Burst", n, sw,
                $"{count} projs/tick  SIMD parallel  {(usPerTick < budget * 1000f ? "✓" : "⚠")}");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Collision
        // ─────────────────────────────────────────────────────────────────────

        BenchResult BenchCollisionRust()
        {
            int n = _colIter, pc = _colProjCount, tc = _colTargetCount;

            var projs   = new NativeProjectile[pc];
            var targets = new CollisionTarget[tc];
            var hits    = new HitResult[_colHitMax];
            FillManaged(projs, pc);
            FillTargetsManaged(targets, tc);

            var ph = GCHandle.Alloc(projs,   GCHandleType.Pinned);
            var th = GCHandle.Alloc(targets, GCHandleType.Pinned);
            var hh = GCHandle.Alloc(hits,    GCHandleType.Pinned);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
                ProjectileLib.check_hits_grid(
                    ph.AddrOfPinnedObject(), pc,
                    th.AddrOfPinnedObject(), tc,
                    hh.AddrOfPinnedObject(), _colHitMax, out int _);
            sw.Stop();

            ph.Free(); th.Free(); hh.Free();
            return Make("Rust", n, sw, $"{pc}p × {tc}t  O(P×T)  grid=TODO");
        }

        BenchResult BenchCollisionBurst()
        {
            int n = _colIter, pc = _colProjCount, tc = _colTargetCount;

            var projs   = new NativeArray<NativeProjectile>(pc, Allocator.Persistent);
            var targets = new NativeArray<CollisionTarget>(tc,   Allocator.TempJob);
            var hits    = new NativeArray<HitResult>(_colHitMax, Allocator.TempJob);
            var hitCnt  = new NativeArray<int>(1,                Allocator.TempJob);
            FillNative(projs, pc);
            FillTargetsNative(targets, tc);

            var sw = Stopwatch.StartNew();
            for (int i = 0; i < n; i++)
            {
                hitCnt[0] = 0;
                new BurstCollisionJob
                {
                    Projectiles = projs, Targets = targets,
                    OutHits = hits, HitCountOut = hitCnt,
                }.Schedule().Complete();
            }
            sw.Stop();

            projs.Dispose(); targets.Dispose(); hits.Dispose(); hitCnt.Dispose();
            return Make("Burst", n, sw, $"{pc}p × {tc}t  O(P×T)  grid=TODO");
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Save / Restore
        // ─────────────────────────────────────────────────────────────────────

        BenchResult BenchSaveRestoreRust()
        {
            int count = _srCount, bufBytes = count * 72;

            var projs    = new NativeProjectile[count];
            var buf      = new byte[bufBytes];
            var restored = new NativeProjectile[count];

            for (int i = 0; i < count; i++) { projs[i].X = i; projs[i].Alive = 1; projs[i].ProjId = (uint)i; }

            var ph = GCHandle.Alloc(projs,    GCHandleType.Pinned);
            var bh = GCHandle.Alloc(buf,      GCHandleType.Pinned);
            var rh = GCHandle.Alloc(restored, GCHandleType.Pinned);

            var sw = Stopwatch.StartNew();
            int written = ProjectileLib.save_state(ph.AddrOfPinnedObject(), count, bh.AddrOfPinnedObject(), bufBytes);
            ProjectileLib.restore_state(rh.AddrOfPinnedObject(), count, bh.AddrOfPinnedObject(), written, out int restoredCount);
            sw.Stop();

            bool ok = restoredCount == count;
            for (int i = 0; i < count && ok; i++)
                if (restored[i].ProjId != projs[i].ProjId) ok = false;

            ph.Free(); bh.Free(); rh.Free();

            return new BenchResult
            {
                backend = "Rust", totalMs = sw.Elapsed.TotalMilliseconds,
                perOpUs = sw.Elapsed.TotalMilliseconds * 1000.0 / count,
                opCount = count, passed = ok,
                note    = $"written={written}B  fidelity={(ok ? "PASS" : "FAIL")}",
            };
        }

        BenchResult BenchSaveRestoreBurst()
        {
            // Two NativeArray.Copy calls = save + restore (both are memcpy).
            // Comparing vs Rust gives us pure FFI overhead for this path.
            int count = _srCount;

            var src      = new NativeArray<NativeProjectile>(count, Allocator.TempJob);
            var snapshot = new NativeArray<NativeProjectile>(count, Allocator.TempJob);
            var restored = new NativeArray<NativeProjectile>(count, Allocator.TempJob);

            for (int i = 0; i < count; i++)
                src[i] = new NativeProjectile { X = i, Alive = 1, ProjId = (uint)i };

            var sw = Stopwatch.StartNew();
            NativeArray<NativeProjectile>.Copy(src, snapshot, count); // save
            NativeArray<NativeProjectile>.Copy(snapshot, restored, count); // restore
            sw.Stop();

            bool ok = true;
            for (int i = 0; i < count && ok; i++)
                if (restored[i].ProjId != src[i].ProjId) ok = false;

            src.Dispose(); snapshot.Dispose(); restored.Dispose();

            return new BenchResult
            {
                backend = "Burst", totalMs = sw.Elapsed.TotalMilliseconds,
                perOpUs = sw.Elapsed.TotalMilliseconds * 1000.0 / count,
                opCount = count, passed = ok,
                note    = $"written={count * 72}B  fidelity={(ok ? "PASS" : "FAIL")}  no FFI",
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Continuous Fire — simulates N frames of sustained combat
        //
        //  Each frame: spawn _cfSpawnPerFrame projectiles → tick all alive →
        //  collision check against _cfTargetCount targets → compact dead slots.
        //  Projectiles expire after _cfLifetime seconds, so the system reaches
        //  a steady state where spawns ≈ deaths.  Peak active count and per-frame
        //  timing reveal the real budget impact of your weapon fire rates.
        // ─────────────────────────────────────────────────────────────────────

        ContFireResult BenchContFireRust()
        {
            int maxSlots = _cfMaxSlots, numFrames = _cfFrames;
            int spawnPF  = _cfSpawnPerFrame, tc = _cfTargetCount;
            float dt = _cfDt, lifetime = _cfLifetime;

            var projs   = new NativeProjectile[maxSlots];
            var targets = new CollisionTarget[tc];
            var hits    = new HitResult[256];
            FillTargetsManaged(targets, tc);

            var ph = GCHandle.Alloc(projs,   GCHandleType.Pinned);
            var th = GCHandle.Alloc(targets, GCHandleType.Pinned);
            var hh = GCHandle.Alloc(hits,    GCHandleType.Pinned);

            IntPtr pp = ph.AddrOfPinnedObject(), tp = th.AddrOfPinnedObject(),
                   hp = hh.AddrOfPinnedObject();

            int activeCount = 0, totalSpawned = 0, peakActive = 0;
            uint nextId = 1;
            double totalMs = 0, peakMs = 0, minMs = double.MaxValue;
            var sw = Stopwatch.StartNew();

            for (int frame = 0; frame < numFrames; frame++)
            {
                long t0 = sw.ElapsedTicks;

                for (int s = 0; s < spawnPF && activeCount < maxSlots; s++)
                {
                    projs[activeCount++] = MakeTestProjectile(nextId++, lifetime);
                    totalSpawned++;
                }

                ProjectileLib.tick_projectiles(pp, activeCount, dt);
                ProjectileLib.check_hits_grid(pp, activeCount, tp, tc, hp, 256, out int _);
                CompactManaged(projs, ref activeCount);

                if (activeCount > peakActive) peakActive = activeCount;
                double fMs = ElapsedMs(sw.ElapsedTicks - t0);
                totalMs += fMs;
                if (fMs > peakMs) peakMs = fMs;
                if (fMs < minMs)  minMs  = fMs;
            }

            ph.Free(); th.Free(); hh.Free();

            return new ContFireResult
            {
                totalMs = totalMs, avgFrameMs = totalMs / numFrames,
                peakFrameMs = peakMs, minFrameMs = minMs,
                framesRun = numFrames, totalSpawned = totalSpawned,
                peakActive = peakActive, steadyActive = activeCount,
            };
        }

        ContFireResult BenchContFireBurst()
        {
            int maxSlots = _cfMaxSlots, numFrames = _cfFrames;
            int spawnPF  = _cfSpawnPerFrame, tc = _cfTargetCount;
            float dt = _cfDt, lifetime = _cfLifetime;

            var projs   = new NativeArray<NativeProjectile>(maxSlots, Allocator.Persistent);
            var targets = new NativeArray<CollisionTarget>(tc,         Allocator.TempJob);
            var hits    = new NativeArray<HitResult>(256,              Allocator.TempJob);
            var hitCnt  = new NativeArray<int>(1,                      Allocator.TempJob);
            FillTargetsNative(targets, tc);

            int activeCount = 0, totalSpawned = 0, peakActive = 0;
            uint nextId = 1;
            double totalMs = 0, peakMs = 0, minMs = double.MaxValue;
            var sw = Stopwatch.StartNew();

            for (int frame = 0; frame < numFrames; frame++)
            {
                long t0 = sw.ElapsedTicks;

                for (int s = 0; s < spawnPF && activeCount < maxSlots; s++)
                {
                    projs[activeCount++] = MakeTestProjectile(nextId++, lifetime);
                    totalSpawned++;
                }

                new BurstTickJob { Projectiles = projs, DeltaTime = dt }
                    .Schedule(activeCount, 64).Complete();

                hitCnt[0] = 0;
                new BurstCollisionJob
                {
                    Projectiles = projs, Targets = targets,
                    OutHits = hits, HitCountOut = hitCnt,
                }.Schedule().Complete();

                CompactNative(projs, ref activeCount);

                if (activeCount > peakActive) peakActive = activeCount;
                double fMs = ElapsedMs(sw.ElapsedTicks - t0);
                totalMs += fMs;
                if (fMs > peakMs) peakMs = fMs;
                if (fMs < minMs)  minMs  = fMs;
            }

            projs.Dispose(); targets.Dispose(); hits.Dispose(); hitCnt.Dispose();

            return new ContFireResult
            {
                totalMs = totalMs, avgFrameMs = totalMs / numFrames,
                peakFrameMs = peakMs, minFrameMs = minMs,
                framesRun = numFrames, totalSpawned = totalSpawned,
                peakActive = peakActive, steadyActive = activeCount,
            };
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────

        static BenchResult Make(string backend, int n, Stopwatch sw, string note = "")
        {
            double ms = sw.Elapsed.TotalMilliseconds;
            return new BenchResult { backend = backend, totalMs = ms,
                perOpUs = ms * 1000.0 / n, opCount = n, passed = true, note = note };
        }

        static BenchResult Fail(string backend, string reason) =>
            new BenchResult { backend = backend, note = "ERR: " + reason };

        static NativeProjectile MakeTestProjectile(uint id, float lifetime) =>
            new NativeProjectile
            {
                X = 0f, Y = 0f, Vx = 10f,
                Lifetime = lifetime, MaxLifetime = lifetime,
                ScaleX = 0.2f, ScaleY = 0.2f,
                Alive = 1, ProjId = id,
            };

        static void FillManaged(NativeProjectile[] arr, int count)
        {
            for (int i = 0; i < count; i++)
                arr[i] = new NativeProjectile { X = i * 0.01f, Vx = 10f,
                    Lifetime = 10f, MaxLifetime = 10f,
                    ScaleX = 0.2f, ScaleY = 0.2f, Alive = 1 };
        }

        static void FillNative(NativeArray<NativeProjectile> arr, int count)
        {
            for (int i = 0; i < count; i++)
                arr[i] = new NativeProjectile { X = i * 0.01f, Vx = 10f,
                    Lifetime = 10f, MaxLifetime = 10f,
                    ScaleX = 0.2f, ScaleY = 0.2f, Alive = 1 };
        }

        static void FillTargetsManaged(CollisionTarget[] arr, int count)
        {
            for (int i = 0; i < count; i++)
                arr[i] = new CollisionTarget { X = i * 10f, Radius = 1f,
                    TargetId = (uint)i, Active = 1 };
        }

        static void FillTargetsNative(NativeArray<CollisionTarget> arr, int count)
        {
            for (int i = 0; i < count; i++)
                arr[i] = new CollisionTarget { X = i * 10f, Radius = 1f,
                    TargetId = (uint)i, Active = 1 };
        }

        // Swap-compact managed array.  O(n) single pass.
        static void CompactManaged(NativeProjectile[] arr, ref int count)
        {
            int i = 0;
            while (i < count)
            {
                if (arr[i].Alive == 0) { count--; if (i < count) arr[i] = arr[count]; }
                else i++;
            }
        }

        // Same swap-compact for NativeArray.
        static void CompactNative(NativeArray<NativeProjectile> arr, ref int count)
        {
            int i = 0;
            while (i < count)
            {
                if (arr[i].Alive == 0) { count--; if (i < count) arr[i] = arr[count]; }
                else i++;
            }
        }

        static double ElapsedMs(long ticks) =>
            ticks * 1000.0 / Stopwatch.Frequency;

        void WarmUpBurst()
        {
            // Fire and forget tiny jobs so Burst JIT is done before any timed run.
            {
                var a = new NativeArray<NativeProjectile>(4, Allocator.TempJob);
                new BurstTickJob { Projectiles = a, DeltaTime = 0.02f }
                    .Schedule(4, 4).Complete();
                a.Dispose();
            }
            {
                var p = new NativeArray<NativeProjectile>(4, Allocator.TempJob);
                var t = new NativeArray<CollisionTarget>(4,  Allocator.TempJob);
                var h = new NativeArray<HitResult>(4,        Allocator.TempJob);
                var c = new NativeArray<int>(1,              Allocator.TempJob);
                new BurstCollisionJob { Projectiles = p, Targets = t,
                    OutHits = h, HitCountOut = c }.Schedule().Complete();
                p.Dispose(); t.Dispose(); h.Dispose(); c.Dispose();
            }
            {
                var a = new NativeArray<NativeProjectile>(4, Allocator.TempJob);
                new BurstSpawnJob { Out = a, Speed = 10f, Lifetime = 5f }
                    .Schedule(4, 4).Complete();
                a.Dispose();
            }
            _burstReady = true;
            Debug.Log("[Bench] Burst warm-up done.");
        }

        void LogSummary()
        {
            LogPair("Spawn",       SpawnResults);
            LogPair("Tick",        TickResults);
            LogPair("Collision",   CollisionResults);
            LogPair("SaveRestore", SaveRestoreResults);
            LogContFire(ContFireResults);
        }

        static void LogPair(string name, BenchPair p)
        {
            string verdict = p.BothRan
                ? $"  →  {(p.BurstWins ? "Burst" : "Rust")} wins ({p.SpeedupRatio:F2}×)"
                : "";
            Debug.Log($"[{name}] Rust: {p.rust.Summary}  |  Burst: {p.burst.Summary}{verdict}");
        }

        static void LogContFire(ContFirePair p)
        {
            if (p.rust.HasData)
                Debug.Log($"[ContFire/Rust]  avg={p.rust.avgFrameMs:F3}ms  " +
                          $"peak={p.rust.peakFrameMs:F3}ms  " +
                          $"peakActive={p.rust.peakActive}  " +
                          $"steady={p.rust.steadyActive}  total={p.rust.totalMs:F1}ms");
            if (p.burst.HasData)
                Debug.Log($"[ContFire/Burst] avg={p.burst.avgFrameMs:F3}ms  " +
                          $"peak={p.burst.peakFrameMs:F3}ms  " +
                          $"peakActive={p.burst.peakActive}  " +
                          $"steady={p.burst.steadyActive}  total={p.burst.totalMs:F1}ms");
            if (p.BothRan)
                Debug.Log($"[ContFire] → {(p.BurstWinsAvg ? "Burst" : "Rust")} wins avg frame ({p.AvgSpeedup:F2}×)");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────
    //  Editor Window
    // ─────────────────────────────────────────────────────────────────────────

#if UNITY_EDITOR

    public class ProjectileBenchmarkWindow : EditorWindow
    {
        // ── State ────────────────────────────────────────────────────────────

        ProjectileBenchmark _bench;
        Vector2 _scroll;

        bool _foldSpawn    = true;
        bool _foldTick     = true;
        bool _foldCol      = false;
        bool _foldSR       = false;
        bool _foldContFire = true;

        // ── Colors ───────────────────────────────────────────────────────────

        static readonly Color RustCol  = new Color(0.85f, 0.42f, 0.25f, 1f);
        static readonly Color BurstCol = new Color(0.22f, 0.74f, 0.60f, 1f);
        static readonly Color WinCol   = new Color(0.28f, 0.88f, 0.44f, 1f);
        static readonly Color LoseCol  = new Color(0.85f, 0.32f, 0.32f, 1f);
        static readonly Color DimCol   = new Color(0.50f, 0.50f, 0.50f, 1f);
        static readonly Color BarBg    = new Color(0.20f, 0.20f, 0.20f, 0.35f);

        // ── Setup ────────────────────────────────────────────────────────────

        [MenuItem("Window/MID/Projectile Benchmark")]
        static void Open()
        {
            var w = GetWindow<ProjectileBenchmarkWindow>("Projectile Benchmark");
            w.minSize = new Vector2(480f, 340f);
        }

        void OnEnable()
        {
            EditorApplication.update += Repaint;
            FindBench();
        }

        void OnDisable() => EditorApplication.update -= Repaint;

        void FindBench()
        {
            if (_bench == null)
                _bench = FindObjectOfType<ProjectileBenchmark>();
        }

        // ── Main GUI ─────────────────────────────────────────────────────────

        void OnGUI()
        {
            FindBench();
            DrawToolbar();

            if (_bench == null)
            {
                EditorGUILayout.HelpBox(
                    "No ProjectileBenchmark component found in the open scene.\n" +
                    "Add it to a GameObject alongside ProjectileManager.",
                    MessageType.Warning);
                return;
            }

            EditorGUILayout.Space(2);
            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            {
                DrawBenchSection("Spawn",        ref _foldSpawn,    _bench.SpawnResults,       _bench.RunSpawn);
                DrawBenchSection("Tick",         ref _foldTick,     _bench.TickResults,        _bench.RunTick);
                DrawBenchSection("Collision",    ref _foldCol,      _bench.CollisionResults,   _bench.RunCollision);
                DrawBenchSection("Save/Restore", ref _foldSR,       _bench.SaveRestoreResults, _bench.RunSaveRestore);
                DrawContFireSection();
            }
            EditorGUILayout.EndScrollView();
        }

        // ── Toolbar ──────────────────────────────────────────────────────────

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                EditorGUILayout.LabelField("MID Projectile Benchmark", EditorStyles.boldLabel,
                    GUILayout.ExpandWidth(false));
                GUILayout.FlexibleSpace();

                _bench = (ProjectileBenchmark)EditorGUILayout.ObjectField(
                    _bench, typeof(ProjectileBenchmark), true, GUILayout.Width(186));

                if (GUILayout.Button("Run All", EditorStyles.toolbarButton, GUILayout.Width(58)))
                    _bench?.RunAll();

                if (GUILayout.Button("Clear", EditorStyles.toolbarButton, GUILayout.Width(46)))
                    ClearAll();
            }
        }

        // ── Section (Spawn / Tick / Collision / Save-Restore) ────────────────

        void DrawBenchSection(string title, ref bool fold, BenchPair pair, Action run)
        {
            fold = DrawSectionHeader(title, fold, run);
            if (!fold) return;

            using (new EditorGUI.IndentLevelScope(1))
            {
                EditorGUILayout.Space(3);
                DrawResultRow("Rust ", pair.rust,  pair.burst, RustCol);
                DrawResultRow("Burst", pair.burst, pair.rust,  BurstCol);

                if (pair.BothRan)
                {
                    EditorGUILayout.Space(1);
                    DrawWinnerLine(pair.BurstWins ? "Burst" : "Rust",
                                  pair.SpeedupRatio, pair.BurstWins);
                }
            }
            EditorGUILayout.Space(4);
        }

        // ── Continuous Fire section ───────────────────────────────────────────

        void DrawContFireSection()
        {
            _foldContFire = DrawSectionHeader("Continuous Fire", _foldContFire, _bench.RunContFire);
            if (!_foldContFire) return;

            using (new EditorGUI.IndentLevelScope(1))
            {
                EditorGUILayout.Space(3);

                var cf = _bench.ContFireResults;
                DrawContFireRow("Rust ", cf.rust,  cf.burst.HasData ? cf.burst.avgFrameMs : 0, RustCol);
                DrawContFireRow("Burst", cf.burst, cf.rust.HasData  ? cf.rust.avgFrameMs  : 0, BurstCol);

                if (cf.BothRan)
                {
                    EditorGUILayout.Space(1);
                    DrawWinnerLine(cf.BurstWinsAvg ? "Burst" : "Rust",
                                  cf.AvgSpeedup, cf.BurstWinsAvg);

                    // Show steady-state active — the "what you actually pay at runtime" count
                    var old = GUI.color;
                    GUI.color = DimCol;
                    EditorGUILayout.LabelField(
                        $"Steady-state active:  Rust {cf.rust.steadyActive}  /  Burst {cf.burst.steadyActive}  " +
                        "(active count at final simulated frame)",
                        EditorStyles.miniLabel);
                    GUI.color = old;
                }
            }
            EditorGUILayout.Space(4);
        }

        // ── Row renderers ─────────────────────────────────────────────────────

        void DrawResultRow(string label, BenchResult r, BenchResult other, Color col)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Backend label (colored)
                var old = GUI.color;
                GUI.color = col;
                EditorGUILayout.LabelField(label, GUILayout.Width(40));
                GUI.color = old;

                if (!r.HasData)
                {
                    GUI.color = DimCol;
                    EditorGUILayout.LabelField("—  not run yet", EditorStyles.miniLabel);
                    GUI.color = old;
                    return;
                }

                // Total ms
                EditorGUILayout.LabelField($"{r.totalMs:F2} ms", GUILayout.Width(72));
                // Per-op
                EditorGUILayout.LabelField($"({r.perOpUs:F1} µs/op)", GUILayout.Width(100));
                // Proportional bar
                float maxMs = other.HasData ? (float)Math.Max(r.totalMs, other.totalMs) : (float)r.totalMs;
                DrawBar((float)(r.totalMs / maxMs), col, 90f);
                // Note
                GUI.color = DimCol;
                EditorGUILayout.LabelField(r.note, EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        void DrawContFireRow(string label, ContFireResult r, double otherAvgMs, Color col)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var old = GUI.color;
                GUI.color = col;
                EditorGUILayout.LabelField(label, GUILayout.Width(40));
                GUI.color = old;

                if (!r.HasData)
                {
                    GUI.color = DimCol;
                    EditorGUILayout.LabelField("—  not run yet", EditorStyles.miniLabel);
                    GUI.color = old;
                    return;
                }

                EditorGUILayout.LabelField($"avg {r.avgFrameMs:F3} ms",  GUILayout.Width(100));
                EditorGUILayout.LabelField($"peak {r.peakFrameMs:F3} ms", GUILayout.Width(100));

                float maxAvg = otherAvgMs > 0 ? (float)Math.Max(r.avgFrameMs, otherAvgMs) : (float)r.avgFrameMs;
                DrawBar((float)(r.avgFrameMs / maxAvg), col, 70f);

                GUI.color = DimCol;
                EditorGUILayout.LabelField(
                    $"peak active={r.peakActive}  steady={r.steadyActive}  spawned={r.totalSpawned}",
                    EditorStyles.miniLabel);
                GUI.color = old;
            }
        }

        void DrawWinnerLine(string winner, double ratio, bool isBurst)
        {
            var old = GUI.color;
            GUI.color = isBurst ? WinCol : RustCol;
            EditorGUILayout.LabelField(
                $"→  {winner} wins  ({ratio:F2}×)",
                EditorStyles.miniBoldLabel);
            GUI.color = old;
        }

        // ── Utilities ─────────────────────────────────────────────────────────

        bool DrawSectionHeader(string title, bool fold, Action run)
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                bool next = EditorGUILayout.Foldout(fold, title, true, EditorStyles.foldoutHeader);
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("Run", EditorStyles.toolbarButton, GUILayout.Width(36)))
                    run?.Invoke();
                return next;
            }
        }

        void DrawBar(float fraction, Color col, float width)
        {
            fraction = Mathf.Clamp01(fraction);
            Rect r = GUILayoutUtility.GetRect(width, 14f, GUILayout.Width(width));
            r.y      += 3f;
            r.height  = 8f;
            EditorGUI.DrawRect(r, BarBg);
            Rect fill = r; fill.width = r.width * fraction;
            EditorGUI.DrawRect(fill, col);
            GUILayout.Space(2);
        }

        void ClearAll()
        {
            if (_bench == null) return;
            _bench.SpawnResults       = default;
            _bench.TickResults        = default;
            _bench.CollisionResults   = default;
            _bench.SaveRestoreResults = default;
            _bench.ContFireResults    = default;
        }
    }

#endif // UNITY_EDITOR

} // namespace MidManStudio.Projectiles
