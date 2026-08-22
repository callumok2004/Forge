cbuffer AmbientConstants : register(b0)
{
	float4 Frame;
	float4 Pointer;
	float4 Accent;
	float4 Language;
	float4 Signals[3];
};

#define SIG_BUILD    Signals[0].x
#define SIG_DEBUG    Signals[0].y
#define SIG_TESTS    Signals[0].z
#define SIG_ERROR    Signals[0].w
#define SIG_WARNING  Signals[1].x
#define SIG_GIT      Signals[1].y
#define SIG_CLAUDE   Signals[1].z
#define SIG_TYPING   Signals[1].w
#define SIG_SEARCH   Signals[2].x
#define SIG_NOTIFY   Signals[2].y
#define SIG_TASK     Signals[2].z

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

float Combine(float a, float b)
{
	return 1.0 - (1.0 - a) * (1.0 - b);
}

float Dither(float2 pixel)
{
	return frac(52.9829189 * frac(dot(pixel, float2(0.06711056, 0.00583715))));
}

float Perimeter(float2 pixel, float2 size)
{
	float2 centred = (pixel / size - 0.5) * float2(size.x / max(size.y, 1.0), 1.0);
	return atan2(centred.y, centred.x) * 0.15915494 + 0.5;
}

float3 Mix(inout float weight, float3 color, float3 tint, float level)
{
	weight += level;
	return color + tint * level;
}

float4 PSMain(Fragment input) : SV_Target
{
	float2 uv = input.Uv;
	float2 pixel = uv * Frame.xy;
	float time = Frame.z;
	float focus = Pointer.z;
	float idle = Pointer.w;

	float progress = Combine(Combine(SIG_BUILD, SIG_TESTS), Combine(SIG_DEBUG, SIG_TASK));
	float running = Combine(progress, Combine(SIG_SEARCH, SIG_CLAUDE));
	float alerting = Combine(SIG_ERROR, SIG_WARNING);
	float ambientActivity = Combine(alerting, Combine(SIG_GIT, Combine(SIG_TYPING, SIG_NOTIFY)));
	float activity = Combine(ambientActivity, running * 0.35);

	float3 base = lerp(Accent.rgb, Language.rgb, Language.a * 0.75);

	float weight = 1.0;
	float3 tinted = base;
	tinted = Mix(weight, tinted, float3(0.28, 0.56, 1.00), SIG_BUILD);
	tinted = Mix(weight, tinted, float3(1.00, 0.64, 0.18), SIG_DEBUG);
	tinted = Mix(weight, tinted, float3(0.22, 0.86, 0.62), SIG_TESTS);
	tinted = Mix(weight, tinted, float3(1.00, 0.32, 0.34), SIG_ERROR);
	tinted = Mix(weight, tinted, float3(1.00, 0.78, 0.24), SIG_WARNING);
	tinted = Mix(weight, tinted, float3(0.38, 0.80, 0.46), SIG_GIT);
	tinted = Mix(weight, tinted, float3(0.80, 0.46, 1.00), SIG_CLAUDE);
	tinted = Mix(weight, tinted, float3(0.44, 0.72, 1.00), SIG_SEARCH);
	tinted = Mix(weight, tinted, float3(0.58, 0.82, 1.00), SIG_TYPING);
	tinted = Mix(weight, tinted, float3(1.00, 0.58, 0.78), SIG_NOTIFY);

	float3 color = tinted / weight;

	float span = max(Frame.y, 1.0);
	float arc = Perimeter(pixel, Frame.xy);
	float turns = 6.2831853;

	float wave = 0.62
		+ 0.22 * sin(turns * (2.0 * arc - time * 0.055))
		+ 0.16 * sin(turns * (3.0 * arc + time * 0.037));

	float2 toEdge = min(pixel, Frame.xy - pixel);
	float nearest = min(toEdge.x, toEdge.y);
	float border = exp(-nearest / (span * lerp(0.042, 0.060, activity)));

	float pools = border * wave;

	float2 pointerPixel = Pointer.xy * Frame.xy;
	float pointerDistance = length(pixel - pointerPixel) / span;
	float halo = exp(-pointerDistance * pointerDistance * 2.6) * focus * (1.0 - idle * 0.9) * border;

	float strength = 0.085 + 0.14 * ambientActivity + 0.03 * running;
	float alpha = pools * strength + halo * 0.055;

	float fromBottom = Frame.y - pixel.y;
	float bar = exp(-max(fromBottom - 1.5, 0.0) / 1.6);
	float bloom = exp(-fromBottom / 16.0);

	float sweep = Ease(abs(frac(time * 0.30) * 2.0 - 1.0));
	float centre = lerp(0.14, 0.86, sweep);
	float extent = saturate(1.0 - abs(uv.x - centre) / 0.19);
	float segment = Ease(Ease(extent)) * progress;

	alpha += segment * (bar * 0.62 + bloom * 0.16);

	alpha *= lerp(0.42, 1.0, focus);
	alpha *= lerp(1.0, 0.62, idle);
	alpha = saturate(alpha) * Accent.a;

	float4 result = float4(color * alpha, alpha);

	return result + (Dither(pixel) - 0.5) * 0.0045;
}
