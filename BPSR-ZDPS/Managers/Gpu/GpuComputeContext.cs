using Serilog;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace BPSR_ZDPS.Managers.Gpu
{
    /// <summary>
    /// Layout-compatible with the <c>Candidate</c> struct in ModuleSolver.hlsl (12 bytes).
    /// </summary>
    public struct GpuCandidate
    {
        public int Score;
        public uint I;     // first module index
        public uint Rank;  // lexicographic rank of the chosen K-1 offsets (decoded on CPU)
    }

    /// <summary>
    /// All inputs required for one GPU module-combo solve. Arrays use the normalized
    /// stat indexing produced by ModuleOptimizerGpu; per-stat arrays are zero-padded
    /// to a multiple of 4 entries and ModuleStats packs four 8-bit values per uint.
    /// </summary>
    public sealed class GpuSolveInput
    {
        public int NumModules;          // N
        public int NumModulesInSet;     // K (1..10)
        public int NumStats;            // S real stat count (<= 32); arrays sized ceil4(S)
        public int ScoreMode;           // 0 = ZScore, 1 = CombatPower
        public bool HasExact;
        public int MaxTotal;            // K * 20
        public int OriginalScoring;     // 1 = upstream ZScore (adds overcap points)

        public uint[] ModuleStats = [];   // [N * ceil(S/4)] four packed 8-bit stat values per uint
        public float[] StatMul = [];      // [ceil4(S)] combined weight: legendaryMul x orderBoost (0 = ignore)
        public int[] StatReq = [];        // [ceil4(S)]
        public int[] StatExact = [];      // [ceil4(S)]
        public int[] StatCap = [];        // [ceil4(S)] upper-bound cap per stat (StatPrio.NoCap = none)
        public uint[] Binom = [];         // [(N+1)*(K+1)]
        public int[] LinkBonus = [];      // [6]
        public int[] StatCombat = [];     // [ceil4(S)*6]
        public int[] LinkTotalFight = []; // [MaxTotal+1]
    }

    /// <summary>
    /// A dedicated D3D11 device + immediate context used only for the module solver's
    /// background task (kept separate from the render device to avoid context contention).
    /// Throws on construction when DirectCompute (FL11) is unavailable.
    /// </summary>
    public sealed unsafe class GpuComputeContext : IDisposable
    {
        // THREADS x GROUPS_X = threads per dispatch. The old 64x256 = 16k threads left a
        // modern GPU ~90% idle (a 5080 keeps ~170k threads in flight); 256x1024 = 262k
        // threads measured ~2.5x faster end-to-end on a 70G-combo solve. Output scales
        // linearly with GROUPS_X (numI * GROUPS_X * TOPK entries; ~47MB at N=386).
        private const int THREADS = 256;    // must match ModuleSolver.hlsl
        private const int TOPK = 10;        // must match ModuleSolver.hlsl
        private const uint GROUPS_X = 1024; // grid.x; each (i,group) covers a contiguous rank chunk

        // Max sub-combos evaluated per dispatch. Each dispatch is flushed as its own GPU
        // packet, keeping it far below the ~2s Windows TDR limit even when the game is
        // hogging the GPU (a single huge dispatch used to reset the driver = black screen).
        // Also keeps every in-shader rank offset within uint32 no matter the slice base.
        private const uint COMBO_BUDGET = 4_000_000;

        /// <summary>Test/debug override for the per-dispatch combo budget (0 = default).</summary>
        public static uint DebugComboBudget;

        private static uint ComboBudget => DebugComboBudget > 0 ? DebugComboBudget : COMBO_BUDGET;

        private readonly D3D11 _d3d11;
        private readonly DXGI _dxgi;

        private ComPtr<ID3D11Device> _device;
        private ComPtr<ID3D11DeviceContext> _context;
        private ComPtr<ID3D11ComputeShader> _shader;

        public string AdapterName { get; private set; } = "Unknown";

        private struct ParamsCb
        {
            public uint NumModules;
            public uint K;
            public uint NumStats;
            public uint ScoreMode;
            public uint HasExact;
            public uint MaxTotal;
            public uint GroupsX;
            public uint IBase;      // first module row covered by this dispatch
            public uint RankStart;  // first sub-combo rank of this slice
            public uint RankEnd;    // one past the last rank (clamped to subCount in-shader)
            public uint OriginalScoring; // 1 = upstream ZScore (adds overcap points)
            public uint CollectMode;     // 1 = append combos with score >= ScoreThreshold (gates ignored)
            public int ScoreThreshold;   // collect mode: minimum score to keep
        }

        public GpuComputeContext(byte[] shaderSource)
        {
            _dxgi = DXGI.GetApi();
            _d3d11 = D3D11.GetApi();

            CreateDevice();
            CompileAndCreateShader(shaderSource);
        }

        private void CreateDevice()
        {
            D3DFeatureLevel[] levels =
            [
                D3DFeatureLevel.Level111,
                D3DFeatureLevel.Level110
            ];

            ID3D11Device* device = null;
            ID3D11DeviceContext* context = null;
            D3DFeatureLevel obtained = 0;

            fixed (D3DFeatureLevel* pLevels = levels)
            {
                int hr = _d3d11.CreateDevice(
                    (IDXGIAdapter*)null,
                    D3DDriverType.Hardware,
                    nint.Zero,
                    (uint)CreateDeviceFlag.None,
                    pLevels,
                    (uint)levels.Length,
                    D3D11.SdkVersion,
                    &device,
                    &obtained,
                    &context);

                SilkMarshal.ThrowHResult(hr);
            }

            if (obtained < D3DFeatureLevel.Level110)
            {
                throw new NotSupportedException($"GPU feature level {obtained} does not support DirectCompute structured buffers.");
            }

            _device = device;
            _context = context;

            QueryAdapterName();
        }

        private void QueryAdapterName()
        {
            try
            {
                ComPtr<IDXGIDevice> dxgiDevice = default;
                _device.QueryInterface(out dxgiDevice);
                if (dxgiDevice.Handle == null) return;

                ComPtr<IDXGIAdapter> adapter = default;
                dxgiDevice.GetAdapter(ref adapter);
                if (adapter.Handle != null)
                {
                    AdapterDesc desc;
                    adapter.GetDesc(&desc);
                    AdapterName = new string(desc.Description);
                    adapter.Release();
                }
                dxgiDevice.Release();
            }
            catch
            {
                // Name is cosmetic only.
            }
        }

        private void CompileAndCreateShader(byte[] shaderSource)
        {
            var compiler = D3DCompiler.GetApi();
            try
            {
                ID3D10Blob* code = null;
                ID3D10Blob* errors = null;

                byte[] entry = Encoding.ASCII.GetBytes("CSMain\0");
                byte[] target = Encoding.ASCII.GetBytes("cs_5_0\0");
                byte[] name = Encoding.ASCII.GetBytes("ModuleSolver.hlsl\0");

                int hr;
                fixed (byte* pSrc = shaderSource)
                fixed (byte* pEntry = entry)
                fixed (byte* pTarget = target)
                fixed (byte* pName = name)
                {
                    hr = compiler.Compile(
                        pSrc,
                        (nuint)shaderSource.Length,
                        pName,
                        (D3DShaderMacro*)null,
                        (ID3DInclude*)null,
                        pEntry,
                        pTarget,
                        0u,
                        0u,
                        &code,
                        &errors);
                }

                if (HResult.IndicatesFailure(hr))
                {
                    string msg = "Unknown HLSL compile error";
                    if (errors != null)
                    {
                        msg = SilkMarshal.PtrToString((nint)errors->GetBufferPointer()) ?? msg;
                        errors->Release();
                    }
                    throw new InvalidOperationException($"Failed to compile ModuleSolver.hlsl: {msg}");
                }

                if (errors != null)
                {
                    errors->Release();
                }

                ID3D11ComputeShader* shader = null;
                SilkMarshal.ThrowHResult(_device.CreateComputeShader(
                    code->GetBufferPointer(),
                    code->GetBufferSize(),
                    (ID3D11ClassLinkage*)null,
                    &shader));

                _shader = shader;
                code->Release();
            }
            finally
            {
                compiler.Dispose();
            }
        }

        private ParamsCb BuildParams(GpuSolveInput input, uint collectMode, int scoreThreshold)
        {
            return new ParamsCb
            {
                NumModules = (uint)input.NumModules,
                K = (uint)input.NumModulesInSet,
                NumStats = (uint)input.NumStats,
                ScoreMode = (uint)input.ScoreMode,
                HasExact = input.HasExact ? 1u : 0u,
                MaxTotal = (uint)input.MaxTotal,
                GroupsX = GROUPS_X,
                IBase = 0,
                RankStart = 0,
                RankEnd = uint.MaxValue,
                OriginalScoring = (uint)input.OriginalScoring,
                CollectMode = collectMode,
                ScoreThreshold = scoreThreshold
            };
        }

        private ID3D11ShaderResourceView*[] CreateInputSrvs(GpuSolveInput input, out ID3D11Buffer*[] buffers)
        {
            var srvs = new ID3D11ShaderResourceView*[9];
            buffers = new ID3D11Buffer*[9];
            srvs[0] = CreateStructuredSrv(input.ModuleStats, out buffers[0]);
            srvs[1] = CreateStructuredSrv(input.StatMul, out buffers[1]);
            srvs[2] = CreateStructuredSrv(input.StatReq, out buffers[2]);
            srvs[3] = CreateStructuredSrv(input.StatExact, out buffers[3]);
            srvs[4] = CreateStructuredSrv(input.Binom, out buffers[4]);
            srvs[5] = CreateStructuredSrv(input.LinkBonus, out buffers[5]);
            srvs[6] = CreateStructuredSrv(input.StatCombat, out buffers[6]);
            srvs[7] = CreateStructuredSrv(input.LinkTotalFight, out buffers[7]);
            srvs[8] = CreateStructuredSrv(input.StatCap, out buffers[8]);
            return srvs;
        }

        private void ReleaseInputSrvs(ID3D11ShaderResourceView*[] srvs, ID3D11Buffer*[] buffers)
        {
            for (int i = 0; i < srvs.Length; i++)
            {
                ReleaseSrv(srvs[i], buffers[i]);
            }
        }

        private void BindCommon(ID3D11Buffer* cbBuf, ID3D11ShaderResourceView*[] srvs)
        {
            _context.CSSetShader(_shader, (ID3D11ClassInstance**)null, 0);
            _context.CSSetConstantBuffers(0, 1, ref cbBuf);

            fixed (ID3D11ShaderResourceView** pSrvs = srvs)
            {
                _context.CSSetShaderResources(0, (uint)srvs.Length, pSrvs);
            }
        }

        /// <summary>
        /// Issues the enumeration as many small dispatches, each covering at most
        /// COMBO_BUDGET sub-combos, flushed individually so no single GPU packet can hit
        /// the TDR limit. Progress (0..1, fraction of sub-combos dispatched) is reported
        /// after each slice; cancelling stops after the current slice.
        /// </summary>
        private void RunSlices(ID3D11Buffer* cbBuf, ParamsCb cb, GpuSolveInput input, CancellationToken cancelToken, Action<float>? progress)
        {
            int numI = input.NumModules - input.NumModulesInSet + 1;
            int k = input.NumModulesInSet;
            int cols = k + 1;

            // Total sub-combos across all rows, for progress reporting.
            double totalCombos = 0;
            for (int row = 0; row < numI; row++)
            {
                totalCombos += input.Binom[(input.NumModules - row - 1) * cols + (k - 1)];
            }

            // Adaptive slice budget: start at the conservative fixed size, then measure
            // real throughput at sync points and grow each packet toward TARGET_SLICE_SEC
            // of GPU time. Millions of tiny fixed slices spent most wall time on submit
            // overhead; ~30ms packets keep a ~60x margin below the ~2s TDR limit and
            // retuning every SYNC_INTERVAL slices tracks changing GPU load (the game
            // starting/stopping mid-solve shrinks/grows the next packets accordingly).
            // A debug override pins the budget for tests.
            const double TARGET_SLICE_SEC = 0.030;
            const uint MAX_BUDGET = 256_000_000;
            const int SYNC_INTERVAL = 16;
            uint budget = ComboBudget;
            bool adaptive = DebugComboBudget == 0;
            bool tunedOnce = false;
            int slicesSinceSync = 0;
            double combosSinceSync = 0;
            var syncWatch = Stopwatch.StartNew();

            void AfterSlice(double sliceCombos, ref double doneAcc)
            {
                doneAcc += sliceCombos;
                progress?.Invoke((float)(doneAcc / totalCombos));

                if (!adaptive)
                {
                    return;
                }

                slicesSinceSync++;
                combosSinceSync += sliceCombos;
                if (!tunedOnce || slicesSinceSync >= SYNC_INTERVAL)
                {
                    WaitForGpu();
                    double sec = syncWatch.Elapsed.TotalSeconds;
                    if (sec > 0.0005)
                    {
                        double throughput = combosSinceSync / sec; // combos per second
                        budget = (uint)Math.Clamp(throughput * TARGET_SLICE_SEC, ComboBudget, MAX_BUDGET);
                    }

                    tunedOnce = true;
                    slicesSinceSync = 0;
                    combosSinceSync = 0;
                    syncWatch.Restart();
                }
            }

            double doneCombos = 0;
            int i = 0;
            while (i < numI && !cancelToken.IsCancellationRequested)
            {
                // C(N-i-1, K-1): sub-combos rooted at first-module row i.
                uint subCount = input.Binom[(input.NumModules - i - 1) * cols + (k - 1)];
                if (subCount > budget)
                {
                    // One oversized row: slice its rank range.
                    cb.IBase = (uint)i;
                    ulong start = 0;
                    while (start < subCount && !cancelToken.IsCancellationRequested)
                    {
                        cb.RankStart = (uint)start;
                        cb.RankEnd = (uint)Math.Min(subCount, start + budget);
                        UpdateConstantBuffer(cbBuf, ref cb);
                        _context.Dispatch(GROUPS_X, 1, 1);
                        _context.Flush();

                        double done = cb.RankEnd - cb.RankStart;
                        start = cb.RankEnd;
                        AfterSlice(done, ref doneCombos);
                    }
                    i++;
                }
                else
                {
                    // Batch consecutive small rows into one dispatch within the budget.
                    int rows = 1;
                    ulong total = subCount;
                    while (i + rows < numI && rows < 65535)
                    {
                        uint next = input.Binom[(input.NumModules - (i + rows) - 1) * cols + (k - 1)];
                        if (total + next > budget)
                        {
                            break;
                        }
                        total += next;
                        rows++;
                    }

                    cb.IBase = (uint)i;
                    cb.RankStart = 0;
                    cb.RankEnd = uint.MaxValue;
                    UpdateConstantBuffer(cbBuf, ref cb);
                    _context.Dispatch(GROUPS_X, (uint)rows, 1);
                    _context.Flush();
                    i += rows;

                    AfterSlice(total, ref doneCombos);
                }
            }
        }

        /// <summary>
        /// Blocks until every command submitted so far has finished executing on the GPU
        /// (event query + poll). Used as the adaptive-budget measurement sync point.
        /// </summary>
        private void WaitForGpu()
        {
            var desc = new QueryDesc { Query = Query.Event, MiscFlags = 0 };
            ID3D11Query* query = null;
            SilkMarshal.ThrowHResult(_device.CreateQuery(&desc, &query));

            _context.End((ID3D11Asynchronous*)query);
            _context.Flush();

            uint data = 0;
            while (_context.GetData((ID3D11Asynchronous*)query, &data, (uint)sizeof(uint), 0) != 0)
            {
                Thread.SpinWait(256);
            }

            query->Release();
        }

        /// <summary>
        /// Solve mode: runs the kernel in TDR-safe slices and returns every (i, group)'s
        /// accumulated top-K candidates; the CPU caller merges these into the final top-10.
        /// Cancelling stops after the current slice; already-computed candidates remain
        /// valid (partial result).
        /// </summary>
        public GpuCandidate[] Dispatch(GpuSolveInput input, CancellationToken cancelToken, Action<float>? progress = null)
        {
            int numI = input.NumModules - input.NumModulesInSet + 1;
            if (numI <= 0)
            {
                return [];
            }

            var cb = BuildParams(input, collectMode: 0, scoreThreshold: 0);
            ID3D11Buffer* cbBuf = CreateConstantBuffer(ref cb);
            var srvs = CreateInputSrvs(input, out var srvBuffers);

            int outCount = (int)(GROUPS_X * (uint)numI * TOPK);
            ID3D11UnorderedAccessView* uav = CreateRWStructured((uint)sizeof(GpuCandidate), (uint)outCount, out var outBuffer);

            try
            {
                BindCommon(cbBuf, srvs);

                uint initCount = 0;
                _context.CSSetUnorderedAccessViews(0, 1, ref uav, ref initCount);

                // Groups whose (i + K > N) return without writing; pre-fill every uint with
                // 0x80000001 (== SCORE_INVALID as int) so unwritten candidates are filtered out.
                uint* clearVals = stackalloc uint[4] { 0x80000001u, 0x80000001u, 0x80000001u, 0x80000001u };
                _context.ClearUnorderedAccessViewUint(uav, clearVals);

                RunSlices(cbBuf, cb, input, cancelToken, progress);

                var results = ReadBackRange(outBuffer, outCount);

                // Unbind UAV so the buffer can be released and reused next solve.
                ID3D11UnorderedAccessView* nullUav = null;
                _context.CSSetUnorderedAccessViews(0, 1, ref nullUav, ref initCount);

                return results;
            }
            finally
            {
                outBuffer->Release();
                uav->Release();
                ReleaseInputSrvs(srvs, srvBuffers);
                cbBuf->Release();
            }
        }

        /// <summary>
        /// Collect mode: re-runs the enumeration appending every combo whose score is at
        /// least <paramref name="scoreThreshold"/> (requirement gates ignored) into a pool
        /// of at most <paramref name="capacity"/> entries. Returns the entries that fit and
        /// the total number of qualifying combos (Total > Entries.Length means overflow;
        /// the caller retries with a higher threshold or gives up caching).
        /// </summary>
        public (GpuCandidate[] Entries, uint TotalCount) DispatchCollect(GpuSolveInput input, int scoreThreshold, int capacity, CancellationToken cancelToken, Action<float>? progress = null)
        {
            int numI = input.NumModules - input.NumModulesInSet + 1;
            if (numI <= 0)
            {
                return ([], 0);
            }

            var cb = BuildParams(input, collectMode: 1, scoreThreshold: scoreThreshold);
            ID3D11Buffer* cbBuf = CreateConstantBuffer(ref cb);
            var srvs = CreateInputSrvs(input, out var srvBuffers);

            ID3D11UnorderedAccessView* uav = CreateAppendStructured((uint)sizeof(GpuCandidate), (uint)capacity, out var poolBuffer);

            try
            {
                BindCommon(cbBuf, srvs);

                // Bind at u1 with the hidden append counter reset to 0; it then accumulates
                // across all slices (bindings and counter persist across Flush).
                uint initCount = 0;
                _context.CSSetUnorderedAccessViews(1, 1, ref uav, ref initCount);

                RunSlices(cbBuf, cb, input, cancelToken, progress);

                uint total = ReadCounter(uav);
                int readCount = (int)Math.Min(total, (uint)capacity);
                var entries = ReadBackRange(poolBuffer, readCount);

                ID3D11UnorderedAccessView* nullUav = null;
                uint keepCount = uint.MaxValue;
                _context.CSSetUnorderedAccessViews(1, 1, ref nullUav, ref keepCount);

                return (entries, total);
            }
            finally
            {
                poolBuffer->Release();
                uav->Release();
                ReleaseInputSrvs(srvs, srvBuffers);
                cbBuf->Release();
            }
        }

        private ID3D11Buffer* CreateConstantBuffer<T>(ref T data) where T : unmanaged
        {
            var desc = new BufferDesc
            {
                ByteWidth = (uint)((sizeof(T) + 15) & ~15),
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.ConstantBuffer,
                CPUAccessFlags = 0,
                MiscFlags = 0,
                StructureByteStride = 0
            };

            ID3D11Buffer* buffer = null;
            fixed (T* p = &data)
            {
                var init = new SubresourceData { PSysMem = p };
                SilkMarshal.ThrowHResult(_device.CreateBuffer(&desc, &init, &buffer));
            }
            return buffer;
        }

        private void UpdateConstantBuffer<T>(ID3D11Buffer* buffer, ref T data) where T : unmanaged
        {
            fixed (T* p = &data)
            {
                _context.UpdateSubresource((ID3D11Resource*)buffer, 0, (Box*)null, p, 0, 0);
            }
        }

        private ID3D11ShaderResourceView* CreateStructuredSrv<T>(T[] data, out ID3D11Buffer* buffer)
            where T : unmanaged
        {
            uint stride = (uint)sizeof(T);
            uint count = (uint)Math.Max(1, data.Length);

            var desc = new BufferDesc
            {
                ByteWidth = stride * count,
                Usage = Usage.Immutable,
                BindFlags = (uint)BindFlag.ShaderResource,
                CPUAccessFlags = 0,
                MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
                StructureByteStride = stride
            };

            // Immutable buffers require non-null init data; ensure at least one element.
            T[] backing = data.Length > 0 ? data : new T[1];

            ID3D11Buffer* buf = null;
            fixed (T* p = backing)
            {
                var init = new SubresourceData { PSysMem = p };
                SilkMarshal.ThrowHResult(_device.CreateBuffer(&desc, &init, &buf));
            }

            var srvDesc = new ShaderResourceViewDesc
            {
                Format = Format.FormatUnknown,
                ViewDimension = D3DSrvDimension.D3D11SrvDimensionBuffer
            };
            srvDesc.Anonymous.Buffer.FirstElement = 0;
            srvDesc.Anonymous.Buffer.NumElements = count;

            ID3D11ShaderResourceView* srv = null;
            SilkMarshal.ThrowHResult(_device.CreateShaderResourceView((ID3D11Resource*)buf, &srvDesc, &srv));

            buffer = buf;
            return srv;
        }

        private ID3D11UnorderedAccessView* CreateRWStructured(uint stride, uint count, out ID3D11Buffer* buffer)
        {
            return CreateStructuredUav(stride, count, 0, out buffer);
        }

        private ID3D11UnorderedAccessView* CreateAppendStructured(uint stride, uint count, out ID3D11Buffer* buffer)
        {
            return CreateStructuredUav(stride, count, (uint)BufferUavFlag.Append, out buffer);
        }

        private ID3D11UnorderedAccessView* CreateStructuredUav(uint stride, uint count, uint uavFlags, out ID3D11Buffer* buffer)
        {
            var desc = new BufferDesc
            {
                ByteWidth = stride * count,
                Usage = Usage.Default,
                BindFlags = (uint)BindFlag.UnorderedAccess,
                CPUAccessFlags = 0,
                MiscFlags = (uint)ResourceMiscFlag.BufferStructured,
                StructureByteStride = stride
            };

            ID3D11Buffer* buf = null;
            SilkMarshal.ThrowHResult(_device.CreateBuffer(&desc, (SubresourceData*)null, &buf));

            var uavDesc = new UnorderedAccessViewDesc
            {
                Format = Format.FormatUnknown,
                ViewDimension = UavDimension.Buffer
            };
            uavDesc.Anonymous.Buffer.FirstElement = 0;
            uavDesc.Anonymous.Buffer.NumElements = count;
            uavDesc.Anonymous.Buffer.Flags = uavFlags;

            ID3D11UnorderedAccessView* uav = null;
            SilkMarshal.ThrowHResult(_device.CreateUnorderedAccessView((ID3D11Resource*)buf, &uavDesc, &uav));

            buffer = buf;
            return uav;
        }

        /// <summary>Reads back an append UAV's hidden counter (number of appended entries).</summary>
        private uint ReadCounter(ID3D11UnorderedAccessView* uav)
        {
            var desc = new BufferDesc
            {
                ByteWidth = 4,
                Usage = Usage.Staging,
                BindFlags = 0,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
                MiscFlags = 0,
                StructureByteStride = 0
            };

            ID3D11Buffer* staging = null;
            SilkMarshal.ThrowHResult(_device.CreateBuffer(&desc, (SubresourceData*)null, &staging));

            try
            {
                _context.CopyStructureCount(staging, 0, uav);

                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(_context.Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped));
                uint count = *(uint*)mapped.PData;
                _context.Unmap((ID3D11Resource*)staging, 0);
                return count;
            }
            finally
            {
                staging->Release();
            }
        }

        /// <summary>Copies the first <paramref name="count"/> candidates of a GPU buffer to the CPU.</summary>
        private GpuCandidate[] ReadBackRange(ID3D11Buffer* src, int count)
        {
            if (count <= 0)
            {
                return [];
            }

            uint byteWidth = (uint)(sizeof(GpuCandidate) * count);
            var desc = new BufferDesc
            {
                ByteWidth = byteWidth,
                Usage = Usage.Staging,
                BindFlags = 0,
                CPUAccessFlags = (uint)CpuAccessFlag.Read,
                MiscFlags = 0,
                StructureByteStride = 0
            };

            ID3D11Buffer* staging = null;
            SilkMarshal.ThrowHResult(_device.CreateBuffer(&desc, (SubresourceData*)null, &staging));

            try
            {
                var box = new Box { Left = 0, Top = 0, Front = 0, Right = byteWidth, Bottom = 1, Back = 1 };
                _context.CopySubresourceRegion((ID3D11Resource*)staging, 0, 0, 0, 0, (ID3D11Resource*)src, 0, &box);

                MappedSubresource mapped = default;
                SilkMarshal.ThrowHResult(_context.Map((ID3D11Resource*)staging, 0, Map.Read, 0, &mapped));

                var result = new GpuCandidate[count];
                fixed (GpuCandidate* dst = result)
                {
                    Unsafe.CopyBlock(dst, mapped.PData, byteWidth);
                }

                _context.Unmap((ID3D11Resource*)staging, 0);
                return result;
            }
            finally
            {
                staging->Release();
            }
        }

        private static void ReleaseSrv(ID3D11ShaderResourceView* srv, ID3D11Buffer* buffer)
        {
            if (srv != null) srv->Release();
            if (buffer != null) buffer->Release();
        }

        public void Dispose()
        {
            if (_shader.Handle != null) _shader.Release();
            if (_context.Handle != null) _context.Release();
            if (_device.Handle != null) _device.Release();
            _d3d11.Dispose();
            _dxgi.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
