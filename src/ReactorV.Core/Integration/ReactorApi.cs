using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using RageWebUI.Core.Protocol;

namespace ReactorV.Integration
{
    public interface IReactorExtensionHandle : IDisposable
    {
        ReactorExtensionDescriptor Descriptor { get; }
        void UpdateMenu(ReactorMenuDescriptor menu);
        bool RemoveMenu(string menuId);
        bool TryPublishEvent(string eventId, JToken? payload);
    }

    public sealed class ReactorExtensionBuilder
    {
        private readonly Dictionary<string, ReactorActionRegistration> _actions =
            new Dictionary<string, ReactorActionRegistration>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReactorEventDescriptor> _events =
            new Dictionary<string, ReactorEventDescriptor>(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, ReactorMenuDescriptor> _menus =
            new Dictionary<string, ReactorMenuDescriptor>(StringComparer.OrdinalIgnoreCase);
        private bool _built;
        private IReactorExtensionLifecycle? _lifecycle;

        internal ReactorExtensionBuilder() { }

        public ReactorExtensionBuilder AddAction(
            ReactorActionDescriptor descriptor,
            ReactorActionHandler handler)
        {
            EnsureMutable();
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (handler == null) throw new ArgumentNullException(nameof(handler));
            if (handler.GetInvocationList().Length != 1)
                throw new ArgumentException("Action handlers must contain exactly one callback.", nameof(handler));
            if (_actions.ContainsKey(descriptor.Id))
                throw new InvalidOperationException($"Action '{descriptor.Id}' is already declared.");
            _actions.Add(descriptor.Id, new ReactorActionRegistration(descriptor, handler));
            if (_actions.Count > ReactorRegistry.MaximumActionsPerExtension)
                throw new InvalidOperationException("The extension action limit was exceeded.");
            return this;
        }

        public ReactorExtensionBuilder AddEvent(ReactorEventDescriptor descriptor)
        {
            EnsureMutable();
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (_events.ContainsKey(descriptor.Id))
                throw new InvalidOperationException($"Event '{descriptor.Id}' is already declared.");
            _events.Add(descriptor.Id, descriptor);
            if (_events.Count > ReactorRegistry.MaximumEventsPerExtension)
                throw new InvalidOperationException("The extension event limit was exceeded.");
            return this;
        }

        public ReactorExtensionBuilder AddMenu(ReactorMenuDescriptor menu)
        {
            EnsureMutable();
            if (menu == null) throw new ArgumentNullException(nameof(menu));
            if (_menus.ContainsKey(menu.Id))
                throw new InvalidOperationException($"Menu '{menu.Id}' is already declared.");
            _menus.Add(menu.Id, menu);
            if (_menus.Count > ReactorRegistry.MaximumMenusPerExtension)
                throw new InvalidOperationException("The extension menu limit was exceeded.");
            return this;
        }

        public ReactorExtensionBuilder UseLifecycle(IReactorExtensionLifecycle lifecycle)
        {
            EnsureMutable();
            if (lifecycle == null) throw new ArgumentNullException(nameof(lifecycle));
            if (_lifecycle != null) throw new InvalidOperationException("A lifecycle callback is already registered.");
            _lifecycle = lifecycle;
            return this;
        }

        internal ReactorExtensionDefinition Build()
        {
            EnsureMutable();
            _built = true;
            ValidateMenuReferences(_menus, _actions);
            return new ReactorExtensionDefinition(_actions, _events, _menus, _lifecycle);
        }

        internal static void ValidateMenuReferences(
            IReadOnlyDictionary<string, ReactorMenuDescriptor> menus,
            IReadOnlyDictionary<string, ReactorActionRegistration> actions)
        {
            foreach (var menu in menus.Values)
            {
                foreach (var node in menu.AllNodes())
                {
                    var actionId = ActionReference(node);
                    if (actionId != null && !actions.ContainsKey(actionId))
                        throw new InvalidOperationException(
                            $"Menu '{menu.Id}' references undeclared action '{actionId}'.");
                    if (actionId != null)
                        ValidateMenuActionContract(menu.Id, node, actions[actionId].Descriptor);
                    if (node is ReactorSubmenuNode submenu && !menus.ContainsKey(submenu.MenuId))
                        throw new InvalidOperationException(
                            $"Menu '{menu.Id}' references undeclared submenu '{submenu.MenuId}'.");
                }
            }
        }

        internal static string? ActionReference(ReactorMenuNode node) =>
            node is ReactorActionNode action ? action.Action :
            node is ReactorToggleNode toggle ? toggle.Action :
            node is ReactorChoiceNode choice ? choice.Action :
            node is ReactorRangeNode range ? range.Action :
            node is ReactorInputNode input ? input.Action :
            node is ReactorKeybindNode keybind ? keybind.Action :
            node is ReactorPaginationNode pagination ? pagination.Action : null;

        private static void ValidateMenuActionContract(
            string menuId,
            ReactorMenuNode node,
            ReactorActionDescriptor action)
        {
            ReactorValueType? expected = node is ReactorToggleNode ? ReactorValueType.Boolean :
                node is ReactorChoiceNode ? ReactorValueType.String :
                node is ReactorRangeNode ? ReactorValueType.Number :
                node is ReactorInputNode ? ReactorValueType.String :
                node is ReactorKeybindNode ? ReactorValueType.String :
                node is ReactorPaginationNode ? ReactorValueType.Integer : null;
            if (!expected.HasValue) return;

            var valueParameter = action.Parameters.FirstOrDefault(parameter =>
                string.Equals(parameter.Name, "value", StringComparison.Ordinal));
            if (valueParameter == null)
            {
                if (action.AllowAdditionalParameters) return;
                throw new InvalidOperationException(
                    $"Menu '{menuId}' node '{node.Id}' sends a '{expected.Value.ToString().ToLowerInvariant()}' value, " +
                    $"but action '{action.Id}' does not declare a 'value' parameter or allow additional parameters.");
            }
            if (valueParameter.Type != expected.Value)
                throw new InvalidOperationException(
                    $"Menu '{menuId}' node '{node.Id}' requires action '{action.Id}' to declare 'value' as " +
                    $"'{expected.Value.ToString().ToLowerInvariant()}'.");
        }

        private void EnsureMutable()
        {
            if (_built) throw new InvalidOperationException("The extension builder has already been registered.");
        }
    }

