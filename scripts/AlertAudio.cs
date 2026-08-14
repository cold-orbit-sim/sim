using Godot;
using System.Linq;

namespace ColdOrbit.SimCore;

// Plays synthesised alarm tones for unacknowledged caution/warning alerts.
// Uses AudioStreamGenerator (PCM push) so no audio files are needed.
// Both tones run on independent players and can overlap.
public partial class AlertAudio : Node
{
	[Export] public float CautionInterval { get; set; } = 1.4f; // seconds between woomps
	[Export] public float WarningInterval { get; set; } = 0.6f; // seconds between WAAHs

	private const int SampleRate = 22050;

	private AudioStreamPlayer _cautionPlayer;
	private AudioStreamPlayer _warningPlayer;
	private AudioStreamGeneratorPlayback _cautionPlayback;
	private AudioStreamGeneratorPlayback _warningPlayback;

	// Pre-generated tone sample arrays (stereo Vector2 pairs).
	private Vector2[] _cautionSamples;
	private Vector2[] _warningSamples;

	// Current read position into the sample array; -1 = not playing (pushing silence).
	private int _cautionPos = -1;
	private int _warningPos = -1;

	// Countdown timers; fire at 0 to start the next tone.
	private float _cautionTimer = 0f;
	private float _warningTimer = 0f;

	public override void _Ready()
	{
		_cautionSamples = GenerateCautionTone();
		_warningSamples = GenerateWarningTone();

		_cautionPlayer = CreateGeneratorPlayer();
		_warningPlayer = CreateGeneratorPlayer();
		AddChild(_cautionPlayer);
		AddChild(_warningPlayer);

		_cautionPlayer.Play();
		_warningPlayer.Play();

		_cautionPlayback = _cautionPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
		_warningPlayback = _warningPlayer.GetStreamPlayback() as AudioStreamGeneratorPlayback;
	}

	public override void _Process(double delta)
	{
		if (_cautionPlayback == null || _warningPlayback == null) return;
		if (SimBus.Instance?.Alerts == null) return;

		var active = SimBus.Instance.Alerts.Active;
		bool hasCaution = active.Any(a => a.Severity == "caution" && !a.Acknowledged);
		bool hasWarning  = active.Any(a => a.Severity == "warning"  && !a.Acknowledged);

		TickAlert(hasCaution, ref _cautionTimer, ref _cautionPos, CautionInterval, (float)delta);
		TickAlert(hasWarning,  ref _warningTimer, ref _warningPos, WarningInterval, (float)delta);

		FillBuffer(_cautionPlayback, _cautionSamples, ref _cautionPos);
		FillBuffer(_warningPlayback, _warningSamples, ref _warningPos);
	}

	private static void TickAlert(
		bool alertActive, ref float timer, ref int pos, float interval, float dt)
	{
		if (!alertActive)
		{
			timer = 0f; // reset so the tone fires immediately on the next alert
			return;
		}
		timer -= dt;
		if (timer <= 0f && pos < 0) // only start if previous tone has finished
		{
			pos   = 0;
			timer = interval;
		}
	}

	private static void FillBuffer(
		AudioStreamGeneratorPlayback playback, Vector2[] samples, ref int pos)
	{
		int available = playback.GetFramesAvailable();
		for (int i = 0; i < available; i++)
		{
			if (pos >= 0 && pos < samples.Length)
			{
				playback.PushFrame(samples[pos++]);
				if (pos >= samples.Length) pos = -1;
			}
			else
			{
				playback.PushFrame(Vector2.Zero); // silence
			}
		}
	}

	private static AudioStreamPlayer CreateGeneratorPlayer()
	{
		var gen = new AudioStreamGenerator
		{
			MixRate     = SampleRate,
			BufferLength = 0.1f, // 100 ms lookahead
		};
		return new AudioStreamPlayer { Stream = gen };
	}

	// "woomp" — descending sine 480 Hz → 180 Hz over 0.5 s, quadratic fade-out.
	private static Vector2[] GenerateCautionTone()
	{
		int n = (int)(SampleRate * 0.5f);
		var s = new Vector2[n];
		float phase = 0f;
		for (int i = 0; i < n; i++)
		{
			float t    = (float)i / n;
			float freq = Mathf.Lerp(480f, 180f, t);
			float env  = (1f - t) * (1f - t); // quadratic fade-out
			float v    = Mathf.Sin(phase) * env * 0.8f;
			s[i] = new Vector2(v, v);
			phase += Mathf.Tau * freq / SampleRate;
		}
		return s;
	}

	// "WAAH WAAH" — 900 Hz for 0.18 s then 600 Hz for 0.18 s, brief attack on each.
	private static Vector2[] GenerateWarningTone()
	{
		int toneN = (int)(SampleRate * 0.18f);
		var s = new Vector2[toneN * 2];
		WriteTone(s, 0,     toneN,     900f);
		WriteTone(s, toneN, toneN * 2, 600f);
		return s;
	}

	private static void WriteTone(Vector2[] s, int start, int end, float freq)
	{
		float phase = 0f;
		int len = end - start;
		for (int i = start; i < end; i++)
		{
			float t   = (float)(i - start) / len;
			float env = t < 0.04f ? t / 0.04f : 1f; // 4 % linear attack, full sustain
			float v   = Mathf.Sin(phase) * env * 0.8f;
			s[i] = new Vector2(v, v);
			phase += Mathf.Tau * freq / SampleRate;
		}
	}
}
