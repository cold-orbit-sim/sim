using Godot;

namespace ColdOrbit.SimCore;

// Procedural starfield skybox -- gives rotation feedback in an otherwise
// featureless void. Stars are effectively at infinity so this shows
// rotation only, not translation (see DebrisField for that).
//
// Godot's built-in ProceduralSkyMaterial has no star option, so this bakes
// a scattering of white points onto an equirectangular panorama texture at
// startup and assigns it via a PanoramaSkyMaterial. Placeholder quality --
// swap for a real panorama texture later if needed.
public partial class StarfieldSky : WorldEnvironment
{
    [Export] public int StarCount { get; set; } = 3000;
    [Export] public int PanoramaWidth { get; set; } = 2048;
    [Export] public int PanoramaHeight { get; set; } = 1024;

    public override void _Ready()
    {
        if (Environment == null) return;

        var image = Image.CreateEmpty(PanoramaWidth, PanoramaHeight, false, Image.Format.Rgb8);
        image.Fill(Colors.Black);

        var rng = new RandomNumberGenerator();
        rng.Randomize();

        for (int i = 0; i < StarCount; i++)
        {
            int x = rng.RandiRange(0, PanoramaWidth - 1);
            int y = rng.RandiRange(0, PanoramaHeight - 1);
            float brightness = rng.RandfRange(0.4f, 1.0f);
            image.SetPixel(x, y, new Color(brightness, brightness, brightness));
        }

        var panoramaTexture = ImageTexture.CreateFromImage(image);

        Environment.BackgroundMode = Godot.Environment.BGMode.Sky;
        Environment.Sky = new Sky
        {
            SkyMaterial = new PanoramaSkyMaterial { Panorama = panoramaTexture }
        };
    }
}