    /// <summary>
    /// Stable public registration surface for managed Reactor extensions.
    /// ExtensionApiVersion is independent from the browser bridge protocol version.
    /// </summary>
    public static class ReactorApi
    {
        public const int ExtensionApiVersion = 1;

        [Obsolete("Use ExtensionApiVersion; browser protocol versions are negotiated separately.")]
        public const int ApiVersion = ExtensionApiVersion;

        public static IReactorExtensionHandle RegisterExtension(
            ReactorExtensionDescriptor descriptor,
            Action<ReactorExtensionBuilder> configure)
        {
            if (descriptor == null) throw new ArgumentNullException(nameof(descriptor));
            if (configure == null) throw new ArgumentNullException(nameof(configure));
            if (configure.GetInvocationList().Length != 1)
                throw new ArgumentException("Extension configuration must contain exactly one callback.", nameof(configure));
            var builder = new ReactorExtensionBuilder();
            configure(builder);
            return ReactorRegistry.Instance.Register(descriptor, builder.Build());
        }
    }

    /// <summary>Internal bridge intentionally visible only to the Script, Harness, and Core tests.</summary>
    internal static class ReactorHostApi
    {
        internal static JObject DescribeExtensionSummaries() =>
            ReactorRegistry.Instance.DescribeExtensionSummaries();

        internal static JObject? DescribeExtension(string extensionId) =>
            ReactorRegistry.Instance.DescribeExtension(extensionId);

        internal static JArray DescribeExtensions() => ReactorRegistry.Instance.DescribeExtensions();

        internal static JObject DescribeMenuSummaries(string? extensionId = null) =>
            ReactorRegistry.Instance.DescribeMenuSummaries(extensionId);

        internal static JArray DescribeMenus(string? extensionId = null, string? menuId = null) =>
            ReactorRegistry.Instance.DescribeMenus(extensionId, menuId);

        internal static bool TryResolveMenuAction(
            string extensionId,
            string menuId,
            string nodeId,
            out string? actionId) =>
            ReactorRegistry.Instance.TryResolveMenuAction(extensionId, menuId, nodeId, out actionId);

        internal static ReactorActionResult InvokeMenu(
            string extensionId,
            string menuId,
            string nodeId,
            string interaction,
            JObject parameters,
            bool confirmed = false,
            string? idempotencyKey = null) =>
            ReactorRegistry.Instance.InvokeMenu(
                extensionId,
                menuId,
                nodeId,
                interaction,
                parameters,
                confirmed,
                idempotencyKey);

        internal static ReactorActionResult Invoke(
            string extensionId,
            string actionId,
            JObject parameters,
            bool confirmed = false,
            string? idempotencyKey = null) =>
            ReactorRegistry.Instance.Invoke(extensionId, actionId, parameters, confirmed, idempotencyKey);

