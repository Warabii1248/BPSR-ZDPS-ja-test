using Serilog;
using Silk.NET.Core.Native;
using Silk.NET.Direct3D.Compilers;
using Silk.NET.Direct3D11;
using Silk.NET.DXGI;
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
    /// stat indexing produced by ModuleOptimizerGpu.
    /// </summary>
    public sealed class GpuSolveInput
    {
        public int NumModules;          // N
        public int NumModulesInSet;     // K (1..5)
        public int NumStats;            // S (<= 32)
        public int ScoreMode;           // 0 = ZScore, 1 = CombatPower
        public bool HasExact;
        public int MaxTotal;            // K * 20

        public uint[] ModuleStats = [];   // [N*S]
        public float[] StatMul = [];      // [S] combined weight: legendaryMul x orderBoost (0 = ignore)
        public int[] StatReq = [];        // [S]
        public int[] StatMin = [];        // [S]
        public int[] StatExact = [];      // [S]
        public uint[] Binom = [];         // [(N+1)*(K+1)]
        public int[] LinkBonus = [];      // [6]
        public int[] StatCombat = [];     // [S*6]
        public int[] LinkTotalFight = []; // [MaxTotal+1]
    }

    /// <summary>
    /// A dedicated D3D11 device + immediate context used only for the module solver's
    /// background task (kept separate from the render device to avoid context contention).
    /// Throws on construction when DirectCompute (FL11) is unavailable; callers fall back to CPU.
    /// </summary>
    public sealed unsafe class GpuComputeContext : IDisposable
    {
        private const int THREADS = 64;    // must match ModuleSolver.hlsl
        private const int TOPK = 10;        // must match ModuleSolver.hlsl
        private const uint GROUPS_X = 256;  // grid.x; grid-stride covers each i-row

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
            public uint Pad0;
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

        /// <summary>
        /// Runs the solver kernel and returns every group's top-K candidates. The CPU caller
        /// merges these into the final top-10.
        /// </summary>
        public GpuCandidate[] Dispatch(GpuSolveInput input)
        {
            int numI = input.NumModules - input.NumModulesInSet + 1;
            if (numI <= 0)
            {
                return [];
            }

            var cb = new ParamsCb
            {
                NumModules = (uint)input.NumModules,
                K = (uint)input.NumModulesInSet,
                NumStats = (uint)input.NumStats,
                ScoreMode = (uint)input.ScoreMode,
                HasExact = input.HasExact ? 1u : 0u,
                MaxTotal = (uint)input.MaxTotal,
                GroupsX = GROUPS_X,
                Pad0 = 0
            };

            ID3D11Buffer* cbBuf = CreateConstantBuffer(ref cb);

            ID3D11ShaderResourceView* srvModuleStats = CreateStructuredSrv(input.ModuleStats, out var bModuleStats);
            ID3D11ShaderResourceView* srvStatMul = CreateStructuredSrv(input.StatMul, out var bStatMul);
            ID3D11ShaderResourceView* srvStatReq = CreateStructuredSrv(input.StatReq, out var bStatReq);
            ID3D11ShaderResourceView* srvStatMin = CreateStructuredSrv(input.StatMin, out var bStatMin);
            ID3D11ShaderResourceView* srvStatExact = CreateStructuredSrv(input.StatExact, out var bStatExact);
            ID3D11ShaderResourceView* srvBinom = CreateStructuredSrv(input.Binom, out var bBinom);
            ID3D11ShaderResourceView* srvLinkBonus = CreateStructuredSrv(input.LinkBonus, out var bLinkBonus);
            ID3D11ShaderResourceView* srvStatCombat = CreateStructuredSrv(input.StatCombat, out var bStatCombat);
            ID3D11ShaderResourceView* srvLinkTotal = CreateStructuredSrv(input.LinkTotalFight, out var bLinkTotal);

            int outCount = (int)(GROUPS_X * (uint)numI * TOPK);
            ID3D11UnorderedAccessView* uav = CreateRWStructured((uint)sizeof(GpuCandidate), (uint)outCount, out var outBuffer);

            try
            {
                _context.CSSetShader(_shader, (ID3D11ClassInstance**)null, 0);
                _context.CSSetConstantBuffers(0, 1, ref cbBuf);

                ID3D11ShaderResourceView** srvs = stackalloc ID3D11ShaderResourceView*[9];
                srvs[0] = srvModuleStats;
                srvs[1] = srvStatMul;
                srvs[2] = srvStatReq;
                srvs[3] = srvStatMin;
                srvs[4] = srvStatExact;
                srvs[5] = srvBinom;
                srvs[6] = srvLinkBonus;
                srvs[7] = srvStatCombat;
                srvs[8] = srvLinkTotal;
                _context.CSSetShaderResources(0, 9, srvs);

                uint initCount = 0;
                _context.CSSetUnorderedAccessViews(0, 1, ref uav, ref initCount);

                // Groups whose (i + K > N) return without writing; pre-fill every uint with
                // 0x80000001 (== SCORE_INVALID as int) so unwritten candidates are filtered out.
                uint* clearVals = stackalloc uint[4] { 0x80000001u, 0x80000001u, 0x80000001u, 0x80000001u };
                _context.ClearUnorderedAccessViewUint(uav, clearVals);

                _context.Dispatch(GROUPS_X, (uint)numI, 1);

                var results = ReadBack(outBuffer, outCount);

                // Unbind UAV so the buffer can be released and reused next solve.
                ID3D11UnorderedAccessView* nullUav = null;
                _context.CSSetUnorderedAccessViews(0, 1, ref nullUav, ref initCount);

                return results;
            }
            finally
            {
                outBuffer->Release();
                uav->Release();
                ReleaseSrv(srvModuleStats, bModuleStats);
                ReleaseSrv(srvStatMul, bStatMul);
                ReleaseSrv(srvStatReq, bStatReq);
                ReleaseSrv(srvStatMin, bStatMin);
                ReleaseSrv(srvStatExact, bStatExact);
                ReleaseSrv(srvBinom, bBinom);
                ReleaseSrv(srvLinkBonus, bLinkBonus);
                ReleaseSrv(srvStatCombat, bStatCombat);
                ReleaseSrv(srvLinkTotal, bLinkTotal);
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
            uavDesc.Anonymous.Buffer.Flags = 0;

            ID3D11UnorderedAccessView* uav = null;
            SilkMarshal.ThrowHResult(_device.CreateUnorderedAccessView((ID3D11Resource*)buf, &uavDesc, &uav));

            buffer = buf;
            return uav;
        }

        private GpuCandidate[] ReadBack(ID3D11Buffer* src, int count)
        {
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
                _context.CopyResource((ID3D11Resource*)staging, (ID3D11Resource*)src);

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
