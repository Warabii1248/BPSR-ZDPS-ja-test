// ===========================================================================
// ModuleSolver.hlsl  -  DirectCompute (Shader Model 5.0) module-combo solver
// ---------------------------------------------------------------------------
// Evaluates K-module combinations (K = 1..10), scoring each with either the
// heuristic "ZScore" (mirrors ModuleOptimizerBeam.CalcScore: breakpoint snap x
// link bonus x legendary/order weights, plus overcap points when OriginalScoring
// is set) or the in-game "combat power" (mirrors CalcCombosCombatScore).
//
// Dispatch grid = (G, rows, 1), issued by the CPU as many small slices so each GPU
// packet stays far below the ~2s Windows TDR limit (the game shares the GPU):
//   * IBase + groupId.y = i      -> the fixed first module index of the combo.
//   * Each thread owns a CONTIGUOUS chunk of the [RankStart, RankEnd) sub-combo
//     ranks: it unranks its first combo once, then walks to each next combination
//     incrementally (odometer), keeping a running prefix total of the first K-1
//     modules. This avoids the O(N) per-combo unranking and the K*S per-combo
//     stat accumulation of a strided walk, and keeps warps convergent.
//
// Modes (CollectMode in the cbuffer):
//   * 0 (solve):   per-thread top-K -> groupshared merge -> Output accumulates each
//                  (i, group)'s top-K across slices (thread 0 seeds its merge from
//                  the previous contents); requirement gates are enforced.
//   * 1 (collect): every combo whose score >= ScoreThreshold is appended to PoolOut
//                  (requirement gates ignored) to build the brute-force result cache.
//
// ModuleStats packs four 8-bit stat values per uint (module stats are link levels,
// <= 255); all per-stat buffers are zero-padded to a multiple of 4 entries so the
// kernel can loop word-wise without bounds checks.
//
// Each candidate is identified by (i, rank); the CPU re-expands rank -> module
// indices for the final results. Per-i enumeration keeps every combinatorial
// number in uint32 (no 64-bit math). Slices are sized <= COMBO_BUDGET sub-combos
// by the CPU, so rank arithmetic inside a slice cannot overflow uint32.
// ===========================================================================

#define THREADS 256
#define TOPK 10
#define MAX_STATS 32
#define MAX_K 10
#define SCORE_INVALID (-2147483647)

cbuffer Params : register(b0)
{
    uint NumModules;      // N (filtered module count)
    uint K;               // combo size (1..10)
    uint NumStats;        // S (distinct normalized stats, <= MAX_STATS); buffers padded to ceil4(S)
    uint ScoreModeV;      // 0 = ZScore, 1 = CombatPower
    uint HasExact;        // unused (Exactly is enforced per stat via StatExact); kept for cbuffer layout
    uint MaxTotal;        // K*20, last valid index into LinkTotalFight
    uint GroupsX;         // G (grid.x dimension)
    uint IBase;           // first module row covered by this dispatch (added to groupId.y)
    uint RankStart;       // first sub-combo rank of this slice
    uint RankEnd;         // one past the last rank of this slice (clamped to subCount)
    uint OriginalScoring; // 1 = upstream ZScore (adds overcap points)
    uint CollectMode;     // 1 = append combos with score >= ScoreThreshold (gates ignored)
    int  ScoreThreshold;  // collect mode: minimum score to keep
};

StructuredBuffer<uint>  ModuleStats    : register(t0); // [N * ceil(S/4)] four packed 8-bit values per uint
StructuredBuffer<float> StatMul        : register(t1); // [ceil4(S)] ZScore weight: legendaryMul x orderBoost (0 = ignore)
StructuredBuffer<int>   StatReq        : register(t2); // [ceil4(S)] required link level
StructuredBuffer<int>   StatExact      : register(t3); // [ceil4(S)] 1 if Exactly mode
StructuredBuffer<uint>  Binom          : register(t4); // [(N+1)*(K+1)] C(n,k)
StructuredBuffer<int>   LinkBonus      : register(t5); // [6] link-level breakpoint bonus
StructuredBuffer<int>   StatCombat     : register(t6); // [ceil4(S)*6] combat FightValue per (stat, tier)
StructuredBuffer<int>   LinkTotalFight : register(t7); // [MaxTotal+1] combat bonus per total link
StructuredBuffer<int>   StatCap        : register(t8); // [ceil4(S)] upper-bound cap per stat (huge sentinel = no cap)

