// Fisheye / barrel lens warp for the kinetic lyric stage.
// Destination pixel radius maps to a smaller source radius near the center,
// so type swells as it passes the middle of the lens and stays put at the rim.

sampler2D implicitInputSampler : register(s0);

float2 Center   : register(c0);
float  Strength : register(c1);
float  Aspect   : register(c2);

float4 main(float2 uv : TEXCOORD) : COLOR
{
    float2 d = uv - Center;
    d.x *= Aspect;

    float r  = max(length(d), 0.0001);
    float R  = 0.72;
    float rn = saturate(r / R);

    // Magnification falls off smoothly from the center to the rim.
    float m = 1.0 + Strength * (1.0 - rn * rn);

    float2 suv = Center + float2(d.x / (Aspect * m), d.y / m);
    float4 col = tex2D(implicitInputSampler, suv);

    // Soft glass falloff keeps the rim feeling curved rather than stretched flat.
    float vig = 1.0 - 0.16 * rn * rn;
    col.rgb *= vig;

    return col;
}
