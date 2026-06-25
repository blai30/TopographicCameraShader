using System;
using Godot;

namespace TopographicMap;

// Compositor effect that turns the top-down camera depth buffer into a height
// buffer (normalized world height in R, terrain/background coverage mask in G)
// via a compute shader. Attach via a Compositor on the map camera only, so it
// never affects the main view or the editor. Topographic styling (tint and
// contours) is applied downstream by consumers, so this stage produces data.
[Tool]
[GlobalClass]
public partial class TopographicCompositorEffect : CompositorEffect
{
    [Export] public float HeightMin = -40.0f;
    [Export] public float HeightMax = 110.0f;
    [Export] public float CameraY = 200.0f;
    [Export] public float NearPlane = 80.0f;
    [Export] public float FarPlane = 245.0f;
    [Export] public bool DepthReversed = true;

    private RenderingDevice _rd;
    private Rid _shader;
    private Rid _pipeline;
    private Rid _depthSampler;
    private bool _ready;

    public TopographicCompositorEffect()
    {
        EffectCallbackType = EffectCallbackTypeEnum.PreTransparent;
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

        _ready = _pipeline.IsValid && _depthSampler.IsValid;
    }

    public override void _Notification(int what)
    {
        if (what != NotificationPredelete || _rd == null) return;
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

        uint xGroups = ((uint)size.X - 1) / 8 + 1;
        uint yGroups = ((uint)size.Y - 1) / 8 + 1;

        float[] pushConstant =
        [
            size.X, size.Y, CameraY, NearPlane,
            FarPlane, HeightMin, HeightMax, DepthReversed ? 1.0f : 0.0f
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

            var uniformSet = UniformSetCacheRD.GetCache(_shader, 0,
                [colorUniform, depthUniform]);

            long computeList = _rd.ComputeListBegin();
            _rd.ComputeListBindComputePipeline(computeList, _pipeline);
            _rd.ComputeListBindUniformSet(computeList, uniformSet, 0);
            _rd.ComputeListSetPushConstant(computeList, pushBytes, (uint)pushBytes.Length);
            _rd.ComputeListDispatch(computeList, xGroups, yGroups, 1);
            _rd.ComputeListEnd();
        }
    }
}
