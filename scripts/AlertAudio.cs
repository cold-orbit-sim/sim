using Godot;
using System;
using System.Linq;

namespace ColdOrbit.SimCore;

// Plays synthesised alarm tones for unacknowledged caution/warning alerts.
// Uses AudioStreamGenerator (PCM push) — no audio files required.
// Both tones run on independent players and can overlap.
//
// Caution: smooth siren sweep, 200→600→200 Hz over 1.2 s (one slow arc).
// Warning: harsh square-wave klaxon, two bursts at 700 Hz then 520 Hz.
public partial class AlertAudio : Node
{
	[Export] public float CautionInterval { get; set; } = 2.0f; // seconds between siren sweeps
	[Export] public float WarningInterval { get; set; } = 0.6f; // seconds between klaxon bursts

	private const int SampleRate = 22050;

	private AudioStreamPlayer _cautionPlayer;
	private AudioStreamPlayer _warningPlayer;
	private AudioStreamGeneratorPlayback _cautionPlayback;
	private AudioStreamGeneratorPlayback _warningPlayback;

	private Vector2[] _cautionSamples;
	private Vector2[] _warningSamples;

	private int _cautionPos = -1;
	private int _warningPos = -1;
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
			timer = 0f; // reset so tone fires immediately on next alert
			return;
		}
		timer -= dt;
		if (timer <= 0f && pos < 0)
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
			MixRate      = SampleRate,
			BufferLength = 0.1f,
		};
		return new AudioStreamPlayer { Stream = gen };
	}

	// Caution: smooth siren swell — frequency and amplitude both arc up then back down.
	// 200 Hz → 600 Hz → 200 Hz, amplitude 0 → 1 → 0, over 1.2 s. Sine wave is right
	// here: soft, non-urgent, just a presence-alert.
	private static Vector2[] GenerateCautionTone()
	{
		int n = (int)(SampleRate * 1.2f);
		var s = new Vector2[n];
		float phase = 0f;
		for (int i = 0; i < n; i++)
		{
			float t    = (float)i / n;
			float env  = t < 0.5f ? 2f * t : 2f * (1f - t);           // triangle envelope
			float freq = t < 0.5f
				? Mathf.Lerp(200f, 600f, 2f * t)
				: Mathf.Lerp(600f, 200f, 2f * t - 1f);
			float v = Mathf.Sin(phase) * env * 0.75f;
			s[i] = new Vector2(v, v);
			phase += Mathf.Tau * freq / SampleRate;
		}
		return s;
	}

	// Warning: square-wave klaxon, two sharp bursts at different frequencies.
	// Square waves are naturally harsh (rich in odd harmonics) — that's the point.
	// Burst 1: 700 Hz for 0.2 s | 0.02 s silence | Burst 2: 520 Hz for 0.2 s.
	private static Vector2[] GenerateWarningTone()
	{
		int b1n  = (int)(SampleRate * 0.20f);
		int gapn = (int)(SampleRate * 0.02f);
		int b2n  = (int)(SampleRate * 0.20f);
		var s = new Vector2[b1n + gapn + b2n];
		WriteSquare(s, 0,          b1n,              700f);
		// gap region stays Vector2.Zero (default)
		WriteSquare(s, b1n + gapn, b1n + gapn + b2n, 520f);
		return s;
	}

	private static void WriteSquare(Vector2[] s, int start, int end, float freq)
	{
		float phase = 0f;
		int   len   = end - start;
		int   atkN  = (int)(SampleRate * 0.004f); // 4 ms attack
		int   decN  = (int)(SampleRate * 0.012f); // 12 ms decay
		for (int i = start; i < end; i++)
		{
			int   pos = i - start;
			float env = pos < atkN              ? (float)pos / atkN
			          : pos > len - decN        ? (float)(len - pos) / decN
			          :                           1f;
			float v   = MathF.Sign(Mathf.Sin(phase)) * env * 0.65f;
			s[i] = new Vector2(v, v);
			phase += Mathf.Tau * freq / SampleRate;
		}
	}
}