        internal static JArray DrainEvents() => ReactorRegistry.Instance.DrainEvents();

        internal static void NotifyLifecycle(ReactorLifecycleStage stage, JToken? payload = null) =>
            ReactorRegistry.Instance.NotifyLifecycle(stage, payload);

        internal static void Reset() => ReactorRegistry.Instance.Reset();
    }

    internal sealed class ReactorActionRegistration
    {
        public ReactorActionRegistration(ReactorActionDescriptor descriptor, ReactorActionHandler handler)
        {
            Descriptor = descriptor;
            Handler = handler;
        }
        public ReactorActionDescriptor Descriptor { get; }
        public ReactorActionHandler Handler { get; }
    }

    internal sealed class ReactorExtensionDefinition
    {
        public ReactorExtensionDefinition(
            IReadOnlyDictionary<string, ReactorActionRegistration> actions,
            IReadOnlyDictionary<string, ReactorEventDescriptor> events,
            IReadOnlyDictionary<string, ReactorMenuDescriptor> menus,
            IReactorExtensionLifecycle? lifecycle)
        {
            Actions = actions.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            Events = events.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            Menus = menus.ToDictionary(value => value.Key, value => value.Value, StringComparer.OrdinalIgnoreCase);
            Lifecycle = lifecycle;
        }
        public Dictionary<string, ReactorActionRegistration> Actions { get; }
        public Dictionary<string, ReactorEventDescriptor> Events { get; }
        public Dictionary<string, ReactorMenuDescriptor> Menus { get; }
        public IReactorExtensionLifecycle? Lifecycle { get; }
    }

    internal sealed class ReactorExtensionState
    {
        public ReactorExtensionState(ReactorExtensionDescriptor descriptor, ReactorExtensionDefinition definition)
        {
            Descriptor = descriptor;
            Definition = definition;
            Generation = Guid.NewGuid();
        }
        public ReactorExtensionDescriptor Descriptor { get; }
        public ReactorExtensionDefinition Definition { get; }
        public Guid Generation { get; }
    }

    internal sealed class ReactorIdempotencyEntry
    {
        public ReactorIdempotencyEntry(string parameters)
        {
            Parameters = parameters;
        }
        public string Parameters { get; }
        public ReactorActionResult? Result { get; set; }
    }

    internal sealed class ReactorEventRecord
    {
        public ReactorEventRecord(long sequence, string extensionId, string eventId, JToken? payload)
        {
            Sequence = sequence;
            ExtensionId = extensionId;
            EventId = eventId;
            Payload = payload?.DeepClone() ?? JValue.CreateNull();
            TimestampUtc = DateTime.UtcNow;
        }
        public long Sequence { get; }
        public string ExtensionId { get; }
        public string EventId { get; }
        public string EventName => ExtensionId + "." + EventId;
        public JToken Payload { get; }
        public DateTime TimestampUtc { get; }
        public JObject ToJson() => new JObject
        {
            ["sequence"] = Sequence,
            ["event"] = EventName,
            ["extensionId"] = ExtensionId,
            ["eventId"] = EventId,
            ["timestampUtc"] = TimestampUtc.ToString("O"),
            ["payload"] = Payload.DeepClone(),
        };
    }

    internal sealed class ReactorRegistry
    {
        internal const int MaximumExtensions = 128;
        internal const int MaximumActionsPerExtension = 256;
        internal const int MaximumEventsPerExtension = 128;
        internal const int MaximumMenusPerExtension = 64;
        internal const int MaximumPendingEvents = 256;
        internal const int MaximumIdempotencyEntries = 512;

        internal static ReactorRegistry Instance { get; } = new ReactorRegistry();

        private readonly object _sync = new object();
        private readonly Dictionary<string, ReactorExtensionState> _extensions =
            new Dictionary<string, ReactorExtensionState>(StringComparer.OrdinalIgnoreCase);
        private readonly Queue<ReactorEventRecord> _events = new Queue<ReactorEventRecord>();
        private readonly Dictionary<string, ReactorIdempotencyEntry> _idempotency =
            new Dictionary<string, ReactorIdempotencyEntry>(StringComparer.Ordinal);
        private readonly Queue<string> _idempotencyOrder = new Queue<string>();
        private long _eventSequence;

        private ReactorRegistry() { }

