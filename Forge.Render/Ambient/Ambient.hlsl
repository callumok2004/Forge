cbuffer AmbientConstants : register(b0)
{
	float4 Frame;
	float4 Glow;
	float4 Loader;
};

struct Fragment
{
	float4 Position : SV_Position;
	float2 Uv : TEXCOORD0;
};

Fragment VSMain(uint id : SV_VertexID)
{
	Fragment output;
	float2 uv = float2((id << 1) & 2, id & 2);
	output.Uv = uv;
	output.Position = float4(uv * float2(2.0, -2.0) + float2(-1.0, 1.0), 0.0, 1.0);
	return output;
}

float Ease(float x)
{
	return x * x * (3.0 - 2.0 * x);
}

float Dither(float2 pixel)
{
	return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float GlowFalloff(float2 pixel)
{
	float2 radii = Frame.xy * float2(1.2, 0.38);
	float distance = length((pixel - float2(Frame.x * 0.5, Frame.y)) / max(radii, 1.0));

	return distance < 0.55
		? lerp(0.26, 0.10, distance / 0.55)
		: lerp(0.10, 0.0, saturate((distance - 0.55) / 0.45));
}

float4 PSMain(Fragment input) : SV_Target
{
	float2 uv = input.Uv;
	float2 pixel = float2(uv.x * Frame.x, Frame.y - Frame.w + uv.y * Frame.w);
	float time = Frame.z;

	float4 result = Glow * GlowFalloff(pixel);

	float fromBottom = Frame.y - pixel.y;
	float bar = exp(-max(fromBottom - 1.5, 0.0) / 1.6);
	float bloom = exp(-fromBottom / 16.0);

	float sweep = Ease(abs(frac(time * 0.30) * 2.0 - 1.0));
	float centre = lerp(0.14, 0.86, sweep);
	float extent = saturate(1.0 - abs(uv.x - centre) / 0.19);
	float segment = Ease(Ease(extent)) * Loader.a;

	float loader = saturate(segment * (bar * 0.62 + bloom * 0.16));
	result = float4(Loader.rgb * loader, loader) + result * (1.0 - loader);

	return result + (Dither(pixel) - 0.5) * 0.0045 * step(0.0005, result.a);
}
