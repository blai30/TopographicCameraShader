using System;
using Godot;

namespace TopographicMap;

// Compositor effect that turns the top-down camera depth buffer into a
// topographic map (hypsometric tint plus contour lines) via a compute shader.
// Attach via a Compositor on the map camera only, so it never affects the
// terrain in the main view or the editor.
[Tool]
[GlobalClass]
public partial class TopographicCompositorEffect : CompositorEffect
{
    [Export] public float HeightMin = 0.0f;
    [Export] public float HeightMax = 110.0f;
    [Export] public float ContourInterval = 10.0f;
    [Export] public float ContourWidthPixels = 1.5f;

    // Every Nth contour is drawn thicker as a major contour. Set to 0 to disable.
    [Export] public float MajorContourEvery = 5.0f;
    [Export] public float MajorContourWidthMultiplier = 2.2f;

    [Export] public float SeaLevel = 0.0f;
    [Export] public float CameraY = 200.0f;
    [Export] public float NearPlane = 80.0f;
    [Export] public float FarPlane = 245.0f;
    [Export] public bool DepthReversed = true;
    [Export] public GradientTexture1D ColorRamp;

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _depthSampler;
    private Rid _rampSampler;
    private GradientTexture1D _defaultRamp;
    private GradientTexture1D _cachedRampSource;
    private Rid _cachedRampTexture;
    private bool _ready;

    public TopographicCompositorEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;
        _defaultRamp = BuildDefaultRamp();
        _rd = RenderingServer.GetRenderingDevice();
        if (_rd == null)
        {
            return;
        }

        var shaderFile = GD.Load<RDShaderFile>("res://addons/topographic/topographic.glsl");
        if (shaderFile == null)
        {
            return;
        }

        var spirv = shaderFile.GetSpirV();
        _shader = _rd.ShaderCreateFromSpirV(spirv);
        if (!_shader.IsValid)
        {
            return;
        }

        _pipeline = _rd.ComputePipelineCreate(_shader);

        var nearest = new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Nearest,
            MagFilter = RenderingDevice.SamplerFilter.Nearest,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        };
        _depthSampler = _rd.SamplerCreate(nearest);

        var linear = new RDSamplerState
        {
            MinFilter = RenderingDevice.SamplerFilter.Linear,
            MagFilter = RenderingDevice.SamplerFilter.Linear,
            RepeatU = RenderingDevice.SamplerRepeatMode.ClampToEdge,
            RepeatV = RenderingDevice.SamplerRepeatMode.ClampToEdge
        };
        _rampSampler = _rd.SamplerCreate(linear);

        _ready = _pipeline.IsValid && _depthSampler.IsValid && _rampSampler.IsValid;
    }

    private static GradientTexture1D BuildDefaultRamp()
    {
        var gradient = new Gradient
        {
            Offsets = [0.0f, 0.15f, 0.4f, 0.65f, 0.85f, 1.0f],
            Colors =
            [
                new(0.45f, 0.62f, 0.35f),
                new(0.40f, 0.58f, 0.30f),
                new(0.72f, 0.64f, 0.44f),
                new(0.56f, 0.42f, 0.32f),
                new(0.45f, 0.34f, 0.28f),
                new(0.97f, 0.97f, 0.99f)
            ]
        };
        return new() { Gradient = gradient, Width = 256 };
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _rd == null) return;
        // Free render resources deterministically.
        if (_rampSampler.IsValid)
        {
            _rd.FreeRid(_rampSampler);
        }

        if (_depthSampler.IsValid)
        {
            _rd.FreeRid(_depthSampler);
        }

        if (_pipeline.IsValid)
        {
            _rd.FreeRid(_pipeline);
        }

        if (_shader.IsValid)
        {
            _rd.FreeRid(_shader);
        }
    }

    public override void _RenderCallback(int effectCallbackType, RenderData renderData)
    {
        if (!_ready || effectCallbackType != (int)EffectCallbackTypeEnum.PreTransparent)
        {
            return;
        }

        if (renderData.GetRenderSceneBuffers() is not RenderSceneBuffersRD sceneBuffers)
        {
            return;
        }

        var size = sceneBuffers.GetInternalSize();
        if (size.X == 0 || size.Y == 0)
        {
            return;
        }

        // Cache the ramp's render-device texture; only re-query when it changes,
        // to avoid per-frame RenderingServer calls from the render thread.
        var ramp = ColorRamp ?? _defaultRamp;
        if (ramp != _cachedRampSource)
        {
            _cachedRampTexture = RenderingServer.TextureGetRdTexture(ramp.GetRid());
            _cachedRampSource = ramp;
        }

        if (!_cachedRampTexture.IsValid)
        {
            return;
        }

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        float[] pushConstant =
        [
            size.X, size.Y, CameraY, NearPlane,
            FarPlane, HeightMin, HeightMax, ContourInterval,
            ContourWidthPixels, SeaLevel, DepthReversed ? 1.0f : 0.0f, MajorContourEvery,
            MajorContourWidthMultiplier, 0.0f
        ];
        byte[] pushBytes = new byte[pushConstant.Length * sizeof(float)];
        Buffer.BlockCopy(pushConstant, 0, pushBytes, 0, pushBytes.Length);

        uint viewCount = sceneBuffers.GetViewCount();
        for (uint view = 0; view < viewCount; view++)
        {
            var colorImage = sceneBuffers.GetColorLayer(view);
            var depthImage = sceneBuffers.GetDepthLayer(view);

            var colorUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.Image,
                Binding = 0
            };
            colorUniform.AddId(colorImage);

            var depthUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.SamplerWithTexture,
                Binding = 1
            };
            depthUniform.AddId(_depthSampler);
            depthUniform.AddId(depthImage);

            var rampUniform = new RDUniform
            {
                UniformType = RenderingDevice.UniformType.SamplerWithTexture,
                Binding = 2
            };
            rampUniform.AddId(_rampSampler);
            rampUniform.AddId(_cachedRampTexture);

            var uniformSet = UniformSetCacheRD.GetCache(_shader, 0,
                [colorUniform, depthUniform, rampUniform]);

            long computeList = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(computeList, _pipeline);
            _rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
            _rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
            _rd.ComputeListDispatch(computeList, xGroups, yGroups, 1);
            _rd.ComputeListEnd();
        }
    }
}
