using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Newtonsoft.Json.Linq;
using Newtonsoft.Json.Serialization;
using UnityEngine;
using UnityModManagerNet;
using WebSocketSharp.Server;

namespace DerailValleyWebSocket;

public class WebsocketServer
{
    private static UnityModManager.ModEntry.ModLogger Logger => Main.ModEntry.Logger;

    // Legacy clients keep the original PascalCase wire format; v2 clients get camelCase.
    private static readonly JsonSerializerSettings LegacyJson = new()
    {
        Converters = { new StringEnumConverter() }
    };

    private static readonly JsonSerializerSettings V2Json = new()
    {
        ContractResolver = new CamelCasePropertyNamesContractResolver(),
        Converters = { new StringEnumConverter(new CamelCaseNamingStrategy()) }
    };

    // send state at least this often even when nothing changed, as a heartbeat
    private const float STATE_HEARTBEAT_SECONDS = 1f;

    private WebSocketServer _server;

    // Only touched on the main thread: connect/disconnect/message callbacks are marshalled via MainThread.
    private readonly Dictionary<string, ClientSession> _clients = new();

    private string? _lastStateJson;
    private float _lastStateSentAt = float.NegativeInfinity;

    public WebsocketServer(int port)
    {
        Logger.Log($"Start WebSocket server on port {port}");
        _server = new WebSocketServer(port);
        _server.AddWebSocketService<WsBehavior>("/dv", () =>
        {
            var behavior = new WsBehavior();
            behavior.OnMessageReceived = (id, json) => MainThread.Post(() => HandleMessage(id, json));
            behavior.OnClientConnected = id => MainThread.Post(() =>
            {
                Logger.Log($"Client {id} connected");
                _clients[id] = new ClientSession(id);
            });
            behavior.OnClientDisconnected = id => MainThread.Post(() =>
            {
                Logger.Log($"Client {id} disconnected");
                _clients.Remove(id);
                CommandDispatcher.OnClientDisconnected(id);
            });
            return behavior;
        });
    }

    public void Start()
    {
        _server.Start();
        Logger.Log("WebSocket server started");
    }

    public void Stop()
    {
        _server.Stop();
        Logger.Log("WebSocket server stopped");
    }

    public bool HasLegacyClients => _clients.Values.Any(c => c.Protocol < StateReader.PROTOCOL_VERSION);

    public bool HasV2Clients => _clients.Values.Any(c => c.Protocol >= StateReader.PROTOCOL_VERSION);

    private ClientSession GetClient(string clientId)
    {
        // a message can be dequeued before the connect callback if both landed in the same frame
        if (!_clients.TryGetValue(clientId, out var client))
            _clients[clientId] = client = new ClientSession(clientId);

        return client;
    }

    private void HandleMessage(string clientId, string json)
    {
        try
        {
            var message = JObject.Parse(json);
            var typeName = message.GetValue("type", StringComparison.OrdinalIgnoreCase)?.Value<string>();
            var payload = message.GetValue("payload", StringComparison.OrdinalIgnoreCase) as JObject ?? new JObject();

            if (typeName == null || !Enum.TryParse<MessageType>(typeName, ignoreCase: true, out var type))
                throw new Exception($"Unknown message type '{typeName}'");

            var client = GetClient(clientId);

            switch (type)
            {
                case MessageType.Init:
                    Logger.Log($"Initialize message from client");

                    client.SubscribedVars = new HashSet<(string, string)>();

                    // TODO: Only do this if names don't match
                    SendTo(client, MessageType.Init, new InitPayload { CarName = CarHelper.GetCurrentCarName() });
                    break;

                case MessageType.SubscribeToVar:
                    var subscribeToVarPayload = payload.ToObject<SubscribeToVarPayload>()!;

                    Logger.Log($"Client wants to subscribe to var '{subscribeToVarPayload.Name}' ({subscribeToVarPayload.Unit})");

                    client.SubscribedVars.Add((subscribeToVarPayload.Name, subscribeToVarPayload.Unit));

                    VarSystem.Subscribe(subscribeToVarPayload.Name, subscribeToVarPayload.Unit);
                    break;

                case MessageType.UnsubscribeFromVar:
                    var unsubscribeFromVarPayload = payload.ToObject<SubscribeToVarPayload>()!;

                    client.SubscribedVars.Remove((unsubscribeFromVarPayload.Name, unsubscribeFromVarPayload.Unit));

                    // vars are shared between clients, so only stop fetching when nobody wants it
                    if (!_clients.Values.Any(c => c.SubscribedVars.Contains((unsubscribeFromVarPayload.Name, unsubscribeFromVarPayload.Unit))))
                        VarSystem.Unsubscribe(unsubscribeFromVarPayload.Name, unsubscribeFromVarPayload.Unit);
                    break;

                case MessageType.SubscribeToEvent:
                    var subscribeToEventPayload = payload.ToObject<SubscribeToEventPayload>()!;

                    Logger.Log($"Client wants to subscribe to event '{subscribeToEventPayload.Name}'");

                    client.SubscribedEvents.Add(subscribeToEventPayload.Name);
                    break;

                case MessageType.UnsubscribeFromEvent:
                    client.SubscribedEvents.Remove(payload.ToObject<SubscribeToEventPayload>()!.Name);
                    break;

                case MessageType.Hello:
                    var hello = payload.ToObject<HelloPayload>()!;

                    Logger.Log($"Client {clientId} says hello: client={hello.Client} protocol={hello.Protocol}");

                    client.Protocol = StateReader.PROTOCOL_VERSION;

                    // reply straight away so a reconnecting client doesn't wait for the next change/heartbeat
                    SendTo(client, MessageType.State, StateReader.Read());
                    break;

                case MessageType.Command:
                    var command = payload.ToObject<CommandPayload>()!;
                    var result = CommandDispatcher.Execute(clientId, command);

                    if (!result.Ok)
                        Logger.Log($"Command {command.Control}/{command.Action}={command.Value} rejected: {result.Error}");

                    SendTo(client, MessageType.CommandResult, result);
                    break;

                default:
                    throw new Exception($"Message type '{type}' can't be sent by clients");
            }
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to handle message: {ex}");

            if (_clients.TryGetValue(clientId, out var client))
                SendTo(client, MessageType.Error, new ErrorPayload { Message = ex.Message });
        }
    }

