// ===========================================================================
// ModuleSolver.hlsl  -  DirectCompute (Shader Model 5.0) module-combo solver
// ---------------------------------------------------------------------------
// One thread evaluates one K-module combination (K = 1..10), scoring it with
// either the priority-weighted "ZScore" (mirrors InnerStatsWeightCalcs) or the
// in-game "combat power" (mirrors CalcCombosCombatScore).
//
// Dispatch grid = (G, numI, 1):
//   * groupId.y = i              -> the fixed first module index of the combo.
//   * groupId.x in [0, G)        -> a slice of the C(N-i-1, K-1) sub-combos,
//                                   walked with a grid-stride loop.
// Per-i enumeration keeps every combinatorial number in uint32 (no 64-bit math).
//
// Each candidate is identified by (i, rank) where rank is the lexicographic
// index of the chosen K-1 offsets; the CPU re-expands rank -> module indices
// for the final top-10, so the kernel stays K-agnostic and light on registers.
// ===========================================================================

#define THREADS 64
#define TOPK 10
#define MAX_STATS 32
#define MAX_K 10
#define SCORE_INVALID (-2147483647)

cbuffer Params : register(b0)
{
    uint NumModules;    // N (filtered module count)
    uint K;             // combo size (1..10)
    uint NumStats;      // S (distinct normalized stats, <= MAX_STATS)
    uint ScoreModeV;    // 0 = ZScore, 1 = CombatPower
    uint HasExact;      // 1 if any priority uses StatMode.Exactly
    uint MaxTotal;      // K*20, last valid index into LinkTotalFight
    uint GroupsX;       // G (grid.x dimension)
    uint _pad0;
};

StructuredBuffer<uint> ModuleStats   : register(t0); // [N*S]   stat value per (module, stat)
StructuredBuffer<int>  StatMul        : register(t1); // [S]     priority multiplier (0 = ignore)
StructuredBuffer<int>  StatReq        : register(t2); // [S]     required link level
StructuredBuffer<int>  StatMin        : register(t3); // [S]     minimum link level (usually 0)
StructuredBuffer<int>  StatExact      : register(t4); // [S]     1 if Exactly mode
StructuredBuffer<uint> Binom          : register(t5); // [(N+1)*(K+1)] C(n,k)
StructuredBuffer<int>  LinkBonus       : register(t6); // [6]     link-level breakpoint bonus
StructuredBuffer<int>  StatCombat     : register(t7); // [S*6]   combat FightValue per (stat, tier)
StructuredBuffer<int>  LinkTotalFight : register(t8); // [MaxTotal+1] combat bonus per total link

struct Candidate
{
    int  Score;
    uint I;     // first module index
    uint Rank;  // lexicographic rank of the chosen K-1 offsets (decoded on CPU)
};

RWStructuredBuffer<Candidate> Output : register(u0);

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

[numthreads(THREADS, 1, 1)]
void CSMain(uint3 groupId : SV_GroupID, uint3 gtid : SV_GroupThreadID)
{
    uint i = groupId.y;
    uint groupX = groupId.x;
    uint tid = gtid.x;

    if (i + K > NumModules)
        return;

    uint m = NumModules - i - 1;   // count of modules after i
    uint t = K - 1;                // choose t from m
    uint subCount = Binom2(m, t);

    int  sBest[TOPK];
    uint rBest[TOPK];
    [unroll]
    for (int z = 0; z < TOPK; z++)
    {
        sBest[z] = SCORE_INVALID;
        rBest[z] = 0;
    }

    uint totalThreads = GroupsX * THREADS;
    uint globalId = groupX * THREADS + tid;

    for (uint r = globalId; r < subCount; r += totalThreads)
    {
        // ---- unrank r -> t increasing offsets in [0, m) (lexicographic) ----
        uint chosen[MAX_K];
        uint rank = r;
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
            chosen[j] = c;
            c += 1;
        }

        // ---- accumulate stat totals across the K modules ----
        int totals[MAX_STATS];
        [unroll]
        for (int s0 = 0; s0 < MAX_STATS; s0++)
            totals[s0] = 0;

        uint mod0 = i;
        for (uint s1 = 0; s1 < NumStats; s1++)
            totals[s1] += (int)ModuleStats[mod0 * NumStats + s1];

        for (uint jj = 0; jj < t; jj++)
        {
            uint modIdx = i + 1 + chosen[jj];
            for (uint s2 = 0; s2 < NumStats; s2++)
                totals[s2] += (int)ModuleStats[modIdx * NumStats + s2];
        }

        // ---- shared validity gate (required / exactly link levels) ----
        bool valid = true;

        if (HasExact != 0)
        {
            bool anyExact = false;
            for (uint se = 0; se < NumStats; se++)
            {
                if (StatExact[se] != 0 && totals[se] == StatReq[se])
                    anyExact = true;
            }
            if (!anyExact)
                valid = false;
        }

        if (valid)
        {
            for (uint sr = 0; sr < NumStats; sr++)
            {
                int mined = min(totals[sr], 20);
                if (mined < StatReq[sr]) { valid = false; break; }
            }
        }

        if (!valid)
            continue;

        // ---- score ----
        int score = 0;

        if (ScoreModeV == 0)
        {
            // ZScore (priority weighted), mirrors InnerStatsWeightCalcs
            for (uint s = 0; s < NumStats; s++)
            {
                int mined = min(totals[s], 20);
                int passed = (mined > StatMin[s]) ? mined : 0;
                int mul = StatMul[s];
                score += passed * mul;
                score += LinkBonus[LinkTier(passed)] * mul;
            }
        }
        else
        {
            // Combat power, mirrors CalcCombosCombatScore.
            int totalSum = 0;
            for (uint s = 0; s < NumStats; s++)
            {
                int v = totals[s];
                totalSum += v;
                if (v >= 1)
                    score += StatCombat[s * 6 + LinkTier(v)];
            }
            if (totalSum < 0) totalSum = 0;
            if ((uint)totalSum > MaxTotal) totalSum = (int)MaxTotal;
            score += LinkTotalFight[totalSum];
        }

        LocalInsert(sBest, rBest, score, r);
    }

    // ---- publish local top-K to groupshared ----
    [unroll]
    for (int w = 0; w < TOPK; w++)
    {
        uint gi = tid * TOPK + w;
        gScore[gi] = sBest[w];
        gRank[gi] = rBest[w];
    }

    GroupMemoryBarrierWithGroupSync();

    // ---- thread 0 merges the group's THREADS*TOPK entries into a group top-K ----
    if (tid == 0)
    {
        int  ms[TOPK];
        uint mr[TOPK];
        [unroll]
        for (int q = 0; q < TOPK; q++) { ms[q] = SCORE_INVALID; mr[q] = 0; }

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

        uint baseOut = (i * GroupsX + groupX) * TOPK;
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
