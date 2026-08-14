using Godot;

namespace ColdOrbit.SimCore;

// Plays looping alert tones for unacknowledged caution/warning alerts.
// caution.ogg loops while any caution-level alert is unacknowledged.
// warning.ogg loops while any warning-level alert is unacknowledged.
// Both players run independently and can overlap.
public partial class AlertAudio : Node
{
	private AudioStreamPlayer _cautionPlayer;
	private AudioStreamPlayer _warningPlayer;

	public override void _Ready()
	{
		_cautionPlayer = MakeLoopingPlayer("res://sounds/caution.ogg");
		_warningPlayer = MakeLoopingPlayer("res://sounds/warning.ogg");
		AddChild(_cautionPlayer);
		AddChild(_warningPlayer);
	}

	public override void _Process(double _delta)
	{
		if (SimBus.Instance?.Alerts == null) return;

		var active = SimBus.Instance.Alerts.Active;
		bool hasCaution = false;
		bool hasWarning  = false;
		foreach (var a in active)
		{
			if (a.Acknowledged) continue;
			if (a.Severity == "caution") hasCaution = true;
			if (a.Severity == "warning")  hasWarning  = true;
		}

		SetLooping(_cautionPlayer, hasCaution);
		SetLooping(_warningPlayer, hasWarning);
	}

	private static void SetLooping(AudioStreamPlayer player, bool shouldPlay)
	{
		if (shouldPlay && !player.Playing)  player.Play();
		if (!shouldPlay && player.Playing)  player.Stop();
	}

	private static AudioStreamPlayer MakeLoopingPlayer(string resPath)
	{
		var stream = AudioStreamOggVorbis.LoadFromFile(resPath);
		stream.Loop = true;
		return new AudioStreamPlayer { Stream = stream, Autoplay = false };
	}
}
