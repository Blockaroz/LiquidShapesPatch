sampler uImage0 : register(s0);

texture uMaskTexture;
sampler2D maskTexture0 = sampler_state
{
    texture = <uMaskTexture>;
    magfilter = LINEAR;
    minfilter = LINEAR;
    mipfilter = LINEAR;
    AddressU = wrap;
    AddressV = wrap;
};
bool invert;

float4 PixelShaderFunction(float4 baseColor : COLOR0, float2 coords : TEXCOORD0) : COLOR0
{
    float4 baseImage = tex2D(uImage0, coords);   
    float alpha = tex2D(maskTexture0, coords).a;

    return baseImage * baseColor * (invert ? 1 - alpha : alpha);
}

technique Technique1
{
    pass ShaderPass
    {
        PixelShader = compile ps_2_0 PixelShaderFunction();
    }
}