        public IReactorExtensionHandle Register(
            ReactorExtensionDescriptor descriptor,
            ReactorExtensionDefinition definition)
        {
            foreach (var eventDescriptor in definition.Events.Values)
            {
                var eventName = descriptor.Id + "." + eventDescriptor.Id;
                if (!BridgeProtocol.IsValidEventName(eventName))
                    throw new InvalidOperationException(
                        $"Event '{eventName}' is not a bridge-safe dotted event name of at most 96 characters.");
            }

            var detail = DescribeExtensionDetail(descriptor, definition, "menuIds");
            if (!FitsTransport(detail))
                throw new InvalidOperationException(
                    $"Extension '{descriptor.Id}' has too much descriptor metadata for a bridge response.");

            ReactorExtensionState state;
            lock (_sync)
            {
                if (_extensions.ContainsKey(descriptor.Id))
                    throw new InvalidOperationException($"Extension '{descriptor.Id}' is already registered.");
                if (_extensions.Count >= MaximumExtensions)
                    throw new InvalidOperationException("The Reactor extension limit was reached.");
                state = new ReactorExtensionState(descriptor, definition);
                var summaryItems = new JArray(_extensions.Values
                    .Concat(new[] { state })
                    .OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal)
                    .Select(DescribeExtensionSummary));
                var summaries = new JObject
                {
                    ["total"] = summaryItems.Count,
                    ["items"] = summaryItems,
                };
                if (!FitsTransport(summaries))
                    throw new InvalidOperationException(
                        "The Reactor extension summary registry reached its bridge-safe capacity.");
                _extensions.Add(descriptor.Id, state);
            }
            InvokeLifecycle(state, ReactorLifecycleStage.Registered, null);
            return new ReactorExtensionHandle(this, descriptor, state.Generation);
        }

        public JArray DescribeExtensions()
        {
            lock (_sync)
            {
                return new JArray(_extensions.Values
                    .OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal)
                    .Select(state => DescribeExtensionDetail(state.Descriptor, state.Definition, "menus")));
            }
        }

