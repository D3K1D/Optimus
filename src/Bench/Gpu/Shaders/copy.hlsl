// Buffer copy kernel, benchVersion 1. Compiled to copy.cso (cs_5_0, see Gpu/Shaders.cs).
// One 16-byte element per thread; Count bounds the tail. Bandwidth counts bytes read + bytes written.

cbuffer Params : register(b0)
{
    uint Count;
    uint Pad0;
    uint Pad1;
    uint Pad2;
};

StructuredBuffer<uint4> Source : register(t0);
RWStructuredBuffer<uint4> Destination : register(u0);

[numthreads(256, 1, 1)]
void main(uint3 id : SV_DispatchThreadID)
{
    if (id.x < Count)
        Destination[id.x] = Source[id.x];
}
