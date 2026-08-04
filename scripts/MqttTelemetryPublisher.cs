using System;
using System.Buffers;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using MQTTnet;
using MQTTnet.Protocol;

namespace ColdOrbit.SimCore;

// Owns the single MQTTnet client for sim-core: publishes telemetry outward
// and delivers inbound messages to subscribers via MessageReceived. All
// subscribe registrations must be made before calling Start() to guarantee
// the filters are in place before the first successful connect. Fires
// Connected after every successful broker connection (initial + reconnects)
// so callers can republish retained state in case the broker restarted.
//
// Auto-reconnect design and _connecting guard are unchanged from batch 6;
// see the batch 6 handover for that reasoning.
public sealed class MqttTelemetryPublisher
{
    private const int ReconnectDelayMs = 2000;

    private readonly IMqttClient _client;
    private readonly MqttClientOptions _options;
    private readonly CancellationTokenSource _shutdownCts = new();
    private readonly List<(string Filter, MqttQualityOfServiceLevel Qos)> _subscriptions = new();
    private int _connecting;

    // Fires on an MQTT background thread — handlers must not block.
    public event Action<string, string> MessageReceived;
    // Fires after each successful broker connection (initial and reconnects).
    public event Action Connected;

    public MqttTelemetryPublisher(string host, int port)
    {
        _client = new MqttClientFactory().CreateMqttClient();
        _options = new MqttClientOptionsBuilder()
            .WithClientId("sim-core-telemetry")
            .WithTcpServer(host, port)
            .WithCleanSession()
            .Build();

        _client.ApplicationMessageReceivedAsync += args =>
        {
            try
            {
                var topic = args.ApplicationMessage.Topic;
                var payload = Encoding.UTF8.GetString(
                    args.ApplicationMessage.Payload.ToArray());
                MessageReceived?.Invoke(topic, payload);
            }
            catch (Exception ex)
            {
                GD.PrintErr($"MQTT message dispatch error: {ex.Message}");
            }
            return Task.CompletedTask;
        };

        _client.DisconnectedAsync += args =>
        {
            if (!_shutdownCts.IsCancellationRequested)
            {
                _ = ConnectWithRetryLoop();
            }
            return Task.CompletedTask;
        };
    }

    // Register a topic filter to subscribe to after every successful connect.
    // Call before Start() -- registering after Start() races the connect loop.
    public void Subscribe(string topicFilter, MqttQualityOfServiceLevel qos = MqttQualityOfServiceLevel.AtLeastOnce)
    {
        _subscriptions.Add((topicFilter, qos));
    }

    public void Start()
    {
        _ = ConnectWithRetryLoop();
    }

    // Cancels any in-flight retry loop before disposing the client, so a
    // reconnect attempt racing shutdown exits quietly instead of throwing
    // ObjectDisposedException into the retry loop's catch block (seen once
    // during the batch 6 headless verification run).
    public void Stop()
    {
        _shutdownCts.Cancel();

        if (_client.IsConnected)
        {
            _client.DisconnectAsync().Wait(TimeSpan.FromSeconds(1));
        }

        _client.Dispose();
    }

    // Fire-and-forget: called every publish tick from the physics thread
    // and from UI signal handlers, must never block or throw into the
    // caller. Returns whether the send was actually attempted (i.e. the
    // client was connected) -- callers that track "last published state"
    // for on-change publishing use this to avoid marking a value as sent
    // when it was really just dropped, so a value that changes while
    // disconnected still gets published once the retry loop above
    // reconnects, rather than being silently missed.
    //
    // The IsConnected check isn't airtight against a disconnect racing the
    // call itself -- PublishAsync can still throw MqttClientNotConnectedException
    // synchronously even when IsConnected was true a moment earlier (seen
    // live during verification: an in-flight disconnect between the check
    // and the call). The try/catch below is what actually delivers "never
    // throws into the caller" -- without it, that exception propagates out
    // through whatever UI signal or physics call triggered the publish.
    public bool Publish(string topic, string payload, MqttQualityOfServiceLevel qos, bool retain)
    {
        if (!_client.IsConnected) return false;

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(qos)
            .WithRetainFlag(retain)
            .Build();

        try
        {
            _ = _client.PublishAsync(message, CancellationToken.None).ContinueWith(
                t => GD.PrintErr($"MQTT publish to {topic} failed: {t.Exception!.GetBaseException().Message}"),
                CancellationToken.None, TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);
        }
        catch (Exception ex)
        {
            GD.PrintErr($"MQTT publish to {topic} failed synchronously: {ex.Message}");
            return false;
        }

        return true;
    }

    private async Task ConnectWithRetryLoop()
    {
        if (Interlocked.Exchange(ref _connecting, 1) == 1) return;

        try
        {
            while (!_client.IsConnected && !_shutdownCts.IsCancellationRequested)
            {
                try
                {
                    await _client.ConnectAsync(_options, _shutdownCts.Token);
                    await SubscribeAllAsync();
                    Connected?.Invoke();
                }
                catch (Exception ex)
                {
                    if (_shutdownCts.IsCancellationRequested) break;
                    GD.PrintErr($"MQTT connect failed, retrying in {ReconnectDelayMs}ms: {ex.Message}");
                    try
                    {
                        await Task.Delay(ReconnectDelayMs, _shutdownCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _connecting, 0);
        }
    }

    private async Task SubscribeAllAsync()
    {
        foreach (var (filter, qos) in _subscriptions)
        {
            var opts = new MqttClientSubscribeOptionsBuilder()
                .WithTopicFilter(f => f.WithTopic(filter).WithQualityOfServiceLevel(qos))
                .Build();
            await _client.SubscribeAsync(opts, _shutdownCts.Token);
        }
    }
}
