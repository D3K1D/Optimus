// FP32 FMA throughput kernel, benchVersion 1. Compiled to fma.cso (cs_5_0, see Gpu/Shaders.cs).
// Each thread runs 8 independent chains x = x * A + B for Iterations x 8 steps: 128 flops per thread per iteration.
// The CPU reference in GpuValidator mirrors this exactly in double.

cbuffer Params : register(b0)
{
    uint Iterations;
    float A;
    float B;
    uint Pad;
};

RWStructuredBuffer<float> Output : register(u0);

[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    float x0 = (float)(id.x & 1023u) * (1.0f / 1024.0f);
    float x1 = x0 + 0.125f;
    float x2 = x0 + 0.25f;
    float x3 = x0 + 0.375f;
    float x4 = x0 + 0.5f;
    float x5 = x0 + 0.625f;
    float x6 = x0 + 0.75f;
    float x7 = x0 + 0.875f;

    [loop]
    for (uint i = 0; i < Iterations; i++)
    {
        [unroll]
        for (uint k = 0; k < 8; k++)
        {
            x0 = mad(x0, A, B);
            x1 = mad(x1, A, B);
            x2 = mad(x2, A, B);
            x3 = mad(x3, A, B);
            x4 = mad(x4, A, B);
            x5 = mad(x5, A, B);
            x6 = mad(x6, A, B);
            x7 = mad(x7, A, B);
        }
    }

    Output[id.x] = ((x0 + x1) + (x2 + x3)) + ((x4 + x5) + (x6 + x7));
}