struct Candidate
{
    int  Score;
    uint I;     // first module index
    uint Rank;  // lexicographic rank of the chosen K-1 offsets (decoded on CPU)
};

RWStructuredBuffer<Candidate>     Output  : register(u0); // solve mode: per-(i,group) top-K
AppendStructuredBuffer<Candidate> PoolOut : register(u1); // collect mode: threshold pool

groupshared int  gScore[THREADS * TOPK];
groupshared uint gRank[THREADS * TOPK];

uint Binom2(uint n, uint k)
{
    return Binom[n * (K + 1) + k];
}

int LinkTier(int v)
{
    // returns 0..5 for thresholds [1,4,8,12,16,20]; value 0..3 -> 0
    if (v >= 20) return 5;
    if (v >= 16) return 4;
    if (v >= 12) return 3;
    if (v >= 8)  return 2;
    if (v >= 4)  return 1;
    return 0;
}

int SnapBp(int v)
{
    // snaps to the highest reached breakpoint level [1,4,8,12,16,20]; below 1 -> 0
    if (v >= 20) return 20;
    if (v >= 16) return 16;
    if (v >= 12) return 12;
    if (v >= 8)  return 8;
    if (v >= 4)  return 4;
    if (v >= 1)  return 1;
    return 0;
}

// Insert (score, rank) into a descending top-K kept in local arrays.
void LocalInsert(inout int s[TOPK], inout uint rk[TOPK], int score, uint rank)
{
    if (score <= s[TOPK - 1])
        return;

    int pos = TOPK - 1;
    [unroll]
    for (int n = TOPK - 2; n >= 0; n--)
    {
        if (s[n] < score)
        {
            s[n + 1] = s[n];
            rk[n + 1] = rk[n];
            pos = n;
        }
    }
    s[pos] = score;
    rk[pos] = rank;
}

// Adds module modIdx's (packed) stat values into the running prefix totals.
void AddModuleStats(inout int prefix[MAX_STATS], uint modIdx, uint sPacked)
{
    for (uint w = 0; w < sPacked; w++)
    {
        uint word = ModuleStats[modIdx * sPacked + w];
        uint b = w * 4;
        prefix[b + 0] += (int)( word        & 0xFF);
        prefix[b + 1] += (int)((word >> 8)  & 0xFF);
        prefix[b + 2] += (int)((word >> 16) & 0xFF);
        prefix[b + 3] += (int)((word >> 24) & 0xFF);
    }
}

// Scores prefix + lastMod under the active mode; `valid` reports the requirement
// gates (ignored by the caller in collect mode). Padded stat lanes are all-zero
// (mul 0, req 0, combat 0), so looping the padded range needs no bounds checks.
int ScoreCombo(int prefix[MAX_STATS], uint lastMod, uint sPacked, out bool valid)
{
    bool reqOk = true;
    int score = 0;
    int totalSum = 0;

    for (uint w = 0; w < sPacked; w++)
    {
        uint word = ModuleStats[lastMod * sPacked + w];
        [unroll]
        for (uint b = 0; b < 4; b++)
        {
            uint s = w * 4 + b;
            int tv = prefix[s] + (int)((word >> (b * 8)) & 0xFF);

            // Exactly gates apply per stat: EVERY exact stat must hit its target.
            // (A former any-one-matches check let a combo violate a second Exactly
            // stat.) Raw equality on the total, same as ModuleOptimizerBeam.
            if (StatExact[s] != 0)
            {
                if (tv != StatReq[s])
                    reqOk = false;
            }
            else if (min(tv, 20) < StatReq[s])
                reqOk = false;
            // Upper-bound cap: this stat's raw total must not exceed StatCap[s]
            // (a huge sentinel disables it). Cap 0 excludes the stat entirely.
            if (tv > StatCap[s])
                reqOk = false;

            if (ScoreModeV == 0)
            {
                // ZScore, mirrors ModuleOptimizerBeam.CalcScore. Enhanced: no points
                // past the level-20 breakpoint (favors 20/20/16 over 30/30).
                // Original: upstream behavior, unweighted overcap (tv - bp) added.
                float wgt = StatMul[s];
                if (wgt > 0)
                {
                    int tvc = min(tv, 50);
                    int bp = SnapBp(tvc);
                    int statScore = (int)((bp * LinkBonus[LinkTier(tvc)]) * wgt);
                    if (OriginalScoring != 0)
                        statScore += tvc - bp;
                    score += statScore;
                }
            }
            else
            {
                // Combat power, mirrors CalcCombosCombatScore.
                totalSum += tv;
                if (tv >= 1)
                    score += StatCombat[s * 6 + LinkTier(tv)];
            }
        }
    }

    if (ScoreModeV != 0)
    {
        if (totalSum < 0) totalSum = 0;
        if ((uint)totalSum > MaxTotal) totalSum = (int)MaxTotal;
        score += LinkTotalFight[totalSum];
    }

    valid = reqOk;
    return score;
}

