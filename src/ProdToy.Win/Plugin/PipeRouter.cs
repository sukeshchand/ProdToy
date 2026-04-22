using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using ProdToy.Sdk;

namespace ProdToy;

/// <summary>
/// Dispatches incoming named-pipe payloads to the plugin that registered the
/// matching <see cref="PipeCommand.Command"/> name. Thread-safe. The pipe
/// server decodes the envelope; this class owns the string→handler map.
///
/// Envelope shape:
///   { "command": "claude.notify", "payload": "{...json string...}" }
/// Payload is left as a JSON string (not a nested object) so plugins own
/// their own deserialization and the router never touches plugin-specific types.
/// </summary>
sealed class PipeRouter
{
    private readonly ConcurrentDictionary<string, PipeCommandHandler> _handlers = new();
    private readonly Action<Action> _invokeOnUI;

    public PipeRouter(Action<Action> invokeOnUI)
    {
        _invokeOnUI = invokeOnUI;
    }

    public IDisposable Register(string command, PipeCommandHandler handler)
    {
        if (string.IsNullOrWhiteSpace(command))
            throw new ArgumentException("command must not be empty", nameof(command));

        _handlers[command] = handler;
        return new Registration(this, command);
    }

    /// <summary>Try to parse a pipe payload as a routed command. Returns true
    /// if the payload matched the envelope shape AND a handler was found.</summary>
    public bool TryDispatch(string rawJson)
    {
        if (string.IsNullOrWhiteSpace(rawJson)) return false;

        string? command;
        string? payloadJson;
        try
        {
            var node = JsonNode.Parse(rawJson);
            if (node is null) return false;
            command = node["command"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(command)) return false;

            var payloadNode = node["payload"];
            payloadJson = payloadNode switch
            {
                null => null,
                JsonValue v when v.TryGetValue<string>(out var s) => s,
                _ => payloadNode.ToJsonString(),
            };
        }
        catch (JsonException ex)
        {
            Log.Warn($"PipeRouter: malformed envelope: {ex.Message}");
            return false;
        }

        if (!_handlers.TryGetValue(command!, out var handler))
        {
            Log.Warn($"PipeRouter: no handler for command '{command}'");
            return false;
        }

        var cmd = new PipeCommand(command!, payloadJson);
        _invokeOnUI(() =>
        {
            try { handler(cmd); }
            catch (Exception ex) { Log.Error($"PipeRouter handler '{command}' threw", ex); }
        });
        return true;
    }

    private sealed class Registration : IDisposable
    {
        private readonly PipeRouter _owner;
        private readonly string _command;
        private bool _disposed;

        public Registration(PipeRouter owner, string command)
        {
            _owner = owner;
            _command = command;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _owner._handlers.TryRemove(_command, out _);
        }
    }
}
