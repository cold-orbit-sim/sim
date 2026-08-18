using Godot;

namespace ColdOrbit.SimCore;

// Fullscreen flash on FTL jump execution.
// Triggers when the phase transitions Jumping → Cooldown (the moment the
// jump fires and SceneManager swaps the SoI). Fades from white-blue to
// transparent over FlashDuration seconds.
public partial class FtlFlash : CanvasLayer
{
    [Export] public float FlashDuration { get; set; } = 0.6f;
    [Export] public Color FlashColor { get; set; } = new Color(0.75f, 0.88f, 1.0f, 1.0f);

    private ColorRect _rect;
    private FtlPhase _prevPhase = FtlPhase.Idle;
    private float _fadeTimer = 0f;

    public override void _Ready()
    {
        Layer = 100;

        _rect = new ColorRect();
        _rect.Color = new Color(FlashColor.R, FlashColor.G, FlashColor.B, 0f);
        _rect.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_rect);
    }

    public override void _Process(double delta)
    {
        var phase = SimBus.Instance?.Ftl.Phase ?? FtlPhase.Idle;

        if (_prevPhase == FtlPhase.Jumping && phase == FtlPhase.Cooldown)
        {
            _fadeTimer = FlashDuration;
        }
        _prevPhase = phase;

        if (_fadeTimer > 0f)
        {
            _fadeTimer -= (float)delta;
            float alpha = Mathf.Max(0f, _fadeTimer / FlashDuration);
            _rect.Color = new Color(FlashColor.R, FlashColor.G, FlashColor.B, alpha);
        }
    }
}