[numthreads(THREADS, 1, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint3 gtid : SV_GroupThreadID)
{
    uint i = IBase + groupId.y;
    uint tid = gtid.x;

    // Uniform per group (i comes from groupId.y), so early-out is barrier-safe.
    if (i + K > NumModules)
        return;

    uint m = NumModules - i - 1;   // count of modules after i
    uint t = K - 1;                // choose t from m
    uint subCount = Binom2(m, t);
    uint rankEnd = min(subCount, RankEnd);
    uint sPacked = (NumStats + 3) / 4;

    int  sBest[TOPK];
    uint rBest[TOPK];
    [unroll]
    for (int z = 0; z < TOPK; z++)
    {
        sBest[z] = SCORE_INVALID;
        rBest[z] = 0;
    }

    // Contiguous chunk per thread. `remaining` <= COMBO_BUDGET (CPU slicing), so
    // chunk/offset arithmetic stays far below uint32 range. All flow control below
    // is kept flat (no loops nested inside thread-varying branches): fxc's X4026
    // reconvergence analysis rejects such nesting ahead of the group sync.
    uint remaining = (RankStart < rankEnd) ? (rankEnd - RankStart) : 0;
    uint totalThreads = GroupsX * THREADS;
    uint globalId = groupId.x * THREADS + tid;
    uint chunk = (remaining + totalThreads - 1) / totalThreads;
    uint offset = globalId * chunk;
    bool hasWork = offset < remaining;
    uint myStart = RankStart + (hasWork ? offset : 0);
    uint myCount = hasWork ? min(chunk, remaining - offset) : 0;

    if (t == 0)
    {
        // K == 1: the row's single combo is module i itself (rank 0). Score
        // unconditionally (uniform flow); gate only the emit on hasWork.
        int prefix0[MAX_STATS];
        [unroll]
        for (uint z0 = 0; z0 < MAX_STATS; z0++) prefix0[z0] = 0;

        bool valid0;
        int score0 = ScoreCombo(prefix0, i, sPacked, valid0);
        if (CollectMode != 0)
        {
            if (myCount != 0 && score0 >= ScoreThreshold)
            {
                Candidate c0;
                c0.Score = score0;
                c0.I = i;
                c0.Rank = 0;
                PoolOut.Append(c0);
            }
        }
        else if (myCount != 0 && valid0)
        {
            LocalInsert(sBest, rBest, score0, 0);
        }
    }
    else
    {
        // ---- unrank myStart -> t increasing offsets in [0, m) (once per thread) ----
        // Runs even for idle threads (myCount == 0, garbage rank): it terminates in
        // <= m + t steps regardless and the main loop below then runs zero times.
        uint chosen[MAX_K];
        uint rank = myStart;
        uint c = 0;
        for (uint j = 0; j < t; j++)
        {
            while (c < m)
            {
                uint cnt = Binom2(m - c - 1, t - j - 1);
                if (rank < cnt) break;
                rank -= cnt;
                c += 1;
            }
            chosen[j] = min(c, m - 1);
            c += 1;
        }

        // ---- prefix totals over module i + chosen[0..t-2] ----
        int prefix[MAX_STATS];
        [unroll]
        for (uint z1 = 0; z1 < MAX_STATS; z1++) prefix[z1] = 0;
        AddModuleStats(prefix, i, sPacked);
        for (uint q = 0; q + 1 < t; q++)
            AddModuleStats(prefix, i + 1 + chosen[q], sPacked);

        // Uniform trip count (chunk): fxc only accepts a pre-sync loop whose bound is
        // non-varying. Threads guard their live range via myCount inside the body.
        for (uint step = 0; step < chunk; step++)
        {
            bool valid;
            int score = ScoreCombo(prefix, i + 1 + chosen[t - 1], sPacked, valid);

            if (CollectMode != 0)
            {
                if (step < myCount && score >= ScoreThreshold)
                {
                    Candidate cand;
                    cand.Score = score;
                    cand.I = i;
                    cand.Rank = myStart + step;
                    PoolOut.Append(cand);
                }
            }
            else if (step < myCount && valid)
            {
                LocalInsert(sBest, rBest, score, myStart + step);
            }

            // ---- advance to the next combination (odometer) ----
            if (step + 1 < myCount)
            {
                // Bounded scan (not a while) so fxc can prove termination.
                uint jj = t - 1;
                [loop]
                for (uint d = 0; d < MAX_K; d++)
                {
                    if (jj == 0 || chosen[jj] != m - t + jj)
                        break;
                    jj--;
                }
                chosen[jj] += 1;
                if (jj != t - 1)
                {
                    for (uint q2 = jj + 1; q2 < t; q2++)
                        chosen[q2] = chosen[q2 - 1] + 1;

                    // Prefix modules changed: rebuild i + chosen[0..t-2].
                    [unroll]
                    for (uint z2 = 0; z2 < MAX_STATS; z2++) prefix[z2] = 0;
                    AddModuleStats(prefix, i, sPacked);
                    for (uint q3 = 0; q3 + 1 < t; q3++)
                        AddModuleStats(prefix, i + 1 + chosen[q3], sPacked);
                }
            }
        }
    }

    // Collect mode never touches Output. CollectMode comes from the cbuffer, so
    // this branch is non-varying and the group sync inside it stays legal.
    if (CollectMode != 0)
        return;

    // ---- publish local top-K to groupshared ----
    [unroll]
    for (int w2 = 0; w2 < TOPK; w2++)
    {
        uint gi = tid * TOPK + w2;
        gScore[gi] = sBest[w2];
        gRank[gi] = rBest[w2];
    }

    GroupMemoryBarrierWithGroupSync();

    // ---- thread 0 merges the group's THREADS*TOPK entries into a group top-K ----
    if (tid == 0)
    {
        uint baseOut = (i * GroupsX + groupId.x) * TOPK;

        int  ms[TOPK];
        uint mr[TOPK];
        // Seed from the previous slices' results for this (i, group) so the top-K
        // accumulates across the CPU-issued rank slices (buffer is pre-cleared to
        // SCORE_INVALID before the first slice).
        [unroll]
        for (int q4 = 0; q4 < TOPK; q4++) { ms[q4] = Output[baseOut + q4].Score; mr[q4] = Output[baseOut + q4].Rank; }

        uint count = THREADS * TOPK;
        for (uint e = 0; e < count; e++)
        {
            int sc = gScore[e];
            if (sc <= ms[TOPK - 1])
                continue;
            int pos = TOPK - 1;
            for (int n = TOPK - 2; n >= 0; n--)
            {
                if (ms[n] < sc)
                {
                    ms[n + 1] = ms[n];
                    mr[n + 1] = mr[n];
                    pos = n;
                }
            }
            ms[pos] = sc;
            mr[pos] = gRank[e];
        }

        [unroll]
        for (int o = 0; o < TOPK; o++)
        {
            Candidate cand;
            cand.Score = ms[o];
            cand.I = i;
            cand.Rank = mr[o];
            Output[baseOut + o] = cand;
        }
    }
}