        public JObject DescribeExtensionSummaries()
        {
            lock (_sync)
            {
                var items = new JArray(_extensions.Values
                    .OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal)
                    .Select(DescribeExtensionSummary));
                return new JObject
                {
                    ["total"] = items.Count,
                    ["items"] = items,
                };
            }
        }

        public JObject? DescribeExtension(string extensionId)
        {
            try
            {
                extensionId = ReactorValidation.Identifier(extensionId, nameof(extensionId), 64, allowDots: true);
            }
            catch (ArgumentException)
            {
                return null;
            }
            lock (_sync)
            {
                return _extensions.TryGetValue(extensionId, out var state)
                    ? DescribeExtensionDetail(state.Descriptor, state.Definition, "menuIds")
                    : null;
            }
        }

        public JObject DescribeMenuSummaries(string? extensionId)
        {
            string? normalizedExtensionId = null;
            if (!string.IsNullOrWhiteSpace(extensionId))
            {
                try
                {
                    normalizedExtensionId = ReactorValidation.Identifier(
                        extensionId!, nameof(extensionId), 64, allowDots: true);
                }
                catch (ArgumentException)
                {
                    return new JObject
                    {
                        ["total"] = 0,
                        ["truncated"] = false,
                        ["items"] = new JArray(),
                    };
                }
            }

            lock (_sync)
            {
                var all = _extensions.Values
                    .Where(value => normalizedExtensionId == null ||
                        string.Equals(value.Descriptor.Id, normalizedExtensionId, StringComparison.OrdinalIgnoreCase))
                    .SelectMany(state => state.Definition.Menus.Values.Select(menu => new JObject
                    {
                        ["extensionId"] = state.Descriptor.Id,
                        ["id"] = menu.Id,
                        ["label"] = menu.Label,
                        ["order"] = menu.Order,
                        ["nodeCount"] = menu.AllNodes().Count(),
                    }))
                    .OrderBy(value => value.Value<int>("order"))
                    .ThenBy(value => value.Value<string>("extensionId"), StringComparer.Ordinal)
                    .ThenBy(value => value.Value<string>("id"), StringComparer.Ordinal)
                    .ToArray();
                var items = new JArray();
                var envelopeLength = new JObject
                {
                    ["total"] = all.Length,
                    ["truncated"] = false,
                    ["items"] = new JArray(),
                }.ToString(Formatting.None).Length;
                var serializedLength = envelopeLength;
                foreach (var item in all)
                {
                    var itemLength = item.ToString(Formatting.None).Length + (items.Count == 0 ? 0 : 1);
                    if (serializedLength + itemLength > ReactorExtensionLimits.MaximumTransportPayload)
                        break;
                    items.Add(item);
                    serializedLength += itemLength;
                }
                return new JObject
                {
                    ["total"] = all.Length,
                    ["truncated"] = items.Count < all.Length,
                    ["items"] = items,
                };
            }
        }

        public JArray DescribeMenus(string? extensionId, string? menuId)
        {
            lock (_sync)
            {
                IEnumerable<ReactorExtensionState> states = _extensions.Values;
                var normalizedExtensionId = string.IsNullOrWhiteSpace(extensionId) ? null : extensionId!.Trim();
                var normalizedMenuId = string.IsNullOrWhiteSpace(menuId) ? null : menuId!.Trim();
                if (normalizedExtensionId != null)
                    states = states.Where(value => string.Equals(
                        value.Descriptor.Id,
                        normalizedExtensionId,
                        StringComparison.OrdinalIgnoreCase));
                var menus = states.SelectMany(state => state.Definition.Menus.Values
                        .Where(menu => normalizedMenuId == null ||
                            string.Equals(menu.Id, normalizedMenuId, StringComparison.OrdinalIgnoreCase))
                        .Select(menu => menu.ToJson(state.Descriptor.Id)))
                    .OrderBy(value => value.Value<int>("order"))
                    .ThenBy(value => value.Value<string>("extensionId"), StringComparer.Ordinal)
                    .ThenBy(value => value.Value<string>("id"), StringComparer.Ordinal);
                return new JArray(menus);
            }
        }

        public bool TryResolveMenuAction(
            string extensionId,
            string menuId,
            string nodeId,
            out string? actionId)
        {
            actionId = null;
            try
            {
                extensionId = ReactorValidation.Identifier(extensionId, nameof(extensionId), 64, allowDots: true);
                menuId = ReactorValidation.Identifier(menuId, nameof(menuId), 64, allowDots: true);
                nodeId = ReactorValidation.Identifier(nodeId, nameof(nodeId), 64, allowDots: true);
            }
            catch (ArgumentException)
            {
                return false;
            }
            lock (_sync)
            {
                if (!_extensions.TryGetValue(extensionId, out var state) ||
                    !state.Definition.Menus.TryGetValue(menuId, out var menu))
                    return false;
                var node = menu.AllNodes().FirstOrDefault(value =>
                    string.Equals(value.Id, nodeId, StringComparison.OrdinalIgnoreCase));
                if (node == null || !node.Enabled || !node.Visible) return false;
                actionId = ReactorExtensionBuilder.ActionReference(node);
                return actionId != null && state.Definition.Actions.ContainsKey(actionId);
            }
        }

        public ReactorActionResult InvokeMenu(
            string extensionId,
            string menuId,
            string nodeId,
            string interaction,
            JObject parameters,
            bool confirmed,
            string? idempotencyKey)
        {
            if (parameters == null)
                return ReactorActionResult.Failure("invalid_params", "Menu action parameters must be an object.");

            try
            {
                extensionId = ReactorValidation.Identifier(extensionId, nameof(extensionId), 64, allowDots: true);
                menuId = ReactorValidation.Identifier(menuId, nameof(menuId), 64, allowDots: true);
                nodeId = ReactorValidation.Identifier(nodeId, nameof(nodeId), 64, allowDots: true);
            }
            catch (ArgumentException)
            {
                return ReactorActionResult.Failure("invalid_menu_invocation", "The extension, menu, or node id is invalid.");
            }

            interaction = (interaction ?? string.Empty).Trim().ToLowerInvariant();
            ReactorExtensionState state;
            ReactorMenuDescriptor menu;
            ReactorMenuNode? node;
            string? actionId;
            lock (_sync)
            {
                if (!_extensions.TryGetValue(extensionId, out state!) ||
                    !state.Definition.Menus.TryGetValue(menuId, out menu!))
                    return ReactorActionResult.Failure("menu_not_found", "The requested extension menu is not registered.");
                node = menu.AllNodes().FirstOrDefault(value =>
                    string.Equals(value.Id, nodeId, StringComparison.OrdinalIgnoreCase));
                if (node == null)
                    return ReactorActionResult.Failure("menu_node_not_found", "The requested menu node is not registered.");
                if (!node.Visible || !node.Enabled)
                    return ReactorActionResult.Failure("menu_node_unavailable", "The requested menu node is hidden or disabled.");
                if (!MenuInteractionAllowed(node, interaction))
                    return ReactorActionResult.Failure(
                        "menu_interaction_not_allowed",
                        $"Interaction '{interaction}' is not supported by menu node kind '{node.Kind.ToString().ToLowerInvariant()}'.");
                actionId = ReactorExtensionBuilder.ActionReference(node);
                if (actionId == null || !state.Definition.Actions.ContainsKey(actionId))
                    return ReactorActionResult.Failure("menu_action_not_found", "The requested menu node has no registered action.");
            }

            return Invoke(extensionId, actionId, parameters, confirmed, idempotencyKey);
        }

        private static bool MenuInteractionAllowed(ReactorMenuNode node, string interaction)
        {
            switch (node.Kind)
            {
                case ReactorMenuNodeKind.Action:
                    return interaction == "activate";
                case ReactorMenuNodeKind.Toggle:
                case ReactorMenuNodeKind.Choice:
                case ReactorMenuNodeKind.Range:
                case ReactorMenuNodeKind.Pagination:
                    return interaction == "activate" || interaction == "set-value" || interaction == "adjust";
                case ReactorMenuNodeKind.Text:
                case ReactorMenuNodeKind.Search:
                case ReactorMenuNodeKind.Keybind:
                    return interaction == "activate" || interaction == "set-value";
                default:
                    return false;
            }
        }

        public ReactorActionResult Invoke(
            string extensionId,
            string actionId,
            JObject parameters,
            bool confirmed,
            string? idempotencyKey)
        {
            if (parameters == null) return ReactorActionResult.Failure("invalid_params", "Action parameters must be an object.");
            ReactorExtensionState state;
            ReactorActionRegistration action;
            try
            {
                extensionId = ReactorValidation.Identifier(extensionId, nameof(extensionId), 64, allowDots: true);
                actionId = ReactorValidation.Identifier(actionId, nameof(actionId), 64, allowDots: true);
            }
            catch (ArgumentException)
            {
                return ReactorActionResult.Failure("invalid_action", "The extension or action id is invalid.");
            }

            lock (_sync)
            {
                if (!_extensions.TryGetValue(extensionId, out state!) ||
                    !state.Definition.Actions.TryGetValue(actionId, out action!))
                    return ReactorActionResult.Failure("action_not_found", "The requested extension action is not registered.");
            }

            var detachedParameters = (JObject)parameters.DeepClone();
            try
            {
                action.Descriptor.ValidateParameters(detachedParameters);
            }
            catch (ReactorValidationException error)
            {
                return ReactorActionResult.Failure(error.Code, error.Message);
            }

            if (action.Descriptor.RequiresConfirmation && !confirmed)
                return ReactorActionResult.RequireConfirmation();

            string? cacheKey = null;
            string? canonicalParameters = null;
            if (action.Descriptor.Risk == ReactorActionRisk.Persistent)
            {
                string normalizedKey;
                try
                {
                    normalizedKey = ReactorValidation.IdempotencyKey(idempotencyKey);
                }
                catch (ReactorValidationException error)
                {
                    return ReactorActionResult.Failure(error.Code, error.Message);
                }
                cacheKey = extensionId + "\0" + actionId + "\0" + normalizedKey;
                canonicalParameters = ReactorValidation.CanonicalJson(detachedParameters);
                lock (_sync)
                {
                    if (_idempotency.TryGetValue(cacheKey, out var previous))
                    {
                        if (!string.Equals(previous.Parameters, canonicalParameters, StringComparison.Ordinal))
                            return ReactorActionResult.Failure(
                                "idempotency_conflict",
                                "The idempotency key was already used with different parameters.");
                        return previous.Result == null
                            ? ReactorActionResult.Failure("action_in_progress", "This persistent action is already in progress.")
                            : previous.Result.AsReplay();
                    }
                    _idempotency.Add(cacheKey, new ReactorIdempotencyEntry(canonicalParameters));
                    _idempotencyOrder.Enqueue(cacheKey);
                    TrimIdempotencyLocked();
                }
            }

            ReactorActionResult result;
            try
            {
                var context = new ReactorActionContext(extensionId, actionId, confirmed, idempotencyKey);
                result = action.Handler(context, detachedParameters) ??
                    ReactorActionResult.Failure("invalid_result", "The extension returned no action result.");
                if (result.Succeeded)
                {
                    var resultJson = result.ToJson();
                    if (!ReactorValidation.IsWithinDepth(resultJson, ReactorExtensionLimits.MaximumPayloadDepth))
                        result = ReactorActionResult.Failure(
                            "result_too_deep",
                            "The action result exceeds the transport nesting limit.");
                    else if (resultJson.ToString(Formatting.None).Length > ReactorExtensionLimits.MaximumTransportPayload)
                        result = ReactorActionResult.Failure(
                            "result_too_large",
                            "The action result exceeds the transport-safe 60 KiB limit.");
                }
            }
            catch
            {
                result = ReactorActionResult.Failure("action_failed", "The extension action failed.");
            }

            if (cacheKey != null)
            {
                lock (_sync)
                {
                    if (_idempotency.TryGetValue(cacheKey, out var pending))
                    {
                        if (result.Succeeded &&
                            _extensions.TryGetValue(extensionId, out var current) &&
                            current.Generation == state.Generation)
                            pending.Result = result;
                        else
                            _idempotency.Remove(cacheKey);
                    }
                }
            }
            return result;
        }

        public bool TryPublish(Guid generation, string extensionId, string eventId, JToken? payload)
        {
            try
            {
                eventId = ReactorValidation.Identifier(eventId, nameof(eventId), 64, allowDots: true);
            }
            catch (ArgumentException)
            {
                return false;
            }
            var detachedPayload = payload?.DeepClone() ?? JValue.CreateNull();
            if (!ReactorValidation.IsWithinDepth(detachedPayload, ReactorExtensionLimits.MaximumPayloadDepth))
                return false;
            lock (_sync)
            {
                if (!_extensions.TryGetValue(extensionId, out var state) || state.Generation != generation ||
                    !state.Definition.Events.TryGetValue(eventId, out var descriptor) ||
                    _events.Count >= MaximumPendingEvents)
                    return false;
                var bytes = Encoding.UTF8.GetByteCount(detachedPayload.ToString(Formatting.None));
                if (bytes > descriptor.MaximumPayloadBytes) return false;
                _events.Enqueue(new ReactorEventRecord(++_eventSequence, state.Descriptor.Id, descriptor.Id, detachedPayload));
                return true;
            }
        }

        public JArray DrainEvents()
        {
            lock (_sync)
            {
                var result = new JArray();
                while (_events.Count > 0) result.Add(_events.Dequeue().ToJson());
                return result;
            }
        }

        public void NotifyLifecycle(ReactorLifecycleStage stage, JToken? payload)
        {
            ReactorExtensionState[] states;
            lock (_sync) states = _extensions.Values.OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal).ToArray();
            foreach (var state in states) InvokeLifecycle(state, stage, payload);
        }

        public void UpdateMenu(Guid generation, string extensionId, ReactorMenuDescriptor menu)
        {
            if (menu == null) throw new ArgumentNullException(nameof(menu));
            lock (_sync)
            {
                var state = RequireCurrentLocked(generation, extensionId);
                if (!state.Definition.Menus.ContainsKey(menu.Id))
                    throw new InvalidOperationException($"Menu '{menu.Id}' is not registered by extension '{extensionId}'.");
                var candidate = new Dictionary<string, ReactorMenuDescriptor>(state.Definition.Menus, StringComparer.OrdinalIgnoreCase)
                {
                    [menu.Id] = menu,
                };
                ReactorExtensionBuilder.ValidateMenuReferences(candidate, state.Definition.Actions);
                state.Definition.Menus[menu.Id] = menu;
            }
        }

        public bool RemoveMenu(Guid generation, string extensionId, string menuId)
        {
            menuId = ReactorValidation.Identifier(menuId, nameof(menuId), 64, allowDots: true);
            lock (_sync)
            {
                var state = RequireCurrentLocked(generation, extensionId);
                if (!state.Definition.Menus.ContainsKey(menuId)) return false;
                var candidate = new Dictionary<string, ReactorMenuDescriptor>(state.Definition.Menus, StringComparer.OrdinalIgnoreCase);
                candidate.Remove(menuId);
                // Reject removal while another menu still points to this route.
                ReactorExtensionBuilder.ValidateMenuReferences(candidate, state.Definition.Actions);
                state.Definition.Menus.Remove(menuId);
                return true;
            }
        }

        public void Unregister(Guid generation, string extensionId)
        {
            ReactorExtensionState? removed = null;
            lock (_sync)
            {
                if (_extensions.TryGetValue(extensionId, out var state) && state.Generation == generation)
                {
                    removed = state;
                    _extensions.Remove(extensionId);
                    RemoveExtensionStateLocked(extensionId);
                }
            }
            if (removed != null) InvokeLifecycle(removed, ReactorLifecycleStage.Unloading, null);
        }

        public void Reset()
        {
            ReactorExtensionState[] removed;
            lock (_sync)
            {
                removed = _extensions.Values.ToArray();
                _extensions.Clear();
                _events.Clear();
                _idempotency.Clear();
                _idempotencyOrder.Clear();
                _eventSequence = 0;
            }
            foreach (var state in removed) InvokeLifecycle(state, ReactorLifecycleStage.Unloading, null);
        }

        private ReactorExtensionState RequireCurrentLocked(Guid generation, string extensionId)
        {
            if (!_extensions.TryGetValue(extensionId, out var state) || state.Generation != generation)
                throw new ObjectDisposedException(nameof(IReactorExtensionHandle));
            return state;
        }

        private void RemoveExtensionStateLocked(string extensionId)
        {
            if (_events.Count > 0)
            {
                var retained = _events.Where(value => !string.Equals(value.ExtensionId, extensionId, StringComparison.OrdinalIgnoreCase)).ToArray();
                _events.Clear();
                foreach (var value in retained) _events.Enqueue(value);
            }
            foreach (var key in _idempotency.Keys
                .Where(value => value.StartsWith(extensionId + "\0", StringComparison.OrdinalIgnoreCase))
                .ToArray())
                _idempotency.Remove(key);
        }

        private void TrimIdempotencyLocked()
        {
            while (_idempotency.Count > MaximumIdempotencyEntries && _idempotencyOrder.Count > 0)
            {
                var oldest = _idempotencyOrder.Dequeue();
                _idempotency.Remove(oldest);
            }
            while (_idempotencyOrder.Count > MaximumIdempotencyEntries * 2)
                _idempotencyOrder.Dequeue();
        }

        private static JObject DescribeExtensionSummary(ReactorExtensionState state) => new JObject
        {
            ["id"] = state.Descriptor.Id,
            ["name"] = state.Descriptor.Name,
            ["version"] = state.Descriptor.Version,
            ["extensionApiVersion"] = ReactorApi.ExtensionApiVersion,
            ["actionCount"] = state.Definition.Actions.Count,
            ["eventCount"] = state.Definition.Events.Count,
            ["menuCount"] = state.Definition.Menus.Count,
        };

        private static JObject DescribeExtensionDetail(
            ReactorExtensionDescriptor descriptor,
            ReactorExtensionDefinition definition,
            string menuIdsProperty)
        {
            var result = descriptor.ToJson();
            result["extensionApiVersion"] = ReactorApi.ExtensionApiVersion;
            result["actions"] = new JArray(definition.Actions.Values
                .OrderBy(value => value.Descriptor.Id, StringComparer.Ordinal)
                .Select(value => value.Descriptor.ToJson()));
            result["events"] = new JArray(definition.Events.Values
                .OrderBy(value => value.Id, StringComparer.Ordinal)
                .Select(value => value.ToJson()));
            result[menuIdsProperty] = new JArray(
                definition.Menus.Keys.OrderBy(value => value, StringComparer.Ordinal));
            return result;
        }

        private static bool FitsTransport(JToken value) =>
            ReactorValidation.IsWithinDepth(value, ReactorExtensionLimits.MaximumPayloadDepth) &&
            value.ToString(Formatting.None).Length <= ReactorExtensionLimits.MaximumTransportPayload;

        private static void InvokeLifecycle(
            ReactorExtensionState state,
            ReactorLifecycleStage stage,
            JToken? payload)
        {
            try
            {
                state.Definition.Lifecycle?.OnLifecycle(
                    new ReactorLifecycleContext(state.Descriptor.Id, stage, payload));
            }
            catch
            {
                // One extension must never interrupt lifecycle delivery to others.
            }
        }
    }

    internal sealed class ReactorExtensionHandle : IReactorExtensionHandle
    {
        private readonly ReactorRegistry _registry;
        private readonly Guid _generation;
        private int _disposed;

        public ReactorExtensionHandle(
            ReactorRegistry registry,
            ReactorExtensionDescriptor descriptor,
            Guid generation)
        {
            _registry = registry;
            Descriptor = descriptor;
            _generation = generation;
        }

        public ReactorExtensionDescriptor Descriptor { get; }

        public void UpdateMenu(ReactorMenuDescriptor menu)
        {
            ThrowIfDisposed();
            _registry.UpdateMenu(_generation, Descriptor.Id, menu);
        }

        public bool RemoveMenu(string menuId)
        {
            ThrowIfDisposed();
            return _registry.RemoveMenu(_generation, Descriptor.Id, menuId);
        }

        public bool TryPublishEvent(string eventId, JToken? payload)
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0) return false;
            return _registry.TryPublish(_generation, Descriptor.Id, eventId, payload);
        }

        public void Dispose()
        {
            if (System.Threading.Interlocked.Exchange(ref _disposed, 1) != 0) return;
            _registry.Unregister(_generation, Descriptor.Id);
        }

        private void ThrowIfDisposed()
        {
            if (System.Threading.Interlocked.CompareExchange(ref _disposed, 0, 0) != 0)
                throw new ObjectDisposedException(nameof(ReactorExtensionHandle));
        }
    }
}
