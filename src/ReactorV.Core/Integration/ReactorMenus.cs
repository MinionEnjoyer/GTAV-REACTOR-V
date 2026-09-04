using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace ReactorV.Integration
{
    public enum ReactorMenuNodeKind
    {
        Action,
        Toggle,
        Choice,
        Range,
        Text,
        Search,
        Keybind,
        Tabs,
        List,
        Grid,
        Media,
        Status,
        Progress,
        Pagination,
        Separator,
        Submenu,
    }

    public abstract class ReactorMenuNode
    {
        private JObject _boundParameters = new JObject();

        protected ReactorMenuNode(
            string id,
            ReactorMenuNodeKind kind,
            string label = "",
            string description = "",
            bool enabled = true,
            bool visible = true)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Kind = kind;
            Label = ReactorValidation.Text(label, nameof(label), 128, required: kind != ReactorMenuNodeKind.Separator);
            Description = ReactorValidation.Text(description, nameof(description), 512, required: false);
            Enabled = enabled;
            Visible = visible;
        }

        public string Id { get; }
        public ReactorMenuNodeKind Kind { get; }
        public string Label { get; }
        public string Description { get; }
        public bool Enabled { get; }
        public bool Visible { get; }

        /// <summary>
        /// Parameters owned by the extension and attached to this node's action.
        /// A detached copy is returned so callers cannot mutate a registered menu.
        /// Browser-provided values may supplement, but never replace, these values.
        /// </summary>
        public JObject BoundParameters => (JObject)_boundParameters.DeepClone();

        internal JObject ToJson()
        {
            var result = new JObject
            {
                ["id"] = Id,
                ["kind"] = Kind.ToString().ToLowerInvariant(),
                ["label"] = Label,
                ["description"] = Description,
                ["enabled"] = Enabled,
                ["visible"] = Visible,
            };
            if (_boundParameters.Count > 0)
                result["boundParameters"] = _boundParameters.DeepClone();
            WriteJson(result);
            return result;
        }

        internal virtual IEnumerable<ReactorMenuNode> Children => Array.Empty<ReactorMenuNode>();
        internal abstract void WriteJson(JObject target);

        protected static string ActionId(string value) =>
            ReactorValidation.Identifier(value, nameof(value), 64, allowDots: true);

        protected void SetBoundParameters(JObject? value)
        {
            var detached = value == null ? new JObject() : (JObject)value.DeepClone();
            if (!ReactorValidation.IsWithinDepth(detached, ReactorExtensionLimits.MaximumPayloadDepth))
                throw new ArgumentException("Bound parameters exceed the transport nesting limit.", nameof(value));
            if (Encoding.UTF8.GetByteCount(detached.ToString(Formatting.None)) >
                ReactorExtensionLimits.MaximumTransportPayload)
                throw new ArgumentException("Bound parameters exceed the transport-safe 60 KiB limit.", nameof(value));
            _boundParameters = detached;
        }

        internal JObject BoundParametersSnapshot() => (JObject)_boundParameters.DeepClone();

        internal static IReadOnlyList<ReactorMenuNode> Nodes(IEnumerable<ReactorMenuNode> values)
        {
            var result = (values ?? throw new ArgumentNullException(nameof(values))).ToArray();
            if (result.Length == 0 || result.Length > 256 || result.Any(value => value == null))
                throw new ArgumentException("A menu container requires 1-256 non-null nodes.", nameof(values));
            if (result.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != result.Length)
                throw new ArgumentException("Sibling menu node ids must be unique.", nameof(values));
            return result;
        }
    }

    public sealed class ReactorActionNode : ReactorMenuNode
    {
        public ReactorActionNode(
            string id,
            string label,
            string actionId,
            string description = "",
            bool enabled = true,
            bool visible = true)
            : this(id, label, actionId, description, enabled, visible, null) { }

        public ReactorActionNode(
            string id,
            string label,
            string actionId,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Action, label, description, enabled, visible)
        {
            Action = ActionId(actionId);
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        internal override void WriteJson(JObject target) => target["actionId"] = Action;
    }

    public sealed class ReactorToggleNode : ReactorMenuNode
    {
        public ReactorToggleNode(
            string id,
            string label,
            string actionId,
            bool value,
            string description = "",
            bool enabled = true,
            bool visible = true)
            : this(id, label, actionId, value, description, enabled, visible, null) { }

        public ReactorToggleNode(
            string id,
            string label,
            string actionId,
            bool value,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Toggle, label, description, enabled, visible)
        {
            Action = ActionId(actionId);
            Value = value;
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public bool Value { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["value"] = Value;
        }
    }

    public sealed class ReactorChoiceOption
    {
        public ReactorChoiceOption(string id, string label)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Label = ReactorValidation.Text(label, nameof(label), 128, required: true);
        }
        public string Id { get; }
        public string Label { get; }
        internal JObject ToJson() => new JObject { ["id"] = Id, ["label"] = Label };
    }

    public sealed class ReactorChoiceNode : ReactorMenuNode
    {
        public ReactorChoiceNode(
            string id,
            string label,
            string actionId,
            IEnumerable<ReactorChoiceOption> options,
            string selectedId,
            string description = "",
            bool enabled = true,
            bool visible = true)
            : this(id, label, actionId, options, selectedId, description, enabled, visible, null) { }

        public ReactorChoiceNode(
            string id,
            string label,
            string actionId,
            IEnumerable<ReactorChoiceOption> options,
            string selectedId,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Choice, label, description, enabled, visible)
        {
            Action = ActionId(actionId);
            var values = (options ?? throw new ArgumentNullException(nameof(options))).ToArray();
            if (values.Length == 0 || values.Length > 128 || values.Any(value => value == null) ||
                values.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
                throw new ArgumentException("Choice options must contain 1-128 unique entries.", nameof(options));
            SelectedId = ReactorValidation.Identifier(selectedId, nameof(selectedId), 64, allowDots: true);
            if (!values.Any(value => string.Equals(value.Id, SelectedId, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected choice must exist in options.", nameof(selectedId));
            Options = values;
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public IReadOnlyList<ReactorChoiceOption> Options { get; }
        public string SelectedId { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["selectedId"] = SelectedId;
            target["options"] = new JArray(Options.Select(value => value.ToJson()));
        }
    }

    public sealed class ReactorRangeNode : ReactorMenuNode
    {
        public ReactorRangeNode(
            string id,
            string label,
            string actionId,
            double value,
            double minimum,
            double maximum,
            double step,
            string description = "",
            bool enabled = true,
            bool visible = true)
            : this(id, label, actionId, value, minimum, maximum, step, description, enabled, visible, null) { }

        public ReactorRangeNode(
            string id,
            string label,
            string actionId,
            double value,
            double minimum,
            double maximum,
            double step,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Range, label, description, enabled, visible)
        {
            if (double.IsNaN(value) || double.IsNaN(minimum) || double.IsNaN(maximum) || double.IsNaN(step) ||
                double.IsInfinity(value) || double.IsInfinity(minimum) || double.IsInfinity(maximum) || double.IsInfinity(step) ||
                maximum < minimum || step <= 0 || value < minimum || value > maximum)
                throw new ArgumentException("Range values are invalid.", nameof(value));
            Action = ActionId(actionId);
            Value = value;
            Minimum = minimum;
            Maximum = maximum;
            Step = step;
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public double Value { get; }
        public double Minimum { get; }
        public double Maximum { get; }
        public double Step { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["value"] = Value;
            target["minimum"] = Minimum;
            target["maximum"] = Maximum;
            target["step"] = Step;
        }
    }

    public abstract class ReactorInputNode : ReactorMenuNode
    {
        protected ReactorInputNode(
            string id,
            ReactorMenuNodeKind kind,
            string label,
            string actionId,
            string value,
            string placeholder,
            int maximumLength,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, kind, label, description, enabled, visible)
        {
            Action = ActionId(actionId);
            if (maximumLength < 1 || maximumLength > 16_384) throw new ArgumentOutOfRangeException(nameof(maximumLength));
            Value = ReactorValidation.Text(value, nameof(value), maximumLength, required: false);
            Placeholder = ReactorValidation.Text(placeholder, nameof(placeholder), 256, required: false);
            MaximumLength = maximumLength;
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public string Value { get; }
        public string Placeholder { get; }
        public int MaximumLength { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["value"] = Value;
            target["placeholder"] = Placeholder;
            target["maximumLength"] = MaximumLength;
        }
    }

    public sealed class ReactorTextNode : ReactorInputNode
    {
        public ReactorTextNode(string id, string label, string actionId, string value = "", string placeholder = "", int maximumLength = 256, string description = "", bool enabled = true, bool visible = true)
            : this(id, label, actionId, value, placeholder, maximumLength, description, enabled, visible, null) { }

        public ReactorTextNode(string id, string label, string actionId, string value, string placeholder, int maximumLength, string description, bool enabled, bool visible, JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Text, label, actionId, value, placeholder, maximumLength, description, enabled, visible, boundParameters) { }
    }

    public sealed class ReactorSearchNode : ReactorInputNode
    {
        public ReactorSearchNode(string id, string label, string actionId, string value = "", string placeholder = "Search", int maximumLength = 256, string description = "", bool enabled = true, bool visible = true)
            : this(id, label, actionId, value, placeholder, maximumLength, description, enabled, visible, null) { }

        public ReactorSearchNode(string id, string label, string actionId, string value, string placeholder, int maximumLength, string description, bool enabled, bool visible, JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Search, label, actionId, value, placeholder, maximumLength, description, enabled, visible, boundParameters) { }
    }

    public sealed class ReactorKeybindNode : ReactorMenuNode
    {
        public ReactorKeybindNode(string id, string label, string actionId, string binding, string description = "", bool enabled = true, bool visible = true)
            : this(id, label, actionId, binding, description, enabled, visible, null) { }

        public ReactorKeybindNode(string id, string label, string actionId, string binding, string description, bool enabled, bool visible, JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Keybind, label, description, enabled, visible)
        {
            Action = ActionId(actionId);
            Binding = ReactorValidation.Text(binding, nameof(binding), 64, required: false);
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public string Binding { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["binding"] = Binding;
        }
    }

    public sealed class ReactorMenuTab
    {
        public ReactorMenuTab(string id, string label, IEnumerable<ReactorMenuNode> nodes)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Label = ReactorValidation.Text(label, nameof(label), 128, required: true);
            Nodes = ReactorMenuNode.Nodes(nodes);
        }
        public string Id { get; }
        public string Label { get; }
        public IReadOnlyList<ReactorMenuNode> Nodes { get; }
        internal JObject ToJson() => new JObject
        {
            ["id"] = Id,
            ["label"] = Label,
            ["nodes"] = new JArray(Nodes.Select(value => value.ToJson())),
        };
    }

    public sealed class ReactorTabsNode : ReactorMenuNode
    {
        public ReactorTabsNode(string id, string label, IEnumerable<ReactorMenuTab> tabs, string selectedId, string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Tabs, label, description, enabled, visible)
        {
            var values = (tabs ?? throw new ArgumentNullException(nameof(tabs))).ToArray();
            if (values.Length == 0 || values.Length > 32 || values.Any(value => value == null) ||
                values.Select(value => value.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count() != values.Length)
                throw new ArgumentException("Tabs require 1-32 unique entries.", nameof(tabs));
            SelectedId = ReactorValidation.Identifier(selectedId, nameof(selectedId), 64, allowDots: true);
            if (!values.Any(value => string.Equals(value.Id, SelectedId, StringComparison.OrdinalIgnoreCase)))
                throw new ArgumentException("The selected tab must exist.", nameof(selectedId));
            Tabs = values;
        }
        public IReadOnlyList<ReactorMenuTab> Tabs { get; }
        public string SelectedId { get; }
        internal override IEnumerable<ReactorMenuNode> Children => Tabs.SelectMany(value => value.Nodes);
        internal override void WriteJson(JObject target)
        {
            target["selectedId"] = SelectedId;
            target["tabs"] = new JArray(Tabs.Select(value => value.ToJson()));
        }
    }

    public sealed class ReactorListNode : ReactorMenuNode
    {
        public ReactorListNode(string id, string label, IEnumerable<ReactorMenuNode> nodes, string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.List, label, description, enabled, visible) => Items = Nodes(nodes);
        public IReadOnlyList<ReactorMenuNode> Items { get; }
        internal override IEnumerable<ReactorMenuNode> Children => Items;
        internal override void WriteJson(JObject target) => target["nodes"] = new JArray(Items.Select(value => value.ToJson()));
    }

    public sealed class ReactorGridNode : ReactorMenuNode
    {
        public ReactorGridNode(string id, string label, IEnumerable<ReactorMenuNode> nodes, int columns = 3, string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Grid, label, description, enabled, visible)
        {
            if (columns < 1 || columns > 12) throw new ArgumentOutOfRangeException(nameof(columns));
            Items = Nodes(nodes);
            Columns = columns;
        }
        public IReadOnlyList<ReactorMenuNode> Items { get; }
        public int Columns { get; }
        internal override IEnumerable<ReactorMenuNode> Children => Items;
        internal override void WriteJson(JObject target)
        {
            target["columns"] = Columns;
            target["nodes"] = new JArray(Items.Select(value => value.ToJson()));
        }
    }

    public sealed class ReactorMediaNode : ReactorMenuNode
    {
        public ReactorMediaNode(string id, string label, string source, string mediaType = "image", string alternativeText = "", string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Media, label, description, enabled, visible)
        {
            Source = ReactorValidation.Text(source, nameof(source), 512, required: true);
            MediaType = ReactorValidation.Identifier(mediaType, nameof(mediaType), 32, allowDots: false);
            AlternativeText = ReactorValidation.Text(alternativeText, nameof(alternativeText), 256, required: false);
        }
        public string Source { get; }
        public string MediaType { get; }
        public string AlternativeText { get; }
        internal override void WriteJson(JObject target)
        {
            target["source"] = Source;
            target["mediaType"] = MediaType;
            target["alternativeText"] = AlternativeText;
        }
    }

    public sealed class ReactorStatusNode : ReactorMenuNode
    {
        public ReactorStatusNode(string id, string label, string value, string tone = "neutral", string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Status, label, description, enabled, visible)
        {
            Value = ReactorValidation.Text(value, nameof(value), 256, required: true);
            Tone = ReactorValidation.Identifier(tone, nameof(tone), 32, allowDots: false);
        }
        public string Value { get; }
        public string Tone { get; }
        internal override void WriteJson(JObject target)
        {
            target["value"] = Value;
            target["tone"] = Tone;
        }
    }

    public sealed class ReactorProgressNode : ReactorMenuNode
    {
        public ReactorProgressNode(string id, string label, double value = 0, bool indeterminate = false, string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Progress, label, description, enabled, visible)
        {
            if (!indeterminate && (double.IsNaN(value) || double.IsInfinity(value) || value < 0 || value > 1))
                throw new ArgumentOutOfRangeException(nameof(value));
            Value = value;
            Indeterminate = indeterminate;
        }
        public double Value { get; }
        public bool Indeterminate { get; }
        internal override void WriteJson(JObject target)
        {
            target["value"] = Value;
            target["indeterminate"] = Indeterminate;
        }
    }

    public sealed class ReactorPaginationNode : ReactorMenuNode
    {
        public ReactorPaginationNode(
            string id,
            string label,
            string actionId,
            int page,
            int pageCount,
            string description = "",
            bool enabled = true,
            bool visible = true)
            : this(id, label, actionId, page, pageCount, description, enabled, visible, null) { }

        public ReactorPaginationNode(
            string id,
            string label,
            string actionId,
            int page,
            int pageCount,
            string description,
            bool enabled,
            bool visible,
            JObject? boundParameters)
            : base(id, ReactorMenuNodeKind.Pagination, label, description, enabled, visible)
        {
            if (pageCount < 1 || pageCount > 100_000)
                throw new ArgumentOutOfRangeException(nameof(pageCount));
            if (page < 1 || page > pageCount)
                throw new ArgumentOutOfRangeException(nameof(page));
            Action = ActionId(actionId);
            Page = page;
            PageCount = pageCount;
            SetBoundParameters(boundParameters);
        }
        public string Action { get; }
        public int Page { get; }
        public int PageCount { get; }
        internal override void WriteJson(JObject target)
        {
            target["actionId"] = Action;
            target["page"] = Page;
            target["pageCount"] = PageCount;
        }
    }

    public sealed class ReactorSeparatorNode : ReactorMenuNode
    {
        public ReactorSeparatorNode(string id, string label = "") : base(id, ReactorMenuNodeKind.Separator, label) { }
        internal override void WriteJson(JObject target) { }
    }

    public sealed class ReactorSubmenuNode : ReactorMenuNode
    {
        public ReactorSubmenuNode(string id, string label, string menuId, string description = "", bool enabled = true, bool visible = true)
            : base(id, ReactorMenuNodeKind.Submenu, label, description, enabled, visible) => MenuId = ReactorValidation.Identifier(menuId, nameof(menuId), 64, allowDots: true);
        public string MenuId { get; }
        internal override void WriteJson(JObject target) => target["menuId"] = MenuId;
    }

    public sealed class ReactorMenuDescriptor
    {
        public ReactorMenuDescriptor(
            string id,
            string label,
            IEnumerable<ReactorMenuNode> nodes,
            string description = "",
            string icon = "",
            int order = 100)
        {
            Id = ReactorValidation.Identifier(id, nameof(id), 64, allowDots: true);
            Label = ReactorValidation.Text(label, nameof(label), 128, required: true);
            Description = ReactorValidation.Text(description, nameof(description), 512, required: false);
            Icon = ReactorValidation.Text(icon, nameof(icon), 256, required: false);
            if (order < -10_000 || order > 10_000) throw new ArgumentOutOfRangeException(nameof(order));
            Order = order;
            Nodes = ReactorMenuNode.Nodes(nodes);
            ValidateTree();
        }

        public string Id { get; }
        public string Label { get; }
        public string Description { get; }
        public string Icon { get; }
        public int Order { get; }
        public IReadOnlyList<ReactorMenuNode> Nodes { get; }

        internal JObject ToJson(string extensionId) => new JObject
        {
            ["extensionId"] = extensionId,
            ["id"] = Id,
            ["label"] = Label,
            ["description"] = Description,
            ["icon"] = Icon,
            ["order"] = Order,
            ["nodes"] = new JArray(Nodes.Select(value => value.ToJson())),
        };

        internal IEnumerable<ReactorMenuNode> AllNodes()
        {
            var pending = new Stack<ReactorMenuNode>(Nodes.Reverse());
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                yield return current;
                foreach (var child in current.Children.Reverse()) pending.Push(child);
            }
        }

        private void ValidateTree()
        {
            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var count = 0;
            var pending = new Stack<Tuple<ReactorMenuNode, int>>(
                Nodes.Reverse().Select(value => Tuple.Create(value, 1)));
            while (pending.Count > 0)
            {
                var entry = pending.Pop();
                if (entry.Item2 > 8) throw new ArgumentException("Menu nesting may not exceed eight levels.", nameof(Nodes));
                if (!ids.Add(entry.Item1.Id)) throw new ArgumentException("Menu node ids must be unique across a menu.", nameof(Nodes));
                if (++count > 512) throw new ArgumentException("A menu may contain at most 512 nodes.", nameof(Nodes));
                foreach (var child in entry.Item1.Children.Reverse()) pending.Push(Tuple.Create(child, entry.Item2 + 1));
            }
            if (Encoding.UTF8.GetByteCount(ToJson("validation").ToString(Formatting.None)) >
                ReactorExtensionLimits.MaximumTransportPayload)
                throw new ArgumentException("The serialized menu exceeds the transport-safe 60 KiB limit.", nameof(Nodes));
        }
    }
}