    /// <summary>Called from UpdateDriver at the configured rate.</summary>
    public void PublishState()
    {
        if (!HasV2Clients)
        {
            _lastStateJson = null;
            return;
        }

        var json = Serialize(MessageType.State, StateReader.Read(), V2Json);

        // only send when something changed, plus a heartbeat so clients can detect a frozen game
        if (json == _lastStateJson && Time.unscaledTime - _lastStateSentAt < STATE_HEARTBEAT_SECONDS)
            return;

        _lastStateJson = json;
        _lastStateSentAt = Time.unscaledTime;

        foreach (var client in _clients.Values.Where(c => c.Protocol >= StateReader.PROTOCOL_VERSION))
            SendRaw(client.Id, json);
    }

    public void BroadcastVar(string name, string unit, object value)
    {
        BroadcastLegacy(MessageType.Var, new VarPayload { Name = name, Unit = unit, Value = value });
    }

    public VarsPayload ConvertValuesIntoVarsPayload(Dictionary<(string name, string unit), object> values)
    {
        var vars = values.Select(x => new VarPayload() { Name = x.Key.name, Unit = x.Key.unit, Value = x.Value }).ToList();

        var payload = new VarsPayload() { Vars = vars };

        return payload;
    }

    public void BroadcastVars(Dictionary<(string, string), object> values)
    {
        BroadcastLegacy(MessageType.Vars, ConvertValuesIntoVarsPayload(values));
    }

    public void BroadcastEvent(string name, object? value)
    {
        foreach (var client in _clients.Values)
            SendTo(client, MessageType.Event, new EventPayload { Name = name, Value = value! });
    }

    private void BroadcastLegacy<TPayload>(MessageType type, TPayload payload)
    {
        var json = Serialize(type, payload, LegacyJson);

        foreach (var client in _clients.Values.Where(c => c.Protocol < StateReader.PROTOCOL_VERSION))
            SendRaw(client.Id, json);
    }

    private void SendTo<TPayload>(ClientSession client, MessageType type, TPayload payload)
    {
        var settings = client.Protocol >= StateReader.PROTOCOL_VERSION ? V2Json : LegacyJson;
        SendRaw(client.Id, Serialize(type, payload, settings));
    }

    private static string Serialize<TPayload>(MessageType type, TPayload payload, JsonSerializerSettings settings)
    {
        return JsonConvert.SerializeObject(new Message<TPayload> { Type = type, Payload = payload }, settings);
    }

    private void SendRaw(string clientId, string json)
    {
        try
        {
            // async so a slow client can't block Unity's main thread
            _server.WebSocketServices["/dv"].Sessions.SendToAsync(json, clientId, null);
        }
        catch (Exception ex)
        {
            Logger.Log($"Failed to send to {clientId}: {ex.Message}");
        }
    }
}